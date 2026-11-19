using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using BiharMQTT.Server;

namespace BiharMQTT.Tests.Server;

// ReSharper disable InconsistentNaming
[TestClass]
public sealed class MqttWebSocket_Tests
{
    static string Request(string path = "/mqtt", string protocol = "mqtt", string method = "GET") =>
        $"{method} {path} HTTP/1.1\r\n" +
        "Host: localhost\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: keep-alive, Upgrade\r\n" +
        "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
        "Sec-WebSocket-Version: 13\r\n" +
        (protocol != null ? $"Sec-WebSocket-Protocol: {protocol}\r\n" : string.Empty);

    [TestMethod]
    public void Handshake_AcceptKey_Matches_Rfc6455_Example()
    {
        Assert.AreEqual("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", MqttWebSocketHandshake.ComputeAcceptKey("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    [TestMethod]
    public void Handshake_Valid_Request_Succeeds()
    {
        var result = MqttWebSocketHandshake.Validate(Request(path: "/mqtt?x=1", protocol: "mqttv3.1, mqtt"), "/mqtt", out var status, out var accept);
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(101, status);
        Assert.AreEqual("mqtt", result.SubProtocol);
        Assert.AreEqual("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", accept);
    }

    [TestMethod]
    public void Handshake_Rejects_Missing_Subprotocol()
    {
        Assert.IsFalse(MqttWebSocketHandshake.Validate(Request(protocol: null), "/mqtt", out var status, out _).Success);
        Assert.AreEqual(400, status);
        Assert.IsFalse(MqttWebSocketHandshake.Validate(Request(protocol: "chat"), "/mqtt", out _, out _).Success);
    }

    [TestMethod]
    public void Handshake_Rejects_Wrong_Path()
    {
        Assert.IsFalse(MqttWebSocketHandshake.Validate(Request(path: "/other"), "/mqtt", out var status, out _).Success);
        Assert.AreEqual(404, status);
    }

    [TestMethod]
    public void Handshake_Rejects_Non_Get()
    {
        Assert.IsFalse(MqttWebSocketHandshake.Validate(Request(method: "POST"), "/mqtt", out _, out _).Success);
    }

    // ---- End-to-end over a real broker -------------------------------------------------

    // MQTT 5 CONNECT, clean start, keep-alive 60s, no properties.
    static byte[] Connect(string clientId)
    {
        var id = Encoding.UTF8.GetBytes(clientId);
        var body = new List<byte> { 0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 0x05, 0x02, 0x00, 0x3C, 0x00 };
        body.Add((byte)(id.Length >> 8));
        body.Add((byte)id.Length);
        body.AddRange(id);
        return [0x10, (byte)body.Count, .. body];
    }

    static byte[] Subscribe(string topic)
    {
        var t = Encoding.UTF8.GetBytes(topic);
        var body = new List<byte> { 0x00, 0x01, 0x00, (byte)(t.Length >> 8), (byte)t.Length };
        body.AddRange(t);
        body.Add(0x00); // QoS 0
        return [0x82, (byte)body.Count, .. body];
    }

    static byte[] Publish(string topic, string payload)
    {
        var t = Encoding.UTF8.GetBytes(topic);
        var p = Encoding.UTF8.GetBytes(payload);
        var body = new List<byte> { (byte)(t.Length >> 8), (byte)t.Length };
        body.AddRange(t);
        body.Add(0x00); // no properties
        body.AddRange(p);
        return [0x30, (byte)body.Count, .. body];
    }

    // Reads one MQTT packet (fixed header + body) using a byte-reader.
    static async Task<(byte Type, byte[] Body)> ReadPacketAsync(Func<int, Task<byte[]>> readExactly)
    {
        var type = (await readExactly(1))[0];
        int length = 0, shift = 0;
        byte b;
        do
        {
            b = (await readExactly(1))[0];
            length |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        return (type, length == 0 ? [] : await readExactly(length));
    }

    static Task<(byte Type, byte[] Body)> ReadPacketAsync(ClientWebSocket ws) => ReadPacketAsync(n => ReceiveAsync(ws, n));

    static Task<(byte Type, byte[] Body)> ReadPacketAsync(Stream stream) => ReadPacketAsync(n => Task.FromResult(ReadExactly(stream, n)));

    static void AssertConnAckSuccess((byte Type, byte[] Body) packet)
    {
        Assert.AreEqual((byte)0x20, packet.Type);
        Assert.AreEqual((byte)0x00, packet.Body[1], "CONNACK reason code");
    }

    static void AssertSubAckGranted((byte Type, byte[] Body) packet)
    {
        Assert.AreEqual((byte)0x90, packet.Type);
        Assert.AreEqual((byte)0x00, packet.Body[^1], "SUBACK reason code");
    }

    static void AssertPublish((byte Type, byte[] Body) packet, string expectedPayload)
    {
        Assert.AreEqual((byte)0x30, packet.Type & 0xF0);
        StringAssert.EndsWith(Encoding.UTF8.GetString(packet.Body), expectedPayload);
    }

    static async Task<MqttServer> StartServerAsync(bool tcpToo = false)
    {
        var options = new MqttServerOptionsBuilder().WithWebSocketEndpoint(port: 0).Build();
        options.WebSocketEndpointOptions.BoundInterNetworkV6Address = IPAddress.None;
        if (tcpToo)
        {
            options.DefaultEndpointOptions.IsEnabled = true;
            options.DefaultEndpointOptions.Port = 0;
            options.DefaultEndpointOptions.BoundInterNetworkV6Address = IPAddress.None;
        }

        var logger = new BiharMQTT.Diagnostics.Logger.MqttNetEventLogger();
        logger.LogMessagePublished += (_, e) => Console.WriteLine($"[{e.LogMessage.Level}] {e.LogMessage.Source}: {e.LogMessage.Message} {e.LogMessage.Exception}");
        var server = new MqttServerFactory().CreateMqttServer(options, logger);
        server.Start();
        await Task.Delay(50);
        return server;
    }

    static async Task<ClientWebSocket> ConnectWsAsync(MqttServerOptions options, string clientId)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("mqtt");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{options.WebSocketEndpointOptions.Port}/mqtt"), cts.Token);
        Assert.AreEqual("mqtt", ws.SubProtocol);

        await SendAsync(ws, Connect(clientId));
        AssertConnAckSuccess(await ReadPacketAsync(ws));
        return ws;
    }

    static Task SendAsync(ClientWebSocket ws, byte[] data) =>
        ws.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);

    // Reads exactly `count` bytes, regardless of how the server split them into frames.
    static async Task<byte[]> ReceiveAsync(ClientWebSocket ws, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (read < count)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(read), cts.Token);
            Assert.AreEqual(WebSocketMessageType.Binary, result.MessageType);
            read += result.Count;
        }

        return buffer;
    }

    [TestMethod]
    public async Task WebSocket_Connect_Subscribe_Publish_Roundtrip()
    {
        var server = await StartServerAsync();
        try
        {
            var options = GetOptions(server);
            using var sub = await ConnectWsAsync(options, "sub");
            using var pub = await ConnectWsAsync(options, "pub");

            await SendAsync(sub, Subscribe("t/1"));
            AssertSubAckGranted(await ReadPacketAsync(sub));

            // One MQTT packet split across two WS frames, followed by two packets in one frame.
            var publish = Publish("t/1", "hello");
            await pub.SendAsync(publish.AsMemory(0, 3), WebSocketMessageType.Binary, false, CancellationToken.None);
            await pub.SendAsync(publish.AsMemory(3), WebSocketMessageType.Binary, true, CancellationToken.None);
            await SendAsync(pub, [.. Publish("t/1", "a"), .. Publish("t/1", "b")]);

            AssertPublish(await ReadPacketAsync(sub), "hello");
            AssertPublish(await ReadPacketAsync(sub), "a");
            AssertPublish(await ReadPacketAsync(sub), "b");

            await sub.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [TestMethod]
    public async Task WebSocket_Publish_Reaches_Tcp_Subscriber()
    {
        var server = await StartServerAsync(tcpToo: true);
        try
        {
            var options = GetOptions(server);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, options.DefaultEndpointOptions.Port);
            var tcpStream = tcp.GetStream();
            tcpStream.ReadTimeout = 5000;

            await tcpStream.WriteAsync(Connect("tcp-sub"));
            AssertConnAckSuccess(await ReadPacketAsync(tcpStream));
            await tcpStream.WriteAsync(Subscribe("x"));
            AssertSubAckGranted(await ReadPacketAsync(tcpStream));

            using var pub = await ConnectWsAsync(options, "ws-pub");
            var publish = Publish("x", "from-ws");
            await SendAsync(pub, publish);

            AssertPublish(await ReadPacketAsync(tcpStream), "from-ws");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [TestMethod]
    public async Task WebSocket_Wrong_Path_Is_Rejected()
    {
        var server = await StartServerAsync();
        try
        {
            using var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("mqtt");
            await Assert.ThrowsExactlyAsync<WebSocketException>(() =>
                ws.ConnectAsync(new Uri($"ws://127.0.0.1:{GetOptions(server).WebSocketEndpointOptions.Port}/nope"), CancellationToken.None));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    static MqttServerOptions GetOptions(MqttServer server) =>
        (MqttServerOptions)typeof(MqttServer)
            .GetField("_options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(server);
}
