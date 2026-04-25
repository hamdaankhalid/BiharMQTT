// Simple BiharMQTT broker — no console input needed.
// Starts a plain TCP MQTT server on port 1883.

using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Server;

var logger = new MqttNetEventLogger();
logger.LogMessagePublished += (_, e) =>
{
    Console.WriteLine($"[{e.LogMessage.Level}] {e.LogMessage.Source}: {e.LogMessage.Message}");
};

var mqttServerFactory = new MqttServerFactory();
var mqttServerOptions = new MqttServerOptionsBuilder()
    .WithDefaultEndpoint()       // plain TCP on port 1883
    .WithDefaultEndpointPort(1883)
    .Build();

using var mqttServer = mqttServerFactory.CreateMqttServer(mqttServerOptions, logger);
await mqttServer.StartAsync();

Console.WriteLine("BiharMQTT broker running on tcp://localhost:1883");
Console.WriteLine("Press Ctrl+C to stop.");

// Block until process is killed
var tcs = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.SetResult(); };
await tcs.Task;

await mqttServer.StopAsync();