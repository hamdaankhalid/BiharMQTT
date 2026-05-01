// Simple BiharMQTT broker — no console input needed.
// Defaults to plain TCP on port 1883.
// Set BIHARMQTT_TLS=1 to also expose the encrypted endpoint on 8883 using a
// self-signed cert generated in-memory (see Server_TLS_Samples).

using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Samples.Server;
using BiharMQTT.Server;

// Static interceptor: an Allow no-op exercises the function-pointer call site
// without changing behaviour. Swap in topic-prefix checks here for $SYS-style
// publisher-to-server-only routes or auth-gated publishes.
static PublishInterceptResult AllowAllInterceptor(in MqttPublishInterceptArgs args)
    => PublishInterceptResult.Allow;

var tlsEnabled = Environment.GetEnvironmentVariable("BIHARMQTT_TLS") == "1";

var logger = new MqttNetEventLogger();

var mqttServerFactory = new MqttServerFactory();
var optionsBuilder = new MqttServerOptionsBuilder()
    .WithDefaultEndpoint()
    .WithDefaultEndpointPort(1883);

if (tlsEnabled)
{
    var certificate = Server_TLS_Samples.CreateSelfSignedCertificate("1.3.6.1.5.5.7.3.1");
    optionsBuilder = optionsBuilder
        .WithEncryptedEndpoint()
        .WithEncryptionCertificate(certificate);
}

var mqttServerOptions = optionsBuilder.Build();

unsafe { mqttServerOptions.PublishInterceptor = &AllowAllInterceptor; }

using var mqttServer = mqttServerFactory.CreateMqttServer(mqttServerOptions, logger);
mqttServer.Start();

Console.WriteLine("BiharMQTT broker running on tcp://localhost:1883");
if (tlsEnabled)
{
    Console.WriteLine("  + TLS endpoint on tcp://localhost:8883 (self-signed; client must accept untrusted certs)");
}
Console.WriteLine("Press Ctrl+C to stop.");

// Block until process is killed
var tcs = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.SetResult(); };
await tcs.Task;

await mqttServer.StopAsync();
