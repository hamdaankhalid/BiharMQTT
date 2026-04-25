using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

// Broker-agnostic MQTT v5 test client.
// Tests: fan-out pub/sub, topic lifecycle, concurrent producers/consumers.
// Usage: dotnet run [host] [port]
//   defaults: localhost 1883

var host = args.Length > 0 ? args[0] : "localhost";
var port = args.Length > 1 ? int.Parse(args[1]) : 1883;

Console.WriteLine($"MQTT Test Client — targeting {host}:{port}");
Console.WriteLine(new string('=', 50));

var passed = 0;
var failed = 0;

async Task RunTest(string name, Func<Task> test)
{
    Console.Write($"  [{name}] ... ");
    var sw = Stopwatch.StartNew();
    try
    {
        await test();
        sw.Stop();
        Console.WriteLine($"PASS ({sw.ElapsedMilliseconds}ms)");
        Interlocked.Increment(ref passed);
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"FAIL ({sw.ElapsedMilliseconds}ms)");
        Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
        Interlocked.Increment(ref failed);
    }
}

async Task<IMqttClient> Connect(string? clientId = null)
{
    var factory = new MqttFactory();
    var client = factory.CreateMqttClient();
    var opts = new MqttClientOptionsBuilder()
        .WithTcpServer(host, port)
        .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
        .WithCleanSession(true);

    if (clientId != null)
        opts.WithClientId(clientId);

    await client.ConnectAsync(opts.Build());
    return client;
}

// ─────────────────────────────────────────────────────────────────
// Test 1: Basic pub/sub — one publisher, one subscriber
// ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n[1] Basic pub/sub");

await RunTest("single message delivery", async () =>
{
    using var pub = await Connect("test1-pub");
    using var sub = await Connect("test1-sub");

    var received = new TaskCompletionSource<string>();
    sub.ApplicationMessageReceivedAsync += e =>
    {
        received.TrySetResult(Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
        return Task.CompletedTask;
    };

    await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("test/basic").Build());
    await Task.Delay(200); // let subscription propagate

    await pub.PublishAsync(new MqttApplicationMessageBuilder()
        .WithTopic("test/basic")
        .WithPayload("hello")
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
        .Build());

    var payload = await WaitFor(received.Task, TimeSpan.FromSeconds(5));
    Assert(payload == "hello", $"expected 'hello', got '{payload}'");

    await pub.DisconnectAsync();
    await sub.DisconnectAsync();
});

await RunTest("multiple messages in order", async () =>
{
    using var pub = await Connect("test1b-pub");
    using var sub = await Connect("test1b-sub");

    var messages = new ConcurrentQueue<string>();
    var allReceived = new TaskCompletionSource();
    const int count = 50;

    sub.ApplicationMessageReceivedAsync += e =>
    {
        messages.Enqueue(Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
        if (messages.Count >= count) allReceived.TrySetResult();
        return Task.CompletedTask;
    };

    await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("test/order").Build());
    await Task.Delay(200);

    for (int i = 0; i < count; i++)
    {
        await pub.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("test/order")
            .WithPayload($"msg-{i}")
            .Build());
    }

    await WaitForNonGeneric(allReceived.Task, TimeSpan.FromSeconds(10));
    var list = messages.ToArray();
    Assert(list.Length == count, $"expected {count} messages, got {list.Length}");
    for (int i = 0; i < count; i++)
        Assert(list[i] == $"msg-{i}", $"message {i} out of order: {list[i]}");

    await pub.DisconnectAsync();
    await sub.DisconnectAsync();
});

// ─────────────────────────────────────────────────────────────────
// Test 2: Fan-out — one publisher, multiple subscribers
// ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n[2] Fan-out (1 publisher → N subscribers)");

await RunTest("fan-out to 5 subscribers", async () =>
{
    const int numSubs = 5;
    const int numMessages = 20;
    var topic = "test/fanout";

    using var pub = await Connect("test2-pub");
    var subs = new List<IMqttClient>();
    var counters = new ConcurrentDictionary<int, int>();
    var allDone = new CountdownEvent(numSubs * numMessages);

    for (int i = 0; i < numSubs; i++)
    {
        var subIdx = i;
        var sub = await Connect($"test2-sub-{i}");
        counters[subIdx] = 0;

        sub.ApplicationMessageReceivedAsync += _ =>
        {
            counters.AddOrUpdate(subIdx, 1, (_, v) => v + 1);
            allDone.Signal();
            return Task.CompletedTask;
        };

        await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topic).Build());
        subs.Add(sub);
    }

    await Task.Delay(300);

    for (int i = 0; i < numMessages; i++)
    {
        await pub.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload($"fanout-{i}")
            .Build());
    }

    var done = allDone.Wait(TimeSpan.FromSeconds(10));
    Assert(done, $"timed out — received counts: {string.Join(", ", counters.Select(kv => $"sub{kv.Key}={kv.Value}"))}");

    foreach (var sub in subs) { await sub.DisconnectAsync(); sub.Dispose(); }
    await pub.DisconnectAsync();
});

// ─────────────────────────────────────────────────────────────────
// Test 3: Wildcard subscriptions
// ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n[3] Wildcard subscriptions");

await RunTest("single-level wildcard (+)", async () =>
{
    using var pub = await Connect("test3a-pub");
    using var sub = await Connect("test3a-sub");

    var received = new ConcurrentBag<string>();
    var done = new TaskCompletionSource();

    sub.ApplicationMessageReceivedAsync += e =>
    {
        received.Add(e.ApplicationMessage.Topic);
        if (received.Count >= 3) done.TrySetResult();
        return Task.CompletedTask;
    };

    await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("sensors/+/temp").Build());
    await Task.Delay(200);

    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("sensors/device1/temp").WithPayload("22").Build());
    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("sensors/device2/temp").WithPayload("23").Build());
    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("sensors/device3/temp").WithPayload("24").Build());

    await WaitForNonGeneric(done.Task, TimeSpan.FromSeconds(5));
    Assert(received.Count == 3, $"expected 3 wildcard matches, got {received.Count}");

    await pub.DisconnectAsync();
    await sub.DisconnectAsync();
});

await RunTest("multi-level wildcard (#)", async () =>
{
    using var pub = await Connect("test3b-pub");
    using var sub = await Connect("test3b-sub");

    var received = new ConcurrentBag<string>();
    var done = new TaskCompletionSource();

    sub.ApplicationMessageReceivedAsync += e =>
    {
        received.Add(e.ApplicationMessage.Topic);
        if (received.Count >= 3) done.TrySetResult();
        return Task.CompletedTask;
    };

    await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("home/#").Build());
    await Task.Delay(200);

    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("home/living/light").WithPayload("on").Build());
    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("home/kitchen/temp").WithPayload("21").Build());
    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("home/garage/door/status").WithPayload("closed").Build());

    await WaitForNonGeneric(done.Task, TimeSpan.FromSeconds(5));
    Assert(received.Count == 3, $"expected 3 multi-level matches, got {received.Count}");

    await pub.DisconnectAsync();
    await sub.DisconnectAsync();
});

// ─────────────────────────────────────────────────────────────────
// Test 4: Topic lifecycle — subscribe, message, unsubscribe, verify no delivery
// ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n[4] Topic lifecycle (subscribe → unsubscribe)");

await RunTest("unsubscribe stops delivery", async () =>
{
    using var pub = await Connect("test4-pub");
    using var sub = await Connect("test4-sub");

    var received = new ConcurrentQueue<string>();
    sub.ApplicationMessageReceivedAsync += e =>
    {
        received.Enqueue(e.ApplicationMessage.Topic);
        return Task.CompletedTask;
    };

    await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("lifecycle/topicA").Build());
    await Task.Delay(200);

    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("lifecycle/topicA").WithPayload("before").Build());
    await Task.Delay(300);
    Assert(received.Count == 1, $"expected 1 message before unsub, got {received.Count}");

    await sub.UnsubscribeAsync("lifecycle/topicA");
    await Task.Delay(200);

    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("lifecycle/topicA").WithPayload("after").Build());
    await Task.Delay(500);
    Assert(received.Count == 1, $"expected still 1 message after unsub, got {received.Count}");

    await pub.DisconnectAsync();
    await sub.DisconnectAsync();
});

await RunTest("subscribe to new topic after unsubscribe", async () =>
{
    using var pub = await Connect("test4b-pub");
    using var sub = await Connect("test4b-sub");

    var received = new ConcurrentQueue<string>();
    sub.ApplicationMessageReceivedAsync += e =>
    {
        received.Enqueue(Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
        return Task.CompletedTask;
    };

    await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("lifecycle/old").Build());
    await Task.Delay(200);

    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("lifecycle/old").WithPayload("old-msg").Build());
    await Task.Delay(300);

    await sub.UnsubscribeAsync("lifecycle/old");
    await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("lifecycle/new").Build());
    await Task.Delay(200);

    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("lifecycle/old").WithPayload("should-not-arrive").Build());
    await pub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("lifecycle/new").WithPayload("new-msg").Build());
    await Task.Delay(500);

    var msgs = received.ToArray();
    Assert(msgs.Length == 2, $"expected 2 messages total, got {msgs.Length}");
    Assert(msgs[0] == "old-msg", $"first should be 'old-msg', got '{msgs[0]}'");
    Assert(msgs[1] == "new-msg", $"second should be 'new-msg', got '{msgs[1]}'");

    await pub.DisconnectAsync();
    await sub.DisconnectAsync();
});

// ─────────────────────────────────────────────────────────────────
// Test 5: Concurrent producers on different topics
// ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n[5] Concurrent producers");

await RunTest("10 producers, 1 subscriber on wildcard", async () =>
{
    const int numProducers = 10;
    const int msgsPerProducer = 50;
    var totalExpected = numProducers * msgsPerProducer;

    using var sub = await Connect("test5-sub");
    var count = 0;
    var allDone = new TaskCompletionSource();

    sub.ApplicationMessageReceivedAsync += _ =>
    {
        if (Interlocked.Increment(ref count) >= totalExpected)
            allDone.TrySetResult();
        return Task.CompletedTask;
    };

    await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("concurrent/#").Build());
    await Task.Delay(300);

    var tasks = Enumerable.Range(0, numProducers).Select(async i =>
    {
        using var pub = await Connect($"test5-pub-{i}");
        for (int j = 0; j < msgsPerProducer; j++)
        {
            await pub.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"concurrent/producer-{i}")
                .WithPayload($"{i}-{j}")
                .Build());
        }
        await pub.DisconnectAsync();
    });

    await Task.WhenAll(tasks);
    await WaitForNonGeneric(allDone.Task, TimeSpan.FromSeconds(15));
    Assert(count >= totalExpected, $"expected {totalExpected} messages, got {count}");

    await sub.DisconnectAsync();
});

// ─────────────────────────────────────────────────────────────────
// Test 6: Concurrent consumers on the same topic
// ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n[6] Concurrent consumers on same topic");

await RunTest("1 producer, 10 consumers", async () =>
{
    const int numConsumers = 10;
    const int numMessages = 30;
    var topic = "shared/topic";
    var counters = new ConcurrentDictionary<int, int>();
    var allDone = new CountdownEvent(numConsumers * numMessages);

    var consumers = new List<IMqttClient>();
    for (int i = 0; i < numConsumers; i++)
    {
        var idx = i;
        var consumer = await Connect($"test6-con-{i}");
        counters[idx] = 0;
        consumer.ApplicationMessageReceivedAsync += _ =>
        {
            counters.AddOrUpdate(idx, 1, (_, v) => v + 1);
            allDone.Signal();
            return Task.CompletedTask;
        };
        await consumer.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topic).Build());
        consumers.Add(consumer);
    }
    await Task.Delay(300);

    using var pub = await Connect("test6-pub");
    for (int i = 0; i < numMessages; i++)
    {
        await pub.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload($"shared-{i}")
            .Build());
    }

    var ok = allDone.Wait(TimeSpan.FromSeconds(15));
    Assert(ok, $"timed out — received: {string.Join(", ", counters.Select(kv => $"con{kv.Key}={kv.Value}"))}");

    foreach (var c in consumers)
    {
        Assert(counters.TryGetValue(consumers.IndexOf(c), out var n) && n == numMessages,
            $"consumer {consumers.IndexOf(c)} got {counters.GetValueOrDefault(consumers.IndexOf(c))} instead of {numMessages}");
        await c.DisconnectAsync();
        c.Dispose();
    }
    await pub.DisconnectAsync();
});

// ─────────────────────────────────────────────────────────────────
// Test 7: Large payload
// ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n[7] Large payloads");

await RunTest("12KB payload", async () =>
{
    using var pub = await Connect("test7-pub");
    using var sub = await Connect("test7-sub");

    var payload = new byte[12 * 1024];
    Random.Shared.NextBytes(payload);

    var received = new TaskCompletionSource<byte[]>();
    sub.ApplicationMessageReceivedAsync += e =>
    {
        received.TrySetResult(e.ApplicationMessage.PayloadSegment.ToArray());
        return Task.CompletedTask;
    };

    await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("test/large").Build());
    await Task.Delay(200);

    await pub.PublishAsync(new MqttApplicationMessageBuilder()
        .WithTopic("test/large")
        .WithPayload(payload)
        .Build());

    var result = await WaitFor(received.Task, TimeSpan.FromSeconds(10));
    Assert(result.Length == payload.Length, $"payload size mismatch: {result.Length} vs {payload.Length}");
    Assert(result.SequenceEqual(payload), "payload content mismatch");

    await pub.DisconnectAsync();
    await sub.DisconnectAsync();
});

// ─────────────────────────────────────────────────────────────────
// Test 8: Rapid connect/disconnect cycles
// ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n[8] Connection churn");

await RunTest("50 rapid connect/disconnect cycles", async () =>
{
    var factory = new MqttFactory();
    for (int i = 0; i < 50; i++)
    {
        var client = factory.CreateMqttClient();
        var opts = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithCleanSession(true)
            .Build();

        await client.ConnectAsync(opts);
        Assert(client.IsConnected, $"iteration {i}: not connected after ConnectAsync");
        await client.DisconnectAsync();
        client.Dispose();
    }
});

// ─────────────────────────────────────────────────────────────────
// Test 9: QoS 1 (at-least-once)
// ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n[9] QoS levels");

await RunTest("QoS 1 (at least once)", async () =>
{
    using var pub = await Connect("test9-pub");
    using var sub = await Connect("test9-sub");

    var received = new TaskCompletionSource<string>();
    sub.ApplicationMessageReceivedAsync += e =>
    {
        received.TrySetResult(Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
        return Task.CompletedTask;
    };

    await sub.SubscribeAsync(new MqttTopicFilterBuilder()
        .WithTopic("test/qos1")
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .Build());
    await Task.Delay(200);

    await pub.PublishAsync(new MqttApplicationMessageBuilder()
        .WithTopic("test/qos1")
        .WithPayload("qos1-msg")
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .Build());

    var msg = await WaitFor(received.Task, TimeSpan.FromSeconds(5));
    Assert(msg == "qos1-msg", $"expected 'qos1-msg', got '{msg}'");

    await pub.DisconnectAsync();
    await sub.DisconnectAsync();
});

// ─────────────────────────────────────────────────────────────────
// Summary
// ─────────────────────────────────────────────────────────────────
Console.WriteLine($"\n{new string('=', 50)}");
Console.WriteLine($"Results: {passed} passed, {failed} failed");
Console.WriteLine(new string('=', 50));

return failed > 0 ? 1 : 0;

// ── Helpers ──────────────────────────────────────────────────────

static async Task<T> WaitFor<T>(Task<T> task, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
    if (completed != task)
        throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds}s");
    return await task;
}

static async Task WaitForNonGeneric(Task task, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
    if (completed != task)
        throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds}s");
    await task;
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException($"Assertion failed: {message}");
}
