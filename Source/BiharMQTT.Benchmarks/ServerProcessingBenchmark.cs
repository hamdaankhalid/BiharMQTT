// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BiharMQTT.Server;
using BiharMQTT.Protocol;

namespace BiharMQTT.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[RPlotExporter, RankColumn]
[MemoryDiagnoser]
public class ServerProcessingBenchmark : BaseBenchmark
{
    const int MessageCount = 1000;

    MqttApplicationMessage _message;
    IMqttClient _publisherClient;
    IMqttClient _subscriberClient;
    MqttServer _mqttServer;
    CountdownEvent _countdown;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var serverOptions = new MqttServerOptionsBuilder().WithDefaultEndpoint().Build();
        var serverFactory = new MqttServerFactory();
        _mqttServer = serverFactory.CreateMqttServer(serverOptions);
        _mqttServer.StartAsync().GetAwaiter().GetResult();

        var clientFactory = new MqttClientFactory();

        _subscriberClient = clientFactory.CreateMqttClient();
        var subOptions = new MqttClientOptionsBuilder().WithTcpServer("localhost").WithClientId("bench-sub").Build();
        _subscriberClient.ConnectAsync(subOptions).GetAwaiter().GetResult();
        _subscriberClient.ApplicationMessageReceivedAsync += _ =>
        {
            _countdown.Signal();
            return Task.CompletedTask;
        };
        _subscriberClient.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter("bench/test", MqttQualityOfServiceLevel.AtLeastOnce)
                .Build())
            .GetAwaiter().GetResult();

        _publisherClient = clientFactory.CreateMqttClient();
        var pubOptions = new MqttClientOptionsBuilder().WithTcpServer("localhost").WithClientId("bench-pub").Build();
        _publisherClient.ConnectAsync(pubOptions).GetAwaiter().GetResult();

        _message = new MqttApplicationMessageBuilder()
            .WithTopic("bench/test")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _publisherClient?.DisconnectAsync().GetAwaiter().GetResult();
        _publisherClient?.Dispose();
        _subscriberClient?.DisconnectAsync().GetAwaiter().GetResult();
        _subscriberClient?.Dispose();
        _mqttServer?.StopAsync().GetAwaiter().GetResult();
        _mqttServer?.Dispose();
    }

    [Benchmark]
    public void Publish_And_Deliver_1000_Messages()
    {
        _countdown = new CountdownEvent(MessageCount);

        for (var i = 0; i < MessageCount; i++)
        {
            _publisherClient.PublishAsync(_message).GetAwaiter().GetResult();
        }

        _countdown.Wait(TimeSpan.FromSeconds(30));
        _countdown.Dispose();
    }
}