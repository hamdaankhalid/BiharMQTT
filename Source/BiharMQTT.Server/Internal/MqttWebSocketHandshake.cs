// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace BiharMQTT.Server;

/// <summary>
/// Minimal server side of the RFC 6455 opening handshake (HTTP/1.1 Upgrade).
/// Only what MQTT-over-WebSocket clients need: GET + path, Upgrade/Connection,
/// version 13, Sec-WebSocket-Key, and the "mqtt" subprotocol.
/// </summary>
public static class MqttWebSocketHandshake
{
    const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    const int MaxRequestBytes = 8 * 1024;

    public readonly struct Result
    {
        public Result(bool success, string error, string subProtocol)
        {
            Success = success;
            Error = error;
            SubProtocol = subProtocol;
        }

        public bool Success { get; }
        public string Error { get; }
        public string SubProtocol { get; }
    }

    public static string ComputeAcceptKey(string secWebSocketKey)
    {
        // SHA-1 is mandated by RFC 6455 §4.2.2 for the accept key; it is not used for security here.
#pragma warning disable CA5350
        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(secWebSocketKey + WebSocketGuid));
#pragma warning restore CA5350
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Reads the Upgrade request from <paramref name="stream"/>, validates it, and writes
    /// either a 101 (success) or an error response. On success the stream is positioned
    /// exactly at the first WebSocket frame.
    /// </summary>
    public static async Task<Result> AcceptAsync(Stream stream, string expectedPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var request = await ReadRequestHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (request == null)
        {
            await WriteErrorAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return new Result(false, "Malformed or oversized HTTP request.", null);
        }

        var result = Validate(request, expectedPath, out var statusCode, out var acceptKey);
        if (!result.Success)
        {
            await WriteErrorAsync(stream, statusCode, statusCode == 404 ? "Not Found" : "Bad Request", cancellationToken).ConfigureAwait(false);
            return result;
        }

        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: " + acceptKey + "\r\n" +
            "Sec-WebSocket-Protocol: " + result.SubProtocol + "\r\n" +
            "\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Validates a raw request head (request line + headers, CRLF separated).
    /// Exposed for testing.
    /// </summary>
    public static Result Validate(string requestHead, string expectedPath, out int statusCode, out string acceptKey)
    {
        statusCode = 400;
        acceptKey = null;

        var lines = requestHead.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[0] != "GET" || !requestLine[2].StartsWith("HTTP/1.1", StringComparison.Ordinal))
        {
            return new Result(false, "Expected 'GET <path> HTTP/1.1'.", null);
        }

        var path = requestLine[1];
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path.Substring(0, queryIndex);
        }

        if (!string.Equals(path, expectedPath, StringComparison.Ordinal))
        {
            statusCode = 404;
            return new Result(false, $"Unexpected path '{path}'.", null);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return new Result(false, "Malformed header line.", null);
            }

            var name = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();

            // Repeated headers are combined per RFC 7230 §3.2.2.
            headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
        }

        if (!headers.TryGetValue("Upgrade", out var upgrade) || !ContainsToken(upgrade, "websocket"))
        {
            return new Result(false, "Missing 'Upgrade: websocket'.", null);
        }

        if (!headers.TryGetValue("Connection", out var connection) || !ContainsToken(connection, "upgrade"))
        {
            return new Result(false, "Missing 'Connection: Upgrade'.", null);
        }

        if (!headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13")
        {
            return new Result(false, "Unsupported Sec-WebSocket-Version.", null);
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || key.Length == 0)
        {
            return new Result(false, "Missing Sec-WebSocket-Key.", null);
        }

        string subProtocol = null;
        if (headers.TryGetValue("Sec-WebSocket-Protocol", out var protocols))
        {
            if (ContainsToken(protocols, "mqtt"))
            {
                subProtocol = "mqtt";
            }
            else if (ContainsToken(protocols, "mqttv3.1"))
            {
                subProtocol = "mqttv3.1";
            }
        }

        if (subProtocol == null)
        {
            return new Result(false, "Client did not offer the 'mqtt' subprotocol.", null);
        }

        acceptKey = ComputeAcceptKey(key);
        statusCode = 101;
        return new Result(true, null, subProtocol);
    }

    static bool ContainsToken(string headerValue, string token)
    {
        foreach (var part in headerValue.Split(','))
        {
            if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Reads byte-by-byte up to and including "\r\n\r\n" so no WebSocket frame bytes
    // are consumed. Cold path (once per connection), bounded by MaxRequestBytes.
    static async Task<string> ReadRequestHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length++;

            if (length >= 4 &&
                buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
                buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length - 4);
            }
        }

        return null;
    }

    static async Task WriteErrorAsync(Stream stream, int statusCode, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var response = $"HTTP/1.1 {statusCode} {reason}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best effort; the connection is being rejected anyway.
        }
    }
}
