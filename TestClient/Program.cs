using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

// Broker-agnostic MQTT v5 test client.
// Tests: fan-out pub/sub, topic lifecycle, concurrent producers/consumers.
// Usage: dotnet run [host] [port]
//   defaults: localhost 1883

internal class Program
{

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
            // Interlocked.Increment(ref passed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"FAIL ({sw.ElapsedMilliseconds}ms)");
            Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
            onFailed();
            // Interlocked.Increment(ref failed);
        }
    }

    static MqttFactory factory = new MqttFactory();

    static async Task<IMqttClient> Connect(string host, int port, string? clientId = null)
    {
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

    private static async Task Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "localhost";
        var port = args.Length > 1 ? int.Parse(args[1]) : 1883;

        Console.WriteLine($"MQTT Test Client — targeting {host}:{port}");
        Console.WriteLine(new string('=', 50));

        var passed = 0;
        var failed = 0;

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