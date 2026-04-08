using BiharMQTT.Internal;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;
using BiharMQTT.Server;
using BiharMQTT.Server.Exceptions;

namespace BiharMQTT.Tests.Server;

// ReSharper disable InconsistentNaming
[TestClass]
public sealed class Injection_Tests : BaseTestClass
{
    [TestMethod]
    public async Task Enqueue_Application_Message_At_Session_Level()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer();
        var receiver1 = await testEnvironment.ConnectClient();
        var receiver2 = await testEnvironment.ConnectClient();
        var messageReceivedHandler1 = testEnvironment.CreateApplicationMessageHandler(receiver1);
        var messageReceivedHandler2 = testEnvironment.CreateApplicationMessageHandler(receiver2);

        var status = await server.GetSessionsAsync();
        var clientStatus = status[0];

        await receiver1.SubscribeAsync("#");
        await receiver2.SubscribeAsync("#");

        var message = new MqttApplicationMessageBuilder()
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopic("InjectedOne").Build();

        var enqueued = clientStatus.TryEnqueueApplicationMessage(message, out var injectResult);

        Assert.IsTrue(enqueued);

        await LongTestDelay();

        Assert.HasCount(1, messageReceivedHandler1.ReceivedEventArgs);
        Assert.AreEqual(injectResult.PacketIdentifier, messageReceivedHandler1.ReceivedEventArgs[0].PacketIdentifier);
        Assert.AreEqual("InjectedOne", messageReceivedHandler1.ReceivedEventArgs[0].ApplicationMessage.Topic);

        // The second receiver should NOT receive the message.
        Assert.IsEmpty(messageReceivedHandler2.ReceivedEventArgs);
    }

    [TestMethod]
    public async Task Enqueue_Application_Message_At_Session_Level_QueueOverflow_DropNewMessageStrategy()
    {
        using var testEnvironment = CreateTestEnvironment(trackUnobservedTaskException: false);

        var server = await testEnvironment.StartServer(
            builder => builder
                .WithMaxPendingMessagesPerClient(1)
                .WithPendingMessagesOverflowStrategy(MqttPendingMessagesOverflowStrategy.DropNewMessage));

        var receiver = await testEnvironment.ConnectClient();

        var firstMessageOutboundPacketInterceptedTcs = new TaskCompletionSource();
        server.InterceptingOutboundPacketAsync += async args =>
        {
            // - The first message is dequeued normally and calls this delay
            // - The second message fills the outbound queue
            // - The third message overflows the outbound queue
            if (args.Packet is MqttPublishPacket)
            {
                firstMessageOutboundPacketInterceptedTcs.SetResult();
                await Task.Delay(TimeSpan.FromDays(1), args.CancellationToken);
            }
        };

        var firstMessageEvicted = false;
        var secondMessageEvicted = false;
        var thirdMessageEvicted = false;

        server.QueuedApplicationMessageOverwrittenAsync += args =>
        {
            if (args.Packet is not MqttPublishPacket publishPacket)
            {
                return Task.CompletedTask;
            }

            switch (publishPacket.Topic)
            {
                case "InjectedOne":
                    firstMessageEvicted = true;
                    break;
                case "InjectedTwo":
                    secondMessageEvicted = true;
                    break;
                case "InjectedThree":
                    thirdMessageEvicted = true;
                    break;
            }

            return Task.CompletedTask;
        };

        var status = await server.GetSessionsAsync();
        var clientStatus = status[0];
        await receiver.SubscribeAsync("#");

        var firstMessageEnqueued = clientStatus.TryEnqueueApplicationMessage(
            new MqttApplicationMessageBuilder().WithTopic("InjectedOne").Build(), out _);
        await firstMessageOutboundPacketInterceptedTcs.Task;

        var secondMessageEnqueued = clientStatus.TryEnqueueApplicationMessage(
            new MqttApplicationMessageBuilder().WithTopic("InjectedTwo").Build(), out _);

        var thirdMessageEnqueued = clientStatus.TryEnqueueApplicationMessage(
            new MqttApplicationMessageBuilder().WithTopic("InjectedThree").Build(), out _);

        // Due to the DropNewMessage strategy the third message will not be enqueued.
        // As a result, no existing messages in the queue will be dropped (evicted).
        Assert.IsTrue(firstMessageEnqueued);
        Assert.IsTrue(secondMessageEnqueued);
        Assert.IsFalse(thirdMessageEnqueued);

        Assert.IsFalse(firstMessageEvicted);
        Assert.IsFalse(secondMessageEvicted);
        Assert.IsFalse(thirdMessageEvicted);
    }


    [TestMethod]
    public async Task Enqueue_Application_Message_At_Session_Level_QueueOverflow_DropOldestQueuedMessageStrategy()
    {
        using var testEnvironment = CreateTestEnvironment(trackUnobservedTaskException: false);

        var server = await testEnvironment.StartServer(
            builder => builder
                .WithMaxPendingMessagesPerClient(1)
                .WithPendingMessagesOverflowStrategy(MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage));

        var receiver = await testEnvironment.ConnectClient();

        var firstMessageOutboundPacketInterceptedTcs = new TaskCompletionSource();
        server.InterceptingOutboundPacketAsync += async args =>
        {
            // - The first message is dequeued normally and calls this delay
            // - The second message fills the outbound queue
            // - The third message overflows the outbound queue
            if (args.Packet is MqttPublishPacket)
            {
                firstMessageOutboundPacketInterceptedTcs.SetResult();
                await Task.Delay(TimeSpan.FromDays(1), args.CancellationToken);
            }
        };

        var firstMessageEvicted = false;
        var secondMessageEvicted = false;
        var thirdMessageEvicted = false;

        server.QueuedApplicationMessageOverwrittenAsync += args =>
        {
            if (args.Packet is not MqttPublishPacket publishPacket)
            {
                return Task.CompletedTask;
            }

            switch (publishPacket.Topic)
            {
                case "InjectedOne":
                    firstMessageEvicted = true;
                    break;
                case "InjectedTwo":
                    secondMessageEvicted = true;
                    break;
                case "InjectedThree":
                    thirdMessageEvicted = true;
                    break;
            }

            return Task.CompletedTask;
        };

        var status = await server.GetSessionsAsync();
        var clientStatus = status[0];
        await receiver.SubscribeAsync("#");

        var firstMessageEnqueued = clientStatus.TryEnqueueApplicationMessage(
            new MqttApplicationMessageBuilder().WithTopic("InjectedOne").Build(), out _);
        await firstMessageOutboundPacketInterceptedTcs.Task;

        var secondMessageEnqueued = clientStatus.TryEnqueueApplicationMessage(
            new MqttApplicationMessageBuilder().WithTopic("InjectedTwo").Build(), out _);

        var thirdMessageEnqueued = clientStatus.TryEnqueueApplicationMessage(
            new MqttApplicationMessageBuilder().WithTopic("InjectedThree").Build(), out _);

        // Due to the DropOldestQueuedMessage strategy, all messages will be enqueued initially.
        // But the second message will eventually be dropped (evicted) to make room for the third one.
        Assert.IsTrue(firstMessageEnqueued);
        Assert.IsTrue(secondMessageEnqueued);
        Assert.IsTrue(thirdMessageEnqueued);

        Assert.IsFalse(firstMessageEvicted);
        Assert.IsTrue(secondMessageEvicted);
        Assert.IsFalse(thirdMessageEvicted);
    }

    [TestMethod]
    public async Task Deliver_Application_Message_At_Session_Level()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer();
        var receiver1 = await testEnvironment.ConnectClient();
        var receiver2 = await testEnvironment.ConnectClient();
        var messageReceivedHandler1 = testEnvironment.CreateApplicationMessageHandler(receiver1);
        var messageReceivedHandler2 = testEnvironment.CreateApplicationMessageHandler(receiver2);

        var status = await server.GetSessionsAsync();
        var clientStatus = status[0];

        await receiver1.SubscribeAsync("#");
        await receiver2.SubscribeAsync("#");

        var mqttApplicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic("InjectedOne")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        var injectResult = await clientStatus.DeliverApplicationMessageAsync(mqttApplicationMessage);

        await LongTestDelay();

        Assert.HasCount(1, messageReceivedHandler1.ReceivedEventArgs);
        Assert.AreEqual(injectResult.PacketIdentifier, messageReceivedHandler1.ReceivedEventArgs[0].PacketIdentifier);
        Assert.AreEqual("InjectedOne", messageReceivedHandler1.ReceivedEventArgs[0].ApplicationMessage.Topic);

        // The second receiver should NOT receive the message.
        Assert.IsEmpty(messageReceivedHandler2.ReceivedEventArgs);
    }

    [TestMethod]
    public async Task Deliver_Application_Message_At_Session_Level_QueueOverflow_DropNewMessageStrategy()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer(
            builder => builder
                .WithMaxPendingMessagesPerClient(1)
                .WithPendingMessagesOverflowStrategy(MqttPendingMessagesOverflowStrategy.DropNewMessage));

        var receiver = await testEnvironment.ConnectClient();

        var firstMessageOutboundPacketInterceptedTcs = new TaskCompletionSource();
        server.InterceptingOutboundPacketAsync += async args =>
        {
            // - The first message is dequeued normally and calls this delay
            // - The second message fills the outbound queue
            // - The third message overflows the outbound queue
            if (args.Packet is MqttPublishPacket)
            {
                firstMessageOutboundPacketInterceptedTcs.SetResult();
                await Task.Delay(TimeSpan.FromDays(1), args.CancellationToken);
            }
        };

        var firstMessageEvicted = false;
        var secondMessageEvicted = false;
        var thirdMessageEvicted = false;

        server.QueuedApplicationMessageOverwrittenAsync += args =>
        {
            if (args.Packet is not MqttPublishPacket publishPacket)
            {
                return Task.CompletedTask;
            }

            switch (publishPacket.Topic)
            {
                case "InjectedOne":
                    firstMessageEvicted = true;
                    break;
                case "InjectedTwo":
                    secondMessageEvicted = true;
                    break;
                case "InjectedThree":
                    thirdMessageEvicted = true;
                    break;
            }

            return Task.CompletedTask;
        };

        var status = await server.GetSessionsAsync();
        var clientStatus = status[0];
        await receiver.SubscribeAsync("#");

        var firstMessageTask = Task.Run(
            () => clientStatus.DeliverApplicationMessageAsync(
                new MqttApplicationMessageBuilder().WithTopic("InjectedOne").Build()));
        await LongTestDelay();
        await firstMessageOutboundPacketInterceptedTcs.Task;

        var secondMessageTask = Task.Run(
            () => clientStatus.DeliverApplicationMessageAsync(
                new MqttApplicationMessageBuilder().WithTopic("InjectedTwo").Build()));
        await LongTestDelay();

        var thirdMessageTask = Task.Run(
            () => clientStatus.DeliverApplicationMessageAsync(
                new MqttApplicationMessageBuilder().WithTopic("InjectedThree").Build()));
        await LongTestDelay();

        Task.WaitAny(firstMessageTask, secondMessageTask, thirdMessageTask);

        // Due to the DropNewMessage strategy the third message delivery will fail.
        // As a result, no existing messages in the queue will be dropped (evicted).
        Assert.AreEqual(TaskStatus.WaitingForActivation, firstMessageTask.Status);
        Assert.AreEqual(TaskStatus.WaitingForActivation, secondMessageTask.Status);
        Assert.AreEqual(TaskStatus.Faulted, thirdMessageTask.Status);
        Assert.IsTrue(thirdMessageTask.Exception?.InnerException is MqttPendingMessagesOverflowException);

        Assert.IsFalse(firstMessageEvicted);
        Assert.IsFalse(secondMessageEvicted);
        Assert.IsFalse(thirdMessageEvicted);
    }

    [TestMethod]
    public async Task Deliver_Application_Message_At_Session_Level_QueueOverflow_DropOldestQueuedMessageStrategy()
    {
        using var testEnvironment = CreateTestEnvironment(trackUnobservedTaskException: false);

        var server = await testEnvironment.StartServer(
            builder => builder
                .WithMaxPendingMessagesPerClient(1)
                .WithPendingMessagesOverflowStrategy(MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage));

        var receiver = await testEnvironment.ConnectClient();

        var firstMessageOutboundPacketInterceptedTcs = new TaskCompletionSource();
        server.InterceptingOutboundPacketAsync += async args =>
        {
            // - The first message is dequeued normally and calls this delay
            // - The second message fills the outbound queue
            // - The third message overflows the outbound queue
            if (args.Packet is MqttPublishPacket)
            {
                firstMessageOutboundPacketInterceptedTcs.SetResult();
                await Task.Delay(TimeSpan.FromDays(1), args.CancellationToken);
            }
        };

        var firstMessageEvicted = false;
        var secondMessageEvicted = false;
        var thirdMessageEvicted = false;

        server.QueuedApplicationMessageOverwrittenAsync += args =>
        {
            if (args.Packet is not MqttPublishPacket publishPacket)
            {
                return Task.CompletedTask;
            }

            switch (publishPacket.Topic)
            {
                case "InjectedOne":
                    firstMessageEvicted = true;
                    break;
                case "InjectedTwo":
                    secondMessageEvicted = true;
                    break;
                case "InjectedThree":
                    thirdMessageEvicted = true;
                    break;
            }

            return Task.CompletedTask;
        };

        var status = await server.GetSessionsAsync();
        var clientStatus = status[0];
        await receiver.SubscribeAsync("#");

        var firstMessageTask = Task.Run(
            () => clientStatus.DeliverApplicationMessageAsync(
                new MqttApplicationMessageBuilder().WithTopic("InjectedOne").Build()));
        await LongTestDelay();
        await firstMessageOutboundPacketInterceptedTcs.Task;

        var secondMessageTask = Task.Run(
            () => clientStatus.DeliverApplicationMessageAsync(
                new MqttApplicationMessageBuilder().WithTopic("InjectedTwo").Build()));
        await LongTestDelay();

        var thirdMessageTask = Task.Run(
            () => clientStatus.DeliverApplicationMessageAsync(
                new MqttApplicationMessageBuilder().WithTopic("InjectedThree").Build()));
        await LongTestDelay();

        Task.WaitAny(firstMessageTask, secondMessageTask, thirdMessageTask);

        // Due to the DropOldestQueuedMessage strategy, the second message delivery will fail
        // to make room for the third one.
        Assert.AreEqual(TaskStatus.WaitingForActivation, firstMessageTask.Status);
        Assert.AreEqual(TaskStatus.Faulted, secondMessageTask.Status);
        Assert.IsTrue(secondMessageTask.Exception?.InnerException is MqttPendingMessagesOverflowException);
        Assert.AreEqual(TaskStatus.WaitingForActivation, thirdMessageTask.Status);

        Assert.IsFalse(firstMessageEvicted);
        Assert.IsTrue(secondMessageEvicted);
        Assert.IsFalse(thirdMessageEvicted);
    }

    [TestMethod]
    public async Task Inject_ApplicationMessage_At_Server_Level()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer();

        var receiver = await testEnvironment.ConnectClient();

        var messageReceivedHandler = testEnvironment.CreateApplicationMessageHandler(receiver);

        await receiver.SubscribeAsync("#");

        var injectedApplicationMessage = new MqttApplicationMessageBuilder().WithTopic("InjectedOne").Build();

        await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(injectedApplicationMessage));

        await LongTestDelay();

        Assert.HasCount(1, messageReceivedHandler.ReceivedEventArgs);
        Assert.AreEqual("InjectedOne", messageReceivedHandler.ReceivedEventArgs[0].ApplicationMessage.Topic);
    }

    [TestMethod]
    public async Task Intercept_Injected_Application_Message()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer();

        MqttApplicationMessage interceptedMessage = null;
        server.InterceptingPublishAsync += eventArgs =>
        {
            interceptedMessage = eventArgs.ApplicationMessage;
            return CompletedTask.Instance;
        };

        var injectedApplicationMessage = new MqttApplicationMessageBuilder().WithTopic("InjectedOne").Build();
        await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(injectedApplicationMessage));

        await LongTestDelay();

        Assert.IsNotNull(interceptedMessage);
    }

    [TestMethod]
    public async Task Inject_Buffered_ApplicationMessage_At_Server_Level()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer();

        var receiver = await testEnvironment.ConnectClient();

        var messageReceivedHandler = testEnvironment.CreateApplicationMessageHandler(receiver);

        await receiver.SubscribeAsync("#");

        var payload = "hello"u8.ToArray();

        await server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = "InjectedBuffered",
                Payload = payload,
                QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce
            });

        await LongTestDelay();

        Assert.HasCount(1, messageReceivedHandler.ReceivedEventArgs);
        Assert.AreEqual("InjectedBuffered", messageReceivedHandler.ReceivedEventArgs[0].ApplicationMessage.Topic);
    }

    [TestMethod]
    public async Task Inject_Buffered_ApplicationMessage_With_Pooled_Memory()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer();

        var receiver = await testEnvironment.ConnectClient();

        var messageReceivedHandler = testEnvironment.CreateApplicationMessageHandler(receiver);

        await receiver.SubscribeAsync("#");

        // Simulate pooled buffer usage
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var buffer = pool.Rent(64);
        var written = System.Text.Encoding.UTF8.GetBytes("pooled payload", buffer);

        await server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = "PooledTopic",
                Payload = buffer.AsMemory(0, written)
            });

        // Buffer can be returned immediately after inject completes
        pool.Return(buffer);

        await LongTestDelay();

        Assert.HasCount(1, messageReceivedHandler.ReceivedEventArgs);
        Assert.AreEqual("PooledTopic", messageReceivedHandler.ReceivedEventArgs[0].ApplicationMessage.Topic);
    }

    [TestMethod]
    public async Task Inject_Buffered_ApplicationMessage_With_Builder()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer();

        var receiver = await testEnvironment.ConnectClient();

        var messageReceivedHandler = testEnvironment.CreateApplicationMessageHandler(receiver);

        await receiver.SubscribeAsync("#");

        var builder = new MqttBufferedApplicationMessageBuilder()
            .WithTopic("BuilderTopic")
            .WithPayload("builder payload"u8.ToArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        await server.InjectApplicationMessage(builder.Build());

        await LongTestDelay();

        Assert.HasCount(1, messageReceivedHandler.ReceivedEventArgs);
        Assert.AreEqual("BuilderTopic", messageReceivedHandler.ReceivedEventArgs[0].ApplicationMessage.Topic);
    }

    [TestMethod]
    public async Task Inject_Buffered_ApplicationMessage_Falls_Back_With_Interceptor()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer();

        MqttApplicationMessage interceptedMessage = null;
        server.InterceptingPublishAsync += eventArgs =>
        {
            interceptedMessage = eventArgs.ApplicationMessage;
            return CompletedTask.Instance;
        };

        var receiver = await testEnvironment.ConnectClient();
        var messageReceivedHandler = testEnvironment.CreateApplicationMessageHandler(receiver);
        await receiver.SubscribeAsync("#");

        await server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = "InterceptedBuffered",
                Payload = "test"u8.ToArray()
            });

        await LongTestDelay();

        // The interceptor should have received a materialized MqttApplicationMessage
        Assert.IsNotNull(interceptedMessage);
        Assert.AreEqual("InterceptedBuffered", interceptedMessage.Topic);

        // And the subscriber should still receive the message
        Assert.HasCount(1, messageReceivedHandler.ReceivedEventArgs);
    }

    [TestMethod]
    public async Task Inject_Buffered_ApplicationMessage_Extension_Method()
    {
        using var testEnvironment = CreateTestEnvironment();

        var server = await testEnvironment.StartServer();

        var receiver = await testEnvironment.ConnectClient();

        var messageReceivedHandler = testEnvironment.CreateApplicationMessageHandler(receiver);

        await receiver.SubscribeAsync("#");

        var payload = System.Text.Encoding.UTF8.GetBytes("extension payload");
        await server.InjectApplicationMessage("ExtTopic", (ReadOnlyMemory<byte>)payload);

        await LongTestDelay();

        Assert.HasCount(1, messageReceivedHandler.ReceivedEventArgs);
        Assert.AreEqual("ExtTopic", messageReceivedHandler.ReceivedEventArgs[0].ApplicationMessage.Topic);
    }
}