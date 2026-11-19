# BiharMQTT

**BiharMQTT is a fork of [MQTTnet](https://github.com/dotnet/MQTTnet)** — a high-performance .NET MQTT library — stripped down to a lean TCP + WebSocket broker with a focus on eliminating heap allocations on the publish hot path.

> Upstream: [dotnet/MQTTnet](https://github.com/dotnet/MQTTnet) · License: MIT

---

## What We Changed

The broker has been substantially rewritten. Architecture details are in [`docs/Server-Architecture.md`](docs/Server-Architecture.md). Highlights:

| Area | Change |
|---|---|
| **Receive path** | `SocketAsyncEventArgs` + per-channel callback state machine in `MqttChannelAdapter`. No `async`/`await`, no `Task` allocation, no state-machine boxing per packet. Cached delegate fields avoid closure alloc. |
| **Send path** | Sync-only `MqttChannelAdapter.SendPacket` over `Socket.Send` / `SslStream.Write`. The async send pipeline was deleted. |
| **Sender concurrency** | `MqttSenderPool` — M dedicated background threads (default `Environment.ProcessorCount`) servicing N connected clients via a ready-queue + semaphore. Replaces one-thread-per-client. |
| **Inline-send fast path** | Producers bypass the per-session bus entirely when the channel send-lock is uncontended, the bus is empty, and the kernel send buffer accepts immediately. Falls back to the bus on contention or backpressure. |
| **Per-session bus** | Three priority partitions (Health / Control / Data) backed by `PreAllocatedQ<T>` over native memory (`HugeNativeMemoryPool`). No GC interaction on the per-message path. |
| **Publish interceptor** | `unsafe delegate*<in MqttPublishInterceptArgs, PublishInterceptResult>` static-only function-pointer hook on `MqttServerOptions`. `Allow` / `Consume` / `Reject`. The args type is a `readonly ref struct` — buffers borrowed only for the call. |
| **Server-side publish** | `MqttServer.Publish(ReadOnlySpan<byte> topic, ReadOnlySequence<byte> payload, …)` — zero-alloc inject API with the same lifetime contract as the interceptor. |
| **WebSocket transport** | MQTT over `ws://` and `wss://` (RFC 6455, subprotocol `mqtt`) added back without ASP.NET Core / Kestrel: the existing TCP listener performs the HTTP Upgrade handshake in-process and hands a BCL `WebSocket` to `MqttWebSocketChannel`, which plugs into the same `IMqttChannel` / `MqttChannelAdapter` path as TCP. `wss` reuses the TLS endpoint's certificate and mTLS settings. Trade-off: WebSocket writes are blocking `SendAsync` calls and skip the inline-send fast path. |
| **Removed from upstream** | All ASP.NET Core / Kestrel integration (WebSocket is now served in-process, see above); `IMqttServerAdapter` / `IMqttChannelAdapter` interfaces; the entire event/interceptor surface (`InterceptingPublishAsync` & friends); the shared `MessageRingBuffer` (replaced by per-session buses). |

### Allocation Profile

Verified via `dotnet-counters` against the bombard test (50 publishers × 1000 msg/s × 10 subscribers ≈ 500k deliveries):

- Total Gen0 + Gen1 + Gen2 collections: **3** over ~60 s wall.
- Total GC pause: **~12 ms** over the run.
- Total allocated: **~27 MB**, ~50 bytes per packet operation.

The receive callback chain, sender pool drain, and inline-send fast path are zero-allocation in steady state.

---

## Getting Started

BiharMQTT ships as a single NuGet package:

```bash
dotnet add package BiharMQTT.Server
```

> Upgrading from a version that used two packages? Remove the `BiharMQTT` package reference and keep `BiharMQTT.Server`. The core package has been merged into it and is no longer published; namespaces are unchanged.

`Samples/Program.cs` starts a plain TCP broker on port 1883:

```csharp
var server = new MqttServerOptionsBuilder()
    .WithDefaultEndpoint()
    .WithDefaultEndpointPort(1883)
    .Build();

unsafe { server.PublishInterceptor = &AllowAllInterceptor; }   // optional

using var mqttServer = new MqttServerFactory().CreateMqttServer(server, logger);
await mqttServer.StartAsync();

static PublishInterceptResult AllowAllInterceptor(in MqttPublishInterceptArgs args)
    => PublishInterceptResult.Allow;
```

### TCP and WebSocket in one server

A single `MqttServer` can serve every transport at once, each on its own port. All endpoints share the same sessions, subscriptions and retained messages, so a browser on WebSocket and a device on TCP can talk to each other directly:

```csharp
var options = new MqttServerOptionsBuilder()
    .WithDefaultEndpoint()                  // tcp://   :1883
    .WithEncryptedEndpoint()                // mqtts:// :8883
    .WithEncryptionCertificate(cert)
    .WithWebSocketEndpoint()                // ws://    :8083/mqtt
    .WithEncryptedWebSocketEndpoint()       // wss://   :8084/mqtt (reuses the TLS cert)
    .Build();
```

`WithWebSocketEndpoint(port, path)` and `WithEncryptedWebSocketEndpoint(port, path)` take an optional port and URL path. Clients must offer the `mqtt` subprotocol (e.g. MQTT.js: `mqtt.connect('ws://host:8083/mqtt')`).

`TestClient/Program.cs` is a load generator using the upstream MQTTnet client.

## License

MIT, same as the upstream MQTTnet project.

## .NET Foundation

The upstream MQTTnet project is supported by the [.NET Foundation](https://dotnetfoundation.org).
