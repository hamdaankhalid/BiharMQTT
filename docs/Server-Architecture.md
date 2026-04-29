# BiharMQTT Server — Architecture Guide

How the broker is laid out, and the load-bearing concurrency invariants you
need to know before changing anything in the hot path.

---

## High-Level Layout

```
┌───────────────────────────────────────────────────────────────┐
│                         User Code                             │
│  MqttServerFactory.CreateMqttServer(options, logger)          │
└──────────────────────────┬────────────────────────────────────┘
                           ▼
┌───────────────────────────────────────────────────────────────┐
│                        MqttServer                             │
│  Owns: MqttClientSessionsManager, MqttRetainedMessagesManager │
│        MqttServerKeepAliveMonitor, MqttSenderPool             │
│        HugeNativeMemoryPool, MqttTcpServerAdapter             │
└──────────────────────────┬────────────────────────────────────┘
                           ▼
┌───────────────────────────────────────────────────────────────┐
│                  MqttTcpServerAdapter                         │
│  one MqttTcpServerListener per endpoint (plain / TLS)         │
│  per accepted socket → MqttTcpChannel → MqttChannelAdapter    │
└──────────────────────────┬────────────────────────────────────┘
                           ▼
┌───────────────────────────────────────────────────────────────┐
│              MqttClientSessionsManager                        │
│  ┌──────────────────┐    ┌──────────────────────────────┐     │
│  │ MqttConnected    │◀──▶│  MqttSession                 │     │
│  │  Client          │    │   - MqttPacketBus (3 prio)   │     │
│  │  - receive SM    │    │   - subscriptions manager    │     │
│  │  - schedule flag │    │   - encoder + lock           │     │
│  └────────┬─────────┘    └──────────────────────────────┘     │
│           │ ready-queue entry                                 │
│           ▼                                                   │
│   ┌──────────────────────────────────────────────────────┐    │
│   │          MqttSenderPool (M threads)                  │    │
│   │   ConcurrentQueue<MqttConnectedClient> + Semaphore   │    │
│   │   each worker pops, drains a session, re-arms        │    │
│   └──────────────────────────────────────────────────────┘    │
└───────────────────────────────────────────────────────────────┘
```

Plain TCP only. No ASP.NET Core integration, no WebSocket adapter, no
generic `IMqttServerAdapter` / `IMqttChannelAdapter` interfaces — just the
concrete classes. TLS is supported on the same `MqttChannelAdapter` via
`SslStream` wrapping the `Socket`.

---

## 1. Server Startup

```
MqttServerFactory.CreateMqttServer(options, logger)
  └─ new MqttServer(options, MqttTcpServerAdapter, logger)
        ├─ allocates HugeNativeMemoryPool
        │     bucket: 768 MB worth of native memory for per-session bus
        │     storage (Constants.MaxConcurrentConnections × 3 partitions)
        ├─ MqttRetainedMessagesManager
        ├─ MqttSenderPool(options.SenderThreadCount)
        │     spawns M dedicated background threads
        └─ MqttClientSessionsManager(options, ..., senderPool)

server.StartAsync():
  ├─ retainedMessagesManager.Start()
  ├─ clientSessionsManager.Start()
  ├─ keepAliveMonitor.Start()
  └─ tcpServerAdapter.StartAsync(options, OnHandleClient)
        └─ binds plain endpoint (default 1883), optional TLS endpoint (8883)
        └─ for each accept, calls OnHandleClient(channelAdapter, ct)
```

`MqttSenderPool` is created once at server boot. Worker threads live for
the entire process lifetime and are shared across **all** connections.

---

## 2. Connection Handshake

```
MqttServer.OnHandleClient(adapter, ct)
  └─ MqttClientSessionsManager.HandleClientConnectionAsync(adapter, ct)
        ├─ 1. ReceiveConnectPacket — task-based, with a CommunicationTimeout
        │       (uses MqttChannelAdapter.ReceivePacketAsync TCS wrapper —
        │        the only place async-await touches receive)
        ├─ 2. ValidateConnection — assigns ClientId if empty (v5 only)
        ├─ 3. If reason != Success → encode CONNACK fail, sync send, return
        ├─ 4. CreateClientConnection
        │       ├─ creates / reuses MqttSession (CleanStart vs persistent)
        │       ├─ if old client with same ClientId → IsTakenOver = true,
        │       │   StopAsync on old client (still owns its SAEA until detach)
        │       └─ new MqttConnectedClient(packet, adapter, session, ...,
        │                                  sessionsManager, senderPool, logger)
        ├─ 5. Encode CONNACK success, sync send via adapter.SendPacket
        └─ 6. await connectedClient.RunAsync()    ← lifetime task
```

The handshake is the only path that uses the `Task<ReceivedMqttPacket>`
shape. Once `RunAsync` is entered, every subsequent receive is callback-driven.

---

## 3. Receive Path — SAEA + Callback State Machine

The receive side is fully callback-driven. Zero `async`/`await`, zero
`Task` allocations per packet, zero state-machine boxes.

### 3.1 `MqttTcpChannel`

One persistent `SocketAsyncEventArgs` per channel, allocated in the
constructor. The `Completed` event is hooked once.

```
MqttTcpChannel.BeginReceive(buffer, ReceiveCompletionHandler cb)
  ├─ TLS path: SslStream.ReadAsync(buffer) → ContinueWith → cb
  └─ non-TLS:
       _recvCallback = cb
       _recvSaea.SetBuffer(buffer)
       pending = _socket.ReceiveAsync(_recvSaea)
       if !pending: invoke cb inline (depth-guarded — see §3.3)
       else: SAEA.Completed fires → cb on I/O thread
```

The **delegate is cached on the channel**, not allocated per call. The
caller (channel adapter) also caches its callbacks as instance fields.
Steady-state per-receive allocation: zero.

### 3.2 `MqttChannelAdapter` — packet state machine

The adapter walks the MQTT framing as a state machine driven by
`BeginReceive` callbacks, transitions:

```
Idle
  │ BeginReceivePacket(callback) sets _packetCallback, resets state
  ▼
ReadingFixedHeader (2 bytes into _fixedHeaderBuffer)
  │ if (byte[1] & 0x80) == 0: bodyLength = byte[1] → ProceedToBody
  │ else: stash low 7 bits → ReadingRemainingLength
  ▼
ReadingRemainingLength (1 byte at a time, up to 3 continuation bytes)
  │ accumulate value, multiplier *= 128
  │ when high bit clears: bodyLength = value → ProceedToBody
  ▼
ReadingBody (chunked into _reusableBodyBuffer, grown via ArrayPool)
  │ buffer is bounded by remaining-this-packet, NOT buffer capacity,
  │ so we don't over-read into the next packet
  ▼
DeliverPacket → invokes _packetCallback(packet, null)
```

State lives in instance fields (`_fhTotalRead`, `_rlValue`, `_rlOffset`,
`_bodyOffset`, `_flags`, `_bodyLength`, …). Cached delegates
(`_onFixedHeaderRead`, `_onRemainingLengthRead`, `_onBodyRead`) avoid
closure allocation when arming the next read.

### 3.3 Stack-overflow guard

Loopback / pre-buffered streams routinely sync-complete every
`Socket.ReceiveAsync`. Recursing the callback chain unboundedly would
blow the stack on large packets:

```csharp
[ThreadStatic] static int _syncCompletionDepth;
const int MaxSyncCompletionChain = 16;

static void ContinueOrPunt(Action next) {
    if (_syncCompletionDepth < MaxSyncCompletionChain) {
        _syncCompletionDepth++;
        try { next(); } finally { _syncCompletionDepth--; }
    } else {
        ThreadPool.UnsafeQueueUserWorkItem(static s => ((Action)s)(), next);
    }
}
```

We recurse on the calling thread for cache locality, then hand off to the
thread pool every 16 levels to unwind the stack.

### 3.4 `MqttConnectedClient.OnPacketReceived`

The packet callback decodes and dispatches synchronously in a switch.
Every handler (`HandleIncomingPublishPacket`, `HandleIncomingSubscribePacket`,
`HandleIncomingUnsubscribePacket`, …) is `void`-returning, no `Task`. After
a handler returns, the client calls `BeginReceiveNext()` to re-arm the
channel for the next packet.

`RunAsync` returns a `Task` backed by a `TaskCompletionSource`. The TCS
resolves only when the receive chain terminates (peer close, server-side
StopAsync, takeover, error) — `FinishRun` is idempotent and consolidates
the will-message + cleanup logic that used to live in `RunAsync`'s
`finally`.

---

## 4. Send Path — Sender Pool + Multiplexing

### 4.1 Why a pool

One thread per connection (long-running send loop) burns ~1 MB stack each;
1000 clients = 1 GB. The pool services N clients on M threads
(`Environment.ProcessorCount` by default), with a fairness cap so one
chatty client can't starve the rest.

### 4.2 `MqttSenderPool`

```
ConcurrentQueue<MqttConnectedClient> _readyQueue
SemaphoreSlim _signal (one slot per scheduled client)
Thread[] _workers (IsBackground = true, named BiharMQTT-Sender-{i})

worker:
    while (!ct):
        _signal.Wait(ct)
        client = _readyQueue.TryDequeue()         // skip if empty
        DrainAndSend(client, perThreadBuffer, ct) // up to FairnessCap=32
        client.ResetScheduledFlag()                // 1 → 0
        if (client.Session.HasPendingPackets):
            if (client.TrySetScheduledFlag()):     // CAS 0 → 1
                _readyQueue.Enqueue(client)
                _signal.Release()
```

Each worker owns one `byte[256KB]` buffer rented from `ArrayPool`. All
clients this worker services share that one buffer (one packet at a time
per worker).

### 4.3 The schedule-flag protocol

```
producer (Session enqueue) wants to wake sender:
    if (Interlocked.CompareExchange(ref _isScheduledForSend, 1, 0) == 0):
        senderPool.Schedule(this)  // enqueue + Release semaphore

worker:
    pop client                       // flag is 1
    DrainAndSend                     // flag stays 1 → producers don't re-enqueue
    Volatile.Write(flag, 0)          // release the slot
    if HasPendingPackets:
        if CAS 0→1 succeeds: re-enqueue
```

**Critical invariant:** the flag stays at 1 for the *entire* drain. If we
cleared it before draining, a producer arriving mid-drain would CAS 0→1
and re-enqueue this client, allowing a *second* worker to pop and drain
the same session concurrently — corrupting `MqttPacketBus._activePartition`
and the `PreAllocatedQ` indices. This was a real bug caught during the
e2e bombard test.

After the post-drain reset:
- If a producer arrived during the drain, it sat behind the flag with its
  bytes already in the bus. Our `HasPendingPackets` re-check + CAS 0→1
  re-enqueues us to drain those bytes.
- If a producer arrives *after* the reset, it wins the CAS, enqueues
  itself; our re-claim CAS fails. The producer's enqueue picks them up.

In both orderings, the bytes hit the wire in the same order producers
crossed the lock.

### 4.4 Per-client `MqttConnectedClient` API for the pool

```csharp
ResetScheduledFlag()      // worker: post-drain release (Volatile.Write 0)
TrySetScheduledFlag()     // CAS 0 → 1, returns true if claimed
RequestStop()             // worker: send failed non-recoverably → tear down
```

---

## 5. Per-Session Packet Bus

Each `MqttSession` owns one `MqttPacketBus` with three priority partitions:

| Partition | What goes in |
|---|---|
| `Health` | PINGRESP |
| `Control` | PUBACK / PUBREC / PUBREL / PUBCOMP / SUBACK / UNSUBACK |
| `Data` | PUBLISH (data delivery to this subscriber) |

Each partition is a `PreAllocatedQ<MqttPacketBuffer>` backed by **native
memory** rented from `HugeNativeMemoryPool`. No GC interaction on the
per-message hot path: bytes flow directly into native memory via
`AddLast`'s `Span.CopyTo`, and out via `TryRemoveFirst`'s `CopyTo(Span)`.

```
MqttPacketBus.TryDequeueItem(dest, out written)   // non-blocking
    for each partition (round-robin via _activePartition):
        MoveActivePartition()
        lock (_locks[active]):
            if partition.TryRemoveFirst(dest, out written):
                return true
    return false
```

Round-robin across partitions ensures Health/Control aren't starved by a
flood of Data. The dequeue is **non-blocking** — the sender pool
semaphore is the wakeup primitive, not a per-bus signal.

### Overflow

`MqttSession.EnqueueDataPacket` checks `PendingDataPacketsCount` against
`Constants.MaxPendingMessagesPerSession`; on overflow:

- `DropNewMessage` → return `EnqueueDataPacketResult.Dropped`
- `DropOldestQueuedMessage` (default) → drop from Data partition, then
  enqueue. Health/Control are never dropped (would break the connection).

---

## 6. Inline-Send Fast Path

For the common case (small packets, fast subscriber, uncongested socket)
the broker bypasses the bus entirely.

```
Session.EnqueueXxxPacket(buffer):
    if TrySendInline(buffer):     // succeeded → bytes on the wire
        return
    bus.EnqueueItem(buffer)        // queued
    NotifyReady()                  // wakes sender pool

TrySendInline(buffer):
    adapter = _attachedAdapter
    if adapter == null: return false
    return adapter.TrySendInline(buffer, _packetBus)

MqttChannelAdapter.TrySendInline(buffer, bus):
    if disposed: return false
    if buffer.Length > 16 KB: return false           // large → queue
    if !_channel.IsWritable(): return false          // kernel buf full
    if !Monitor.TryEnter(_syncRoot): return false    // contended
    try:
        if !bus.IsEmpty: return false                // FIFO guard
        if !_channel.IsWritable(): return false      // re-check
        _channel.Write(buffer.Packet)
        foreach (segment in buffer.Payload): _channel.Write(segment)
        return true
    finally:
        Monitor.Exit(_syncRoot)
```

### FIFO invariant

The `bus.IsEmpty` check happens **inside** `_syncRoot`. The sender-pool
worker also acquires `_syncRoot` to drain. While a producer holds the
lock for an inline send, no worker can drain — they wait. When the
producer releases, the worker (if any was about to drain) proceeds.

Concurrent producers race for `TryEnter`:
- Winner sees `bus.IsEmpty == true` → writes its bytes.
- Loser falls through to the queue path. The worker drains its bytes
  later, **after** the winner's bytes are on the wire.

Anything already in the bus when a producer arrives forces that producer
down the queue path (the `IsEmpty` re-check inside the lock fails),
preserving "queued before inline" ordering.

### What bypasses on the happy path

- `PreAllocatedQ.AddLast` (header write + memcpy into the ring)
- The schedule-flag CAS
- `SemaphoreSlim.Release`
- A context switch to a sender-pool worker
- `PreAllocatedQ.TryRemoveFirst` (memcpy out of the ring)

For the loopback bombard test most publishes hit the inline path; the
queue is the safety net for backpressure and concurrency contention.

---

## 7. Subscriptions & Routing

### Subscribe

```
MqttConnectedClient.HandleIncomingSubscribePacket(ref packet)
  └─ session.Subscribe(ref packet)
        └─ MqttClientSubscriptionsManager.Subscribe(ref packet)
              ├─ for each topic filter: build MqttSubscription
              │     compute TopicHash + TopicHashMask
              ├─ insert into the right hash dict
              │     (no-wildcard or wildcard, indexed by topic hash)
              ├─ notify ISubscriptionChangedNotification
              │     → SessionsManager updates _subscriberSessionsSnapshot
              │       (volatile array swap, so dispatch reads it lock-free)
              └─ build SubscribeResult (with retained-message matches)
SUBACK encoded → session.EnqueueControlPacket
retained matches → for each: Encode → session.EnqueueDataPacket
```

### Publish dispatch

```
MqttConnectedClient.HandleIncomingPublishPacket(packet)
  └─ sessionsManager.DispatchPublishPacketDirect(senderId, packet)
        └─ DispatchViaRingBuffer(senderId, topic, payload, qos, ...)
              ├─ snapshot = _subscriberSessionsSnapshot       (volatile read)
              ├─ MqttTopicHash.Calculate(topic, out hash, ...)
              ├─ for each session in snapshot:
              │     if session.TryCheckSubscriptions(topic, hash, qos, senderId,
              │                                       out checkResult)
              │        and checkResult.IsSubscribed:
              │           build MqttPublishPacket copy (per-session QoS, ids)
              │           if qos > 0: get next packet identifier
              │           session.EnqueuePublishPacket(ref publishPacketCopy)
              │             └─ encodes with session encoder (under _encoderLock)
              │             └─ records bytes in _unacknowledgedPublishPackets if qos > 0
              │             └─ EnqueueDataPacket → inline-send-or-queue
              └─ return result (matched count or NoMatchingSubscribers)
```

### Topic hash

`MqttClientSubscriptionsManager` keeps two dicts keyed by topic hash:

- `_noWildcardSubscriptionsByTopicHash` — exact-match topics, O(1).
- `_wildcardSubscriptionsByTopicHash` — wildcard subscriptions bucketed
  by `TopicHashMask` for partial matching.

Dispatch reads the subscriber list via a **single volatile read** of
`_subscriberSessionsSnapshot` (an `MqttSession[]` rebuilt under the
write lock on subscribe/unsubscribe). Per-publish: zero allocations
for the subscriber list.

---

## 8. Retained Messages

`MqttRetainedMessagesManager` keeps an in-memory map. Retain semantics:

- PUBLISH with retain flag + non-empty payload → updates entry
- PUBLISH with retain flag + empty payload → deletes entry
- New SUBSCRIBE matches retained entries → encoded as PUBLISH and
  enqueued via `Session.EnqueueDataPacket`

Persistence is left to the user (no event surface in this fork — if you
need durability, write through the manager directly).

---

## 9. Keep-Alive

`MqttServerKeepAliveMonitor` runs a single background thread that
iterates connected clients on a configurable period
(`MqttServerKeepAliveOptions.MonitorsPeriod`). Per client:

```
if now - client.Statistics.LastPacketReceived > keepAlive * tolerance:
    _ = client.StopAsync(reasonCode = KeepAliveTimeout)
```

`StopAsync` is now sync-bodied (returns `Task.CompletedTask`); the fire-
and-forget pattern stays for shape compatibility.

---

## 10. Concurrency Invariants Cheat-sheet

| Lock / Primitive | Held by | Protects |
|---|---|---|
| `MqttChannelAdapter._syncRoot` | sender pool worker (drain), inline producer (`TryEnter`), cold-path send (CONNACK / DISCONNECT) | exclusive write to one socket |
| `MqttSession._encoderLock` | every publisher dispatching to this session | exclusive use of the per-session encoder buffer |
| `MqttPacketBus._locks[partition]` | enqueue / drain | exclusive mutation of one partition's `PreAllocatedQ` |
| `MqttClientSubscriptionsManager._subscriptionsLock` (RWS) | subscribe/unsubscribe (W), `CheckSubscriptions` (R) | the two topic-hash dicts |
| `MqttClientSessionsManager._sessionsManagementLock` (RWS) | CreateClientConnection (W), status reads (R) | sessions storage |
| `MqttSession._unacknowledgedPublishPackets` | QoS > 0 publish + ack | retransmit list |
| `MqttSession._packetBus` (read) volatile via `_attachedAdapter`, `_onPacketReady` | producer / detach | adapter & notifier set/clear |
| `MqttConnectedClient._isScheduledForSend` (CAS) | producers, worker | "client is in pool ready queue" gate |

**Single-drain invariant.** At any instant at most one worker thread is
inside `MqttPacketBus.TryDequeueItem` for a given session, because:
- The session is in the ready queue at most once (gated by the schedule flag).
- Workers pop one entry at a time.
- The flag stays at 1 throughout the drain.

This is what lets `MqttPacketBus._activePartition` and `PreAllocatedQ`'s
head/tail pointers be unsynchronised across calls within a drain.

---

## 11. Server Options (current surface)

```csharp
public sealed class MqttServerOptions {
    public TimeSpan DefaultCommunicationTimeout = 100s
    public MqttServerTcpEndpointOptions DefaultEndpointOptions
    public MqttServerTlsTcpEndpointOptions TlsEndpointOptions
    public MqttServerKeepAliveOptions KeepAliveOptions
    public bool EnablePersistentSessions = false
    public int MaxPendingMessagesPerClient = Constants.MaxPendingMessagesPerSession
    public MqttPendingMessagesOverflowStrategy PendingMessagesOverflowStrategy
        = DropOldestQueuedMessage
    public int WriterBufferSize = 4096
    public int WriterBufferSizeMax = 65535
    public int SenderThreadCount = Environment.ProcessorCount

    // Publish interceptor — static function pointer, zero per-call cost.
    // See "Publish Interceptor" section for the full contract.
    public unsafe delegate*<in MqttPublishInterceptArgs,
                            PublishInterceptResult> PublishInterceptor
}
```

`MqttServerOptionsBuilder` provides a fluent builder for these.

---

## 12. End-to-End Message Flow

QoS 0 PUBLISH from Client A → 1 matching subscriber Client B:

```
Client A's TCP socket → MqttTcpChannel.ReceiveAsync (SAEA)
    → MqttChannelAdapter state machine assembles ReceivedMqttPacket
    → MqttConnectedClient.OnPacketReceived (sync callback)
    → HandleIncomingPublishPacket
    → MqttClientSessionsManager.DispatchPublishPacketDirect
        → for each session in _subscriberSessionsSnapshot:
            → session.TryCheckSubscriptions   (RWS read lock)
            → session.EnqueuePublishPacket
                → _encoderLock acquired
                → _encoder.Encode(ref publishPacket)
                → EnqueueDataPacket
                    → TrySendInline (B's adapter, B's bus)
                        → TryEnter B's _syncRoot
                        → bus empty? yes
                        → kernel writable? yes
                        → _channel.Write → bytes on B's socket
                    → (or fall through to bus.EnqueueItem + NotifyReady)
                → _encoderLock released
        → return matched count
    → BeginReceiveNext re-arms A's SAEA
```

Hot path (inline send, fast subscriber): one `Socket.Send` per
subscriber, plus the bookkeeping. Zero `Task` allocations, zero state-
machine boxes, no thread handoff.

---

## Notes on what was removed

If you've read upstream MQTTnet docs, this fork has dropped:

- The whole event/interceptor surface (`InterceptingPublishAsync`,
  `ValidatingConnectionAsync`, …). The intercepting-event-args structs
  exist in source but are not raised on a public surface.
- ASP.NET Core integration (Kestrel pipes, WebSocket adapter,
  `MqttHostedServer`, endpoint routing).
- `IMqttServerAdapter` / `IMqttChannelAdapter` interface abstractions.
  Only the concrete classes remain.
- The shared message ring buffer (`MessageRingBuffer`, `MessageSlot`,
  `InterceptingPublishBufferedAsync`, `GetRingBufferDiagnostics`).
  Per-session buses replaced it.
- Async-await throughout the receive path. Replaced by SAEA + callback
  state machine.
- Per-client send loop. Replaced by the multiplexed `MqttSenderPool`.

If you need any of those back, the upstream MQTTnet repo is the place to
look for the patterns; the abstractions cost more allocations than this
fork is willing to pay.
