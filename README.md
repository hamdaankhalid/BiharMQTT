# BiharMQTT

**BiharMQTT is a fork of [MQTTnet](https://github.com/dotnet/MQTTnet)** — a high-performance .NET MQTT library — stripped down to a TCP-only broker with a focus on eliminating heap allocations on the publish hot path.

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
| **Removed from upstream** | All ASP.NET Core / Kestrel / WebSocket integration; `IMqttServerAdapter` / `IMqttChannelAdapter` interfaces; the entire event/interceptor surface (`InterceptingPublishAsync` & friends); the shared `MessageRingBuffer` (replaced by per-session buses). |

### Allocation Profile

Verified via `dotnet-counters` against the bombard test (50 publishers × 1000 msg/s × 10 subscribers ≈ 500k deliveries):

- Total Gen0 + Gen1 + Gen2 collections: **3** over ~60 s wall.
- Total GC pause: **~12 ms** over the run.
- Total allocated: **~27 MB**, ~50 bytes per packet operation.

The receive callback chain, sender pool drain, and inline-send fast path are zero-allocation in steady state.

---

## Getting Started

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

`TestClient/Program.cs` is a load generator using the upstream MQTTnet client.

## License

MIT, same as the upstream MQTTnet project.

## .NET Foundation

The upstream MQTTnet project is supported by the [.NET Foundation](https://dotnetfoundation.org).
