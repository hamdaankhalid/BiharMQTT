# BiharMQTT

**BiharMQTT is a fork of [MQTTnet](https://github.com/dotnet/MQTTnet)** — a high-performance .NET MQTT library — with modifications focused on eliminating heap allocations on the server publish hot path.

> Upstream: [dotnet/MQTTnet](https://github.com/dotnet/MQTTnet) · License: MIT

---

## What We Changed

### Ring Buffer Message Store

The server now uses a pre-allocated **ring buffer** (`MessageRingBuffer`) for all in-flight message payloads. Every PUBLISH payload is written into the ring buffer exactly once via `memcpy`; all downstream consumers (interceptors, subscriber fan-out, network write) reference that memory region via a ref-counted slot handle. When all consumers finish, the slot is freed and the write head advances.

**Key changes from upstream MQTTnet:**

| Area | Change |
|---|---|
| **Ring buffer core** | New `MessageRingBuffer`, `MessageSlot`, `MessageSlotMetadata` — pinned `byte[]` with semaphore-based back-pressure and ref-counted slot lifecycle |
| **Zero-alloc inbound path** | Per-connection reusable body buffer in `MqttChannelAdapter` replaces `ArrayPool` renting; zero-copy payload slice in `MqttBufferReader` avoids decode-time copies |
| **Direct packet dispatch** | Inbound PUBLISH packets dispatch directly to the ring buffer via `DispatchPublishPacketDirect`, bypassing `MqttApplicationMessage` allocation entirely |
| **Outbound fan-out** | `DispatchViaRingBuffer` acquires a slot, copies payload once, and fans out to all subscribers sharing the same ring buffer memory; `MqttPacketBusItem.OnTerminated` releases the slot ref |
| **Buffered injection API** | New `MqttBufferedApplicationMessage` + builder for memory-efficient server-side message injection |
| **Buffered interceptor** | `InterceptingPublishBufferedEventArgs` provides `ReadOnlyMemory<byte>` payload views directly into ring buffer memory |
| **Always-on** | The ring buffer is not optional — it is always active. Configure capacity/slots via `MqttServerOptions.RingBufferCapacityBytes` (default 256 MB) and `RingBufferMaxSlots` (default 65536) |
| **Removed** | `SharedPooledPayloadBuffer` (ArrayPool-based fallback) deleted; `UseRingBuffer` opt-in flag removed |
| **Packet inspector fix** | `MqttPacketInspector.FillReceiveBuffer` now accepts `(byte[], offset, count)` to avoid writing excess bytes from the reusable body buffer |

### Allocation Profile (Server Hot Path)

| Step | Upstream MQTTnet | BiharMQTT |
|---|---|---|
| Inbound body read | `ArrayPool.Rent` / `new byte[]` | Per-connection reusable buffer (zero alloc after warmup) |
| Payload decode | `ReadRemainingData()` → `new byte[]` | `ReadRemainingDataSlice()` → zero-copy segment |
| `MqttApplicationMessage` | Allocated per message | Skipped on ring buffer fast path |
| Subscriber payload | Copied per subscriber | Shared ring buffer memory (ref-counted) |
| Outbound write | `ReadOnlySequence` over copied `byte[]` | `ReadOnlySequence` over ring buffer region |

### Configuration

```csharp
// Adjust ring buffer size (optional — defaults are 256 MB / 65536 slots)
var server = new MqttServerOptionsBuilder()
    .WithRingBuffer(capacityBytes: 128 * 1024 * 1024, maxSlots: 32768)
    .Build();
```

---

## Original MQTTnet Features

### General

* Async support
* TLS support for client and server (but not UWP servers)
* Extensible communication channels (e.g. In-Memory, TCP, TCP+TLS, WS)
* Lightweight (only the low level implementation of MQTT, no overhead)
* Performance optimized (processing ~150.000 messages / second)*
* Uniform API across all supported versions of the MQTT protocol
* Access to internal trace messages
* Unit tested (~636 tests)
* No external dependencies

\* Tested on local machine (Intel i7 8700K) with MQTTnet client and server running in the same process using the TCP
channel. The app for verification is part of this repository and stored in _/Tests/MQTTnet.TestApp.NetCore_.

### Client

* Communication via TCP (+TLS) or WS (WebSocket) supported
* Included core _LowLevelMqttClient_ with low level functionality
* Also included _ManagedMqttClient_ which maintains the connection and subscriptions automatically. Also application
  messages are queued and re-scheduled for higher QoS levels automatically.
* Rx support (via another project)
* Compatible with Microsoft Azure IoT Hub

### Server (broker)

* List of connected clients available
* Supports connected clients with different protocol versions at the same time
* Able to publish its own messages (no loopback client required)
* Able to receive every message (no loopback client required)
* Extensible client credential validation
* Retained messages are supported including persisting via interface methods (own implementation required)
* WebSockets supported (via ASP.NET Core 2.0, separate nuget)
* A custom message interceptor can be added which allows transforming or extending every received application message
* Validate subscriptions and deny subscribing of certain topics depending on requesting clients

## Getting Started

Samples for using MQTTnet are part of this repository. For starters these samples are recommended:

- [Connect with a broker](https://github.com/dotnet/MQTTnet/blob/master/Samples/Client/Client_Connection_Samples.cs)
- [Subscribing to data](https://github.com/dotnet/MQTTnet/blob/master/Samples/Client/Client_Subscribe_Samples.cs)
- [Publishing data](https://github.com/dotnet/MQTTnet/blob/master/Samples/Client/Client_Publish_Samples.cs)
- [Host own broker](https://github.com/dotnet/MQTTnet/blob/master/Samples/Server/Server_Simple_Samples.cs)

## License

This project is licensed under the [MIT License](LICENSE), same as the upstream MQTTnet project.

## .NET Foundation

The upstream MQTTnet project is supported by the [.NET Foundation](https://dotnetfoundation.org).
