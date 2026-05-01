using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

// Broker-agnostic MQTT v5 test client.
// Tests: fan-out pub/sub, topic lifecycle, concurrent producers/consumers.
// Usage: dotnet run [host] [port] [--tls] [--smoke]
//   defaults: localhost 1883
//   --tls   : wrap connection in TLS (accepts self-signed certs — for local sample broker)
//   --smoke : run a quick pub/sub roundtrip instead of the full bombard test

internal class Program
{
    static MqttFactory factory = new MqttFactory();
    static bool useTls;

    static async Task RunTest(string name, Func<Task> test, Action onPassed, Action onFailed)
    {
        Console.Write($"  [{name}] ... ");
        var sw = Stopwatch.StartNew();
        try
        {
            await test();
            sw.Stop();
            Console.WriteLine($"PASS ({sw.ElapsedMilliseconds}ms)");
            onPassed();
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"FAIL ({sw.ElapsedMilliseconds}ms)");
            Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
            onFailed();
        }
    }

    static async Task<IMqttClient> Connect(string host, int port, string? clientId = null)
    {
        var client = factory.CreateMqttClient();
        var optsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithCleanSession(true);

        if (clientId != null)
            optsBuilder.WithClientId(clientId);

        if (useTls)
        {
            optsBuilder.WithTlsOptions(o => o
                .UseTls(true)
                // Sample broker uses an in-memory self-signed cert. Trust everything for the
                // smoke test — production clients would pin the CA / cert thumbprint.
                .WithCertificateValidationHandler(_ => true)
                .WithIgnoreCertificateRevocationErrors(true)
                .WithIgnoreCertificateChainErrors(true)
                .WithAllowUntrustedCertificates(true));
        }

        await client.ConnectAsync(optsBuilder.Build());
        return client;
    }

    private static async Task Main(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();
        var flags = new HashSet<string>(args.Where(a => a.StartsWith("--")));

        var host = positional.Length > 0 ? positional[0] : "localhost";
        var defaultPort = flags.Contains("--tls") ? 8883 : 1883;
        var port = positional.Length > 1 ? int.Parse(positional[1]) : defaultPort;
        useTls = flags.Contains("--tls");
        var smoke = flags.Contains("--smoke");

        Console.WriteLine($"MQTT Test Client — targeting {(useTls ? "tcps" : "tcp")}://{host}:{port}{(smoke ? " (smoke)" : "")}");
        Console.WriteLine(new string('=', 50));

        var passed = 0;
        var failed = 0;

        if (smoke)
        {
            await RunTest("smoke pub/sub roundtrip", async () =>
            {
                using var sub = await Connect(host, port, "smoke-sub");
                var received = new ConcurrentBag<string>();
                var gotAll = new TaskCompletionSource();
                sub.ApplicationMessageReceivedAsync += e =>
                {
                    var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                    received.Add(payload);
                    if (received.Count >= 5) gotAll.TrySetResult();
                    return Task.CompletedTask;
                };
                await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("smoke/+").Build());

                using var pub = await Connect(host, port, "smoke-pub");
                for (var i = 0; i < 5; i++)
                {
                    await pub.PublishAsync(new MqttApplicationMessageBuilder()
                        .WithTopic("smoke/test")
                        .WithPayload($"hello-{i}")
                        .Build());
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var _ = cts.Token.Register(() => gotAll.TrySetException(new TimeoutException("timed out waiting for 5 messages")));
                await gotAll.Task;

                if (received.Count < 5)
                {
                    throw new Exception($"expected >=5 messages, got {received.Count}");
                }
            }, () => passed++, () => failed++);

            Console.WriteLine();
            Console.WriteLine($"Result: {passed} passed, {failed} failed");
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        Console.WriteLine("\n Bombard server scale test");

        await RunTest("bombard server test", async () =>
        {
            const int messagesPerDevice = 10_000;
            const int devices = 100;
            const int subSessions = 10;

            List<IMqttClient> clients = new();

            var counters = new int[subSessions][];
            for (var j = 0; j < subSessions; j++)
            {
                int subId = j;
                counters[subId] = new int[devices];
                IMqttClient sub = await Connect(host, port, $"test-{j}-sub");
                sub.ApplicationMessageReceivedAsync += e =>
                {
                    // from the message parse out the id
                    string msg = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                    if (msg == null)
                    {
                        return Task.CompletedTask;
                    }
                    int deviceId = int.Parse(msg!.Split("-")[1]);
                    Interlocked.Increment(ref counters[subId][deviceId]);
                    return Task.CompletedTask;
                };

                await sub.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("device/+").Build());
                clients.Add(sub);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));

            Stopwatch sw = Stopwatch.StartNew();
            List<Task> pubTasks = new();
            for (var i = 0; i < devices; i++)
            {
                var deviceId = i;
                pubTasks.Add(Task.Run(async () =>
                {
                    using IMqttClient pub = await Connect(host, port, $"test-{deviceId}-pub");
                    string topic = $"device/{deviceId}";
                    for (var j = 0; j < messagesPerDevice; j++)
                    {
                        await pub.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic(topic)
                            .WithPayload($"msg-{deviceId}")
                            .Build());

                        await Task.Delay(50);
                    }
                }));
            }

            await Task.WhenAll(pubTasks);

            Console.WriteLine($"Cooling down teardown after publish finished in {TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds).TotalSeconds}");

            await Task.Delay(TimeSpan.FromSeconds(10));

            foreach (var client in clients)
            {
                client.Dispose();
            }

            for (var i = 0; i < counters.Length; i++)
            {
                var ctrs = counters[i];
                Console.WriteLine($"For Device {i}");
                Console.WriteLine(string.Join("-", ctrs));
            }
        }, () => passed++, () => failed++);
    }
}
