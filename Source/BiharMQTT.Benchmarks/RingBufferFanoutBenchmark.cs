// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BiharMQTT.Protocol;
using BiharMQTT.Server;

namespace BiharMQTT.Benchmarks;

/// <summary>
///     Benchmarks fan-out message delivery through the server with multiple
///     subscribers.  This highlights BiharMQTT's ring buffer zero-copy strategy:
///     the payload is written once into the ring buffer and shared across all
///     subscribers via ref-counted slots, avoiding per-subscriber allocations.
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0)]
[RPlotExporter, RankColumn]
[MemoryDiagnoser]
public class RingBufferFanoutBenchmark : BaseBenchmark
{
    const int MessageCount = 500;

    [Params(1, 10)]
    public int SubscriberCount;

    int PayloadSize = 1024;

    MqttApplicationMessage _message;
    IMqttClient _publisherClient;
    List<IMqttClient> _subscriberClients;
    MqttServer _mqttServer;
    CountdownEvent _countdown;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithRingBuffer(capacityBytes: 64 * 1024 * 1024, maxSlots: 16384)
            .Build();

        var serverFactory = new MqttServerFactory();
        _mqttServer = serverFactory.CreateMqttServer(serverOptions);
        _mqttServer.StartAsync().GetAwaiter().GetResult();

        var clientFactory = new MqttClientFactory();

        _subscriberClients = new List<IMqttClient>();
        for (var s = 0; s < SubscriberCount; s++)
        {
            var sub = clientFactory.CreateMqttClient();
            var subOpts = new MqttClientOptionsBuilder()
                .WithTcpServer("localhost")
                .WithClientId($"bench-sub-{s}")
                .Build();
            sub.ConnectAsync(subOpts).GetAwaiter().GetResult();
            sub.ApplicationMessageReceivedAsync += _ =>
            {
                _countdown.Signal();
                return Task.CompletedTask;
            };
            sub.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter("bench/fanout", MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build())
                .GetAwaiter().GetResult();
            _subscriberClients.Add(sub);
        }

        _publisherClient = clientFactory.CreateMqttClient();
        var pubOpts = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost")
            .WithClientId("bench-pub")
            .Build();
        _publisherClient.ConnectAsync(pubOpts).GetAwaiter().GetResult();

        var payload = new byte[PayloadSize];
        Random.Shared.NextBytes(payload);
        _message = new MqttApplicationMessageBuilder()
            .WithTopic("bench/fanout")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _publisherClient?.DisconnectAsync().GetAwaiter().GetResult();
        _publisherClient?.Dispose();

        foreach (var sub in _subscriberClients)
        {
            sub.DisconnectAsync().GetAwaiter().GetResult();
            sub.Dispose();
        }

        _subscriberClients.Clear();

        _mqttServer?.StopAsync().GetAwaiter().GetResult();
        _mqttServer?.Dispose();
    }

    [Benchmark]
    public void Fanout_500_Messages()
    {
        var totalExpected = MessageCount * SubscriberCount;
        _countdown = new CountdownEvent(totalExpected);

        for (var i = 0; i < MessageCount; i++)
        {
            _publisherClient.PublishAsync(_message).GetAwaiter().GetResult();
        }

        _countdown.Wait(TimeSpan.FromSeconds(60));
        _countdown.Dispose();
    }
}
