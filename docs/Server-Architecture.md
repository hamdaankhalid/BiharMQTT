# MQTTnet Server — Architecture Guide

This document describes how the classes in **MQTTnet.Server** (and the supporting
**MQTTnet** core and **MQTTnet.AspNetCore** libraries) fit together.

---

## High-Level Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                         User Code                                │
│  MqttServerFactory / ASP.NET Core DI (ServiceCollectionExt)      │
└──────────────────────┬───────────────────────────────────────────┘
                       │ creates
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│                        MqttServer                                │
│  Owns: MqttClientSessionsManager, MqttRetainedMessagesManager,  │
│        MqttServerKeepAliveMonitor, MqttServerEventContainer      │
│  Wires: IMqttServerAdapter[] (transports)                        │
└────────┬──────────┬──────────────┬──────────────────────────────┘
         │          │              │
         ▼          ▼              ▼
   ┌───────────┐ ┌──────────┐ ┌──────────────────┐
   │ TCP       │ │ WebSocket│ │ Kestrel/ASP.NET  │
   │ Adapter   │ │ Adapter  │ │ ConnectionHandler│
   └─────┬─────┘ └────┬─────┘ └────────┬─────────┘
         │             │                │
         ▼             ▼                ▼
   ┌──────────────────────────────────────────┐
   │          IMqttChannelAdapter              │
   │  (MqttChannelAdapter / MqttConnection-   │
   │   Context) — packet encode/decode over   │
   │   an IMqttChannel                        │
   └──────────────────┬──────────────────────┘
                      │
                      ▼
   ┌──────────────────────────────────────────┐
   │     MqttClientSessionsManager            │
   │  ┌──────────────┐  ┌─────────────────┐  │
   │  │MqttConnected │  │  MqttSession    │  │
   │  │   Client     │──│  (persistent)   │  │
   │  └──────────────┘  └─────────────────┘  │
   └──────────────────────────────────────────┘
```

---

## 1  Creating & Starting the Server

### Standalone (no ASP.NET Core)

```
MqttServerFactory
  ├─ creates MqttServerOptions
  ├─ creates default IMqttServerAdapter (MqttTcpServerAdapter)
  └─ new MqttServer(options, adapters, logger)
```

`MqttServerFactory` exposes builder helpers (`CreateServerBuilder`,
`CreateOptionsBuilder`, etc.) and defaults to a single
`MqttTcpServerAdapter`.  Calling `MqttServer.StartAsync()`:

1. Loads retained messages via `MqttRetainedMessagesManager.Start()`.
2. Starts `MqttClientSessionsManager`.
3. Starts `MqttServerKeepAliveMonitor` (background loop).
4. Iterates every `IMqttServerAdapter`, sets its `ClientHandler` delegate
   to `MqttServer.OnHandleClient`, and calls `adapter.StartAsync(options)`.
5. Fires `StartedAsync` event.

### ASP.NET Core Hosted

`ServiceCollectionExtensions.AddHostedMqttServer(...)` registers:

| Registration | Type |
|---|---|
| `IHostedService` | `MqttHostedServer` (inherits `MqttServer`) |
| `IMqttServerAdapter` | `MqttConnectionHandler` (Kestrel connections) |
| `IMqttServerAdapter` | `MqttWebSocketServerAdapter` (WebSocket upgrade) |
| Singleton | `MqttServerFactory`, `MqttServerOptions`, logger |

`MqttHostedServer` waits for `IHostApplicationLifetime.ApplicationStarted`
then calls `StartAsync()` — the same startup sequence as standalone.

**Routing entry points:**

| Extension Method | Transport |
|---|---|
| `MapMqtt(pattern)` | Endpoint routing → `MqttConnectionHandler` |
| `UseMqttEndpoint(path)` | Middleware WebSocket upgrade → `MqttWebSocketServerAdapter` |
| `UseMqttServer(configure)` | Resolve & configure the server instance |

---

## 2  Transport Layer — Adapters & Channels

### `IMqttServerAdapter`

```csharp
public interface IMqttServerAdapter : IDisposable
{
    Func<IMqttChannelAdapter, Task> ClientHandler { get; set; }
    Task StartAsync(MqttServerOptions options, IMqttNetLogger logger);
    Task StopAsync();
}
```

The server sets `ClientHandler` before calling `StartAsync`. When a new
connection arrives the adapter wraps it in an `IMqttChannelAdapter` and
invokes `ClientHandler`.

### Built-in Adapters

| Adapter | Transport | How it works |
|---|---|---|
| **`MqttTcpServerAdapter`** | Raw TCP | Creates `MqttTcpServerListener` per endpoint (IPv4/IPv6, plain/TLS). Each listener accepts sockets in a loop, optionally wraps in `SslStream`, creates `MqttTcpChannel` → `MqttChannelAdapter`, and invokes `ClientHandler`. |
| **`MqttConnectionHandler`** | Kestrel pipe | ASP.NET Core `ConnectionHandler`. Wraps `ConnectionContext` in `MqttConnectionContext` (which implements `IMqttChannelAdapter` directly using `PipeReader`/`PipeWriter`). |
| **`MqttWebSocketServerAdapter`** | WebSocket | Accepts an upgraded `WebSocket` from middleware, creates `MqttWebSocketChannel` → `MqttChannelAdapter`. |

### Channel & Adapter Abstraction

```
IMqttChannel                    IMqttChannelAdapter
  ├ ConnectAsync                  ├ ConnectAsync / DisconnectAsync
  ├ DisconnectAsync               ├ SendPacketAsync
  ├ ReadAsync / WriteAsync        ├ ReceivePacketAsync
  └ endpoint, cert, TLS?         ├ PacketFormatterAdapter
                                  └ byte counters, PacketInspector
```

`MqttChannelAdapter` (core library) wraps an `IMqttChannel` and handles
MQTT packet framing, serialisation via `MqttPacketFormatterAdapter`, and
protocol-version detection from the first `CONNECT` packet.

`MqttConnectionContext` (ASP.NET Core) implements `IMqttChannelAdapter`
directly on top of Kestrel's pipelines — bypassing the `IMqttChannel`
layer for better performance.

---

## 3  Connection Handshake

When `ClientHandler` is invoked with a new `IMqttChannelAdapter`:

```
MqttServer.OnHandleClient(adapter)
  └─► MqttClientSessionsManager.HandleClientConnectionAsync(adapter)
        │
        ├─ 1. Read first packet (must be CONNECT)
        ├─ 2. Fire ValidatingConnectionAsync event
        │      └─ User can inspect/modify client ID, credentials,
        │         session expiry, assign session items, set a
        │         return/reason code to reject
        ├─ 3. If rejected → send CONNACK with failure code, disconnect
        ├─ 4. Create or reuse MqttSession
        │      ├─ MqttSessionsStorage.GetOrCreate(clientId)
        │      ├─ Fire PreparingSessionAsync
        │      └─ If existing client with same ID → take over (disconnect old)
        ├─ 5. Create MqttConnectedClient(session, adapter, ...)
        ├─ 6. Send CONNACK (success)
        └─ 7. client.RunAsync()   ← starts send/receive loops
```

---

## 4  Client Runtime — `MqttConnectedClient`

Once connected, `MqttConnectedClient.RunAsync()` spins up two concurrent
loops:

### Receive Loop
```
while running:
    packet = adapter.ReceivePacketAsync()
    fire InterceptingInboundPacketAsync   ← can inspect/modify/drop
    dispatch by packet type:
        PUBLISH      → HandleIncomingPublishPacketAsync
        SUBSCRIBE    → HandleIncomingSubscribePacketAsync
        UNSUBSCRIBE  → HandleIncomingUnsubscribePacketAsync
        PINGREQ      → reply PINGRESP
        DISCONNECT   → initiate shutdown
        PUBACK/REC/REL/COMP → QoS acknowledgement tracking
        AUTH         → enhanced auth exchange
```

### Send Loop
```
while running:
    packet = session.DequeuePacketAsync()   (from 3 priority queues)
    fire InterceptingOutboundPacketAsync    ← can inspect/modify/drop
    adapter.SendPacketAsync(packet)
```

### Disconnect
On disconnect (clean, error, keepalive timeout, or takeover):
- If unclean, publishes the client's **Will Message** (if any).
- Fires `ClientDisconnectedAsync`.
- If session has expired → deletes session, fires `SessionDeletedAsync`.

---

## 5  Session Management — `MqttSession`

Each MQTT client ID maps to a persistent `MqttSession`:

```
MqttSession
  ├─ Id (client ID string)
  ├─ Items (dictionary for user data, survives reconnects)
  ├─ LatestConnectPacket
  ├─ MqttClientSubscriptionsManager   ← owns all subscriptions
  ├─ Packet queues (control / data / health channels)
  ├─ Packet identifier provider (for QoS 1/2)
  ├─ QoS 2 acknowledgement tracking
  ├─ ExpiryInterval (MQTT 5 session expiry)
  └─ WillMessageSent flag
```

Sessions are stored in `MqttSessionsStorage` (an in-memory dictionary).
When `EnablePersistentSessions` is true, sessions survive client
disconnects until they expire.

---

## 6  Subscriptions & Message Routing

### Subscribe Flow

```
MqttConnectedClient receives SUBSCRIBE packet
  └─► MqttClientSessionsManager.SubscribeAsync(clientId, packet)
        └─► session.SubscriptionsManager.Subscribe(packet, ...)
              │
              ├─ For each topic filter:
              │    ├─ Fire InterceptingSubscriptionAsync
              │    │    └─ User can accept/reject, change QoS, close connection
              │    ├─ Create MqttSubscription (topic, QoS, options)
              │    ├─ Compute TopicHash + TopicHashMask for fast routing
              │    └─ Store in hash-indexed maps
              │
              ├─ Notify ISubscriptionChangedNotification
              │    └─ MqttClientSessionsManager updates _subscriberSessions set
              │
              ├─ Match retained messages for new subscriptions
              │    └─ Queue matched retained messages to session
              │
              └─ Return SubscribeResult → SUBACK
```

### Publish / Message Routing

```
MqttConnectedClient receives PUBLISH
  └─► MqttClientSessionsManager.DispatchApplicationMessage(senderClientId, msg)
        │
        ├─ 1. Fire InterceptingPublishAsync
        │      └─ Can rewrite topic/payload, skip routing, or close sender
        │
        ├─ 2. If retain flag → MqttRetainedMessagesManager.UpdateMessage()
        │      └─ Empty payload = delete retained message
        │      └─ Fire RetainedMessageChangedAsync
        │
        ├─ 3. Route to subscribers:
        │      for each session in _subscriberSessions:
        │          result = session.Subscriptions.CheckSubscriptions(topic)
        │          if matched:
        │              fire InterceptingClientEnqueueAsync  ← can skip
        │              session.EnqueueDataPacket(publish)
        │              fire ApplicationMessageEnqueuedOrDroppedAsync
        │
        └─ 4. If no subscriber consumed → fire ApplicationMessageNotConsumedAsync
```

### Topic Hash Optimisation

`MqttClientSubscriptionsManager` uses a **topic hash** scheme for fast
fan-out. Each subscription's topic filter is hashed, and subscriptions
are bucketed into:

- `_noWildcardSubscriptionsByTopicHash` — exact-match topics, O(1) lookup.
- `_wildcardSubscriptionsByTopicHash` — wildcard topics bucketed by
  `TopicHashMask` for partial matching, avoiding full linear scans.

---

## 7  Retained Messages — `MqttRetainedMessagesManager`

```
MqttRetainedMessagesManager
  ├─ Start()
  │    └─ Fire LoadingRetainedMessagesAsync → user loads from storage
  │
  ├─ UpdateMessage(topic, msg)
  │    ├─ Empty payload → delete
  │    └─ Fire RetainedMessageChangedAsync → user persists
  │
  ├─ GetMessages() / GetMessage(topic)
  │    └─ Used during subscribe to deliver retained messages
  │
  └─ ClearMessages()
       └─ Fire RetainedMessagesClearedAsync
```

Retained messages are stored **in memory**. Persistence is the
responsibility of user code via the event handlers.

---

## 8  Keep-Alive Monitoring — `MqttServerKeepAliveMonitor`

A single background thread iterates all connected clients every ~1 second.
For each client it checks:

```
if (now - client.Statistics.LastPacketSentTimestamp) > keepAliveInterval * factor
    → disconnect with MqttDisconnectReasonCode.KeepAliveTimeout
```

The tolerance factor is configurable via `MqttServerKeepAliveOptions.MonitorsPeriod`.

---

## 9  Event / Interceptor Model — `MqttServerEventContainer`

All server events are centralised in `MqttServerEventContainer` and
exposed as public `AsyncEvent<T>` properties on `MqttServer`.

### Lifecycle Events

| Event | When |
|---|---|
| `StartedAsync` | Server has started |
| `StoppedAsync` | Server has stopped |
| `ClientConnectedAsync` | Client completed CONNECT handshake |
| `ClientDisconnectedAsync` | Client disconnected (any reason) |
| `SessionDeletedAsync` | Expired session removed |
| `PreparingSessionAsync` | Session about to be created/reused |

### Validation

| Event | Purpose |
|---|---|
| `ValidatingConnectionAsync` | Accept/reject CONNECT; set client ID, session items, auth |

### Message Interceptors

| Event | Purpose |
|---|---|
| `InterceptingPublishAsync` | Inspect/modify/skip incoming PUBLISH |
| `InterceptingClientEnqueueAsync` | Allow/deny per-subscriber delivery |
| `ApplicationMessageEnqueuedOrDroppedAsync` | Notification after enqueue or drop |
| `ApplicationMessageNotConsumedAsync` | No subscriber consumed the message |

### Packet Interceptors

| Event | Purpose |
|---|---|
| `InterceptingInboundPacketAsync` | Raw inbound packet inspection |
| `InterceptingOutboundPacketAsync` | Raw outbound packet inspection |

### Subscription Interceptors

| Event | Purpose |
|---|---|
| `InterceptingSubscriptionAsync` | Inspect/modify/reject SUBSCRIBE |
| `InterceptingUnsubscriptionAsync` | Inspect/modify/reject UNSUBSCRIBE |
| `ClientSubscribedTopicAsync` | Notification after subscribe |
| `ClientUnsubscribedTopicAsync` | Notification after unsubscribe |

### Retained Message Events

| Event | Purpose |
|---|---|
| `LoadingRetainedMessagesAsync` | Load retained messages on start |
| `RetainedMessageChangedAsync` | A retained message was added/updated/deleted |
| `RetainedMessagesClearedAsync` | All retained messages cleared |

### Enhanced Authentication

| Event | Purpose |
|---|---|
| `ValidatingConnectionAsync` | Supports `ExchangeEnhancedAuthenticationAsync()` for MQTT 5 AUTH packet exchange during CONNECT |

---

## 10  Server Options

```
MqttServerOptions
  ├─ DefaultEndpointOptions: MqttServerTcpEndpointOptions
  │    ├─ IsEnabled, Port (1883), BoundInterSocketAddress
  │    ├─ ConnectionBacklog, ReuseAddress, LingerState, NoDelay
  │    └─ IPv4/IPv6 bind address
  │
  ├─ TlsEndpointOptions: MqttServerTlsTcpEndpointOptions
  │    ├─ Inherits all base options + Port (8883)
  │    ├─ CertificateCredentials, RemoteCertificateValidationCallback
  │    ├─ SslProtocol, ClientCertificateRequired
  │    └─ CheckCertificateRevocation
  │
  ├─ DefaultCommunicationTimeout (TimeSpan)
  ├─ KeepAliveOptions: MqttServerKeepAliveOptions
  ├─ EnablePersistentSessions (bool)
  ├─ MaxPendingMessagesPerClient (int)
  ├─ PendingMessagesOverflowStrategy (DropOldest / DropNew)
  └─ WriterBufferSize / WriterBufferSizeMax
```

`MqttServerOptionsBuilder` provides a fluent API to configure all of the
above. `AspNetMqttServerOptionsBuilder` extends it with `IServiceProvider`
access for DI-based configuration.

---

## 11  Stopping & Disconnecting

### Server Stop

```
MqttServer.StopAsync(MqttServerStopOptions)
  ├─ Cancel root CancellationToken
  ├─ Stop all IMqttServerAdapters
  ├─ MqttClientSessionsManager.CloseAllConnections(reason)
  │    └─ Each MqttConnectedClient.StopAsync(reason)
  │         └─ Optionally sends MQTT 5 DISCONNECT packet
  ├─ Stop MqttServerKeepAliveMonitor
  ├─ Dispose MqttRetainedMessagesManager
  └─ Fire StoppedAsync event
```

### Client Disconnect

```
MqttServer.DisconnectClientAsync(clientId, MqttServerClientDisconnectOptions)
  └─► MqttClientSessionsManager finds client → client.StopAsync(...)
        ├─ Sends DISCONNECT packet (MQTT 5 only, with reason code)
        └─ Fires ClientDisconnectedAsync
```

`MqttServerClientDisconnectOptions` supports:
- `ReasonCode` (MQTT 5 disconnect reason)
- `ReasonString`
- `UserProperties`
- `ServerReference` (for server-redirect)

---

## 12  Status & Monitoring

### `MqttClientStatus`

Exposes live client info: `Id`, `Endpoint`, `ProtocolVersion`,
`ConnectedTimestamp`, `LastPacketReceivedTimestamp`,
`LastNonKeepAlivePacketReceivedTimestamp`, `SentApplicationMessagesCount`,
`ReceivedApplicationMessagesCount`, `Session` reference.

### `MqttSessionStatus`

Exposes session info: `Id`, `PendingApplicationMessagesCount`,
`Items`, `CreatedTimestamp`, `ExpiryInterval`, subscription list,
`DeleteAsync()`, `EnqueueAsync(msg)`.

### `MqttClientStatistics`

Tracks per-client packet/message counters and timestamps. Exposed via
`MqttClientStatus` and reset via `ResetStatistics()`.

---

## 13  Packet Types (Core Library)

The MQTT packet model in `MQTTnet.Packets`:

| Packet Class | MQTT Control Packet |
|---|---|
| `MqttConnectPacket` | CONNECT |
| `MqttConnAckPacket` | CONNACK |
| `MqttPublishPacket` | PUBLISH |
| `MqttPubAckPacket` | PUBACK |
| `MqttPubRecPacket` | PUBREC |
| `MqttPubRelPacket` | PUBREL |
| `MqttPubCompPacket` | PUBCOMP |
| `MqttSubscribePacket` | SUBSCRIBE |
| `MqttSubAckPacket` | SUBACK |
| `MqttUnsubscribePacket` | UNSUBSCRIBE |
| `MqttUnsubAckPacket` | UNSUBACK |
| `MqttPingReqPacket` | PINGREQ |
| `MqttPingRespPacket` | PINGRESP |
| `MqttDisconnectPacket` | DISCONNECT |
| `MqttAuthPacket` | AUTH (MQTT 5) |

---

## 14  Class Dependency Summary

```
MqttServerFactory ──creates──► MqttServer
                                  │
           ┌──────────────────────┼──────────────────────┐
           │                      │                      │
           ▼                      ▼                      ▼
  MqttServerEventContainer  MqttRetainedMessagesManager  MqttServerKeepAliveMonitor
           │                                                    │
           │              MqttClientSessionsManager ◄───────────┘
           │                 │              │
           │                 │              ▼
           │                 │     MqttSessionsStorage
           │                 │         │
           │                 ▼         ▼
           │          MqttConnectedClient ◄──► MqttSession
           │                 │                    │
           │                 │                    ▼
           │                 │          MqttClientSubscriptionsManager
           │                 │                    │
           │                 ▼                    ▼
           │          IMqttChannelAdapter    MqttSubscription
           │                 │
           ▼                 ▼
     AsyncEvent<T>     MqttChannelAdapter / MqttConnectionContext
                             │
                             ▼
                        IMqttChannel
                     (TcpChannel / WebSocketChannel / ConnectionContext)
```

### ASP.NET Core Integration

```
ServiceCollectionExtensions
  └─ registers ──► MqttHostedServer (IHostedService, extends MqttServer)
                   MqttConnectionHandler (IMqttServerAdapter, Kestrel TCP)
                   MqttWebSocketServerAdapter (IMqttServerAdapter, WebSocket)
                   MqttServerFactory, MqttServerOptions, logger

EndpointRouterExtensions.MapMqtt()
  └─ routes connections to ──► MqttConnectionHandler
                                 └─ creates MqttConnectionContext (IMqttChannelAdapter)
                                 └─ invokes ClientHandler → server pipeline

ApplicationBuilderExtensions.UseMqttEndpoint()
  └─ middleware upgrades WebSocket
     └─ passes to MqttWebSocketServerAdapter
        └─ creates MqttChannelAdapter over MqttWebSocketChannel
        └─ invokes ClientHandler → server pipeline
```

---

## 15  End-to-End Message Flow Example

```
Client A publishes "sensor/temp" (QoS 1, Retain)
  │
  ▼
MqttTcpServerListener accepts TCP → MqttChannelAdapter
  │
  ▼
MqttConnectedClient (Client A) receive loop
  │  reads PUBLISH packet
  │  fires InterceptingInboundPacketAsync
  │
  ▼
MqttClientSessionsManager.DispatchApplicationMessage()
  │
  ├─► InterceptingPublishAsync — user can modify/skip
  │
  ├─► MqttRetainedMessagesManager.UpdateMessage("sensor/temp", payload)
  │     └─ fires RetainedMessageChangedAsync
  │
  ├─► For each subscriber session:
  │     session.Subscriptions.CheckSubscriptions("sensor/temp")
  │       └─ hash lookup → match found (Client B subscribed "sensor/#")
  │     InterceptingClientEnqueueAsync — user can block per-subscriber
  │     session.EnqueueDataPacket(PUBLISH)
  │     ApplicationMessageEnqueuedOrDroppedAsync
  │
  └─► Send PUBACK to Client A (QoS 1)

Client B's MqttConnectedClient send loop:
  │  dequeues PUBLISH from session
  │  fires InterceptingOutboundPacketAsync
  │  adapter.SendPacketAsync(PUBLISH)
  │
  ▼
Client B receives "sensor/temp"
```

---

## 16  Ring Buffer Message Store (Optional)

When `MqttServerOptions.UseRingBuffer = true`, the server pre-allocates a
fixed `byte[]` (default 256 MB) at startup.  Message payloads flow through
a zero-allocation path:

```
Producer (wire PUBLISH or InjectApplicationMessage)
  │
  ▼
MessageRingBuffer.Acquire(payloadLength)
  │  blocks if buffer full (back-pressure)
  │  returns MessageSlot + Memory<byte>
  │
  ├─ memcpy payload into ring buffer
  │
  ├─ Invoke InterceptingPublishBufferedAsync
  │    └─ ReadOnlyMemory<byte> view into ring buffer (zero-copy)
  │
  ├─ For each subscriber:
  │    ├─ ringBuffer.AddRef(slot)
  │    ├─ MqttPublishPacket.Payload = ReadOnlySequence over ring buffer
  │    ├─ busItem.OnTerminated = ringBuffer.Release
  │    └─ session.EnqueueDataPacket(busItem)
  │
  └─ ringBuffer.Release(slot)  ← drop sentinel ref
      └─ if 0 subscribers → buffer freed immediately

Send loop (per subscriber):
  adapter.SendPacketAsync(packet)    ← memcpy ring buffer → socket
  busItem.Complete()
    └─ OnTerminated → ringBuffer.Release(slot)
        └─ last subscriber → ring buffer region reclaimed
```

### Configuration

| Option | Default | Description |
|---|---|---|
| `UseRingBuffer` | `false` | Enable the ring buffer message store |
| `RingBufferCapacityBytes` | 256 MB | Total ring buffer size |
| `RingBufferMaxSlots` | 65536 | Max concurrent in-flight messages |

### Diagnostics

`MqttServer.GetRingBufferDiagnostics()` returns:
- `CapacityBytes` — total buffer size
- `TotalAcquired` / `TotalReleased` — lifetime counters
- `InFlight` — currently in-use messages

### Interceptors

When the ring buffer is active:
- `InterceptingPublishBufferedAsync` provides `ReadOnlyMemory<byte>` payload
  views directly into ring buffer memory (zero-copy).
- **The payload is only valid during the callback.** Call `.ToArray()` to keep.
- The class-based `InterceptingPublishAsync` still works; when registered it
  forces a fallback to the standard allocation-based path.

### Retained Messages

Retained messages are copied out of the ring buffer into separate storage
(`Dictionary<string, MqttApplicationMessage>`). Ring buffer space is freed
immediately.
