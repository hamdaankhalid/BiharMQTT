// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Internal;
using BiharMQTT.Protocol;
using BiharMQTT.Server;

namespace BiharMQTT.Tests.Server;

[TestClass]
public sealed class RingBuffer_Integration_Tests : BaseTestClass
{
    [TestMethod]
    public async Task Inject_Via_RingBuffer_Delivers_To_Subscriber()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder.WithRingBuffer(capacityBytes: 1024 * 1024, maxSlots: 256));

        var receiver = await testEnvironment.ConnectClient();
        var messageHandler = testEnvironment.CreateApplicationMessageHandler(receiver);
        await receiver.SubscribeAsync("#");

        await server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = "ring/test",
                Payload = "hello ring buffer"u8.ToArray()
            });

        await LongTestDelay();

        Assert.HasCount(1, messageHandler.ReceivedEventArgs);
        Assert.AreEqual("ring/test", messageHandler.ReceivedEventArgs[0].ApplicationMessage.Topic);
    }

    [TestMethod]
    public async Task Inject_Multiple_Messages_Via_RingBuffer()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder.WithRingBuffer(capacityBytes: 1024 * 1024, maxSlots: 256));

        var receiver = await testEnvironment.ConnectClient();
        var messageHandler = testEnvironment.CreateApplicationMessageHandler(receiver);
        await receiver.SubscribeAsync("#");

        for (var i = 0; i < 10; i++)
        {
            await server.InjectApplicationMessage(
                new MqttBufferedApplicationMessage
                {
                    Topic = $"ring/msg{i}",
                    Payload = System.Text.Encoding.UTF8.GetBytes($"payload {i}")
                });
        }

        await LongTestDelay();

        Assert.HasCount(10, messageHandler.ReceivedEventArgs);
    }

    [TestMethod]
    public async Task RingBuffer_With_Interceptor_Falls_Back_Gracefully()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder.WithRingBuffer(capacityBytes: 1024 * 1024, maxSlots: 256));

        MqttApplicationMessage intercepted = null;
        server.InterceptingPublishAsync += args =>
        {
            intercepted = args.ApplicationMessage;
            return CompletedTask.Instance;
        };

        var receiver = await testEnvironment.ConnectClient();
        var messageHandler = testEnvironment.CreateApplicationMessageHandler(receiver);
        await receiver.SubscribeAsync("#");

        await server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = "ring/intercepted",
                Payload = "data"u8.ToArray()
            });

        await LongTestDelay();

        // Should fall back to standard path and interceptor should work
        Assert.IsNotNull(intercepted);
        Assert.AreEqual("ring/intercepted", intercepted.Topic);
        Assert.HasCount(1, messageHandler.ReceivedEventArgs);
    }

    [TestMethod]
    public async Task Client_Publish_Uses_RingBuffer_When_Enabled()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder.WithRingBuffer(capacityBytes: 1024 * 1024, maxSlots: 256));

        var sender = await testEnvironment.ConnectClient();
        var receiver = await testEnvironment.ConnectClient();
        var messageHandler = testEnvironment.CreateApplicationMessageHandler(receiver);
        await receiver.SubscribeAsync("#");

        await sender.PublishStringAsync("client/pub", "from client");

        await LongTestDelay();

        Assert.HasCount(1, messageHandler.ReceivedEventArgs);
        Assert.AreEqual("client/pub", messageHandler.ReceivedEventArgs[0].ApplicationMessage.Topic);
    }

    [TestMethod]
    public async Task Client_Publish_Multiple_Via_RingBuffer_Direct_Path()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder.WithRingBuffer(capacityBytes: 1024 * 1024, maxSlots: 256));

        var sender = await testEnvironment.ConnectClient();
        var receiver = await testEnvironment.ConnectClient();
        var messageHandler = testEnvironment.CreateApplicationMessageHandler(receiver);
        await receiver.SubscribeAsync("#");

        const int messageCount = 20;
        for (var i = 0; i < messageCount; i++)
        {
            await sender.PublishStringAsync($"direct/msg{i}", $"payload {i}", BiharMQTT.Protocol.MqttQualityOfServiceLevel.AtMostOnce);
        }

        await LongTestDelay();
        await LongTestDelay();

        Assert.HasCount(messageCount, messageHandler.ReceivedEventArgs);
    }

    [TestMethod]
    public async Task RingBuffer_Handles_Empty_Payload()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder.WithRingBuffer(capacityBytes: 1024 * 1024, maxSlots: 256));

        var receiver = await testEnvironment.ConnectClient();
        var messageHandler = testEnvironment.CreateApplicationMessageHandler(receiver);
        await receiver.SubscribeAsync("#");

        await server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = "ring/empty"
            });

        await LongTestDelay();

        Assert.HasCount(1, messageHandler.ReceivedEventArgs);
        Assert.AreEqual("ring/empty", messageHandler.ReceivedEventArgs[0].ApplicationMessage.Topic);
        Assert.AreEqual(0, messageHandler.ReceivedEventArgs[0].ApplicationMessage.Payload.Length);
    }

    [TestMethod]
    public async Task RingBuffer_Multiple_Subscribers_Receive_Message()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder.WithRingBuffer(capacityBytes: 1024 * 1024, maxSlots: 256));

        var receiver1 = await testEnvironment.ConnectClient();
        var receiver2 = await testEnvironment.ConnectClient();
        var handler1 = testEnvironment.CreateApplicationMessageHandler(receiver1);
        var handler2 = testEnvironment.CreateApplicationMessageHandler(receiver2);
        await receiver1.SubscribeAsync("ring/#");
        await receiver2.SubscribeAsync("ring/#");

        await server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = "ring/fanout",
                Payload = "fan out payload"u8.ToArray()
            });

        await LongTestDelay();

        Assert.HasCount(1, handler1.ReceivedEventArgs);
        Assert.HasCount(1, handler2.ReceivedEventArgs);
        Assert.AreEqual("ring/fanout", handler1.ReceivedEventArgs[0].ApplicationMessage.Topic);
        Assert.AreEqual("ring/fanout", handler2.ReceivedEventArgs[0].ApplicationMessage.Topic);
    }

    [TestMethod]
    public async Task Buffered_Interceptor_Receives_Payload_View()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder.WithRingBuffer(capacityBytes: 1024 * 1024, maxSlots: 256));

        string interceptedTopic = null;
        byte[] interceptedPayloadCopy = null;

        server.InterceptingPublishBufferedAsync += (ref InterceptingPublishBufferedEventArgs args) =>
        {
            interceptedTopic = args.Topic;
            interceptedPayloadCopy = args.Payload.ToArray();
        };

        var receiver = await testEnvironment.ConnectClient();
        var messageHandler = testEnvironment.CreateApplicationMessageHandler(receiver);
        await receiver.SubscribeAsync("#");

        await server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = "ring/intercept",
                Payload = "interceptor test"u8.ToArray()
            });

        await LongTestDelay();

        Assert.AreEqual("ring/intercept", interceptedTopic);
        Assert.IsTrue("interceptor test"u8.SequenceEqual(interceptedPayloadCopy));
        Assert.HasCount(1, messageHandler.ReceivedEventArgs);
    }

    [TestMethod]
    public async Task Buffered_Interceptor_Can_Block_Publish()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder.WithRingBuffer(capacityBytes: 1024 * 1024, maxSlots: 256));

        server.InterceptingPublishBufferedAsync += (ref InterceptingPublishBufferedEventArgs args) =>
        {
            args.ProcessPublish = false;
        };

        var receiver = await testEnvironment.ConnectClient();
        var messageHandler = testEnvironment.CreateApplicationMessageHandler(receiver);
        await receiver.SubscribeAsync("#");

        await server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = "ring/blocked",
                Payload = "should not arrive"u8.ToArray()
            });

        await LongTestDelay();

        Assert.IsEmpty(messageHandler.ReceivedEventArgs);
    }
}
