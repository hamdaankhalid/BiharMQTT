// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Exceptions;

namespace BiharMQTT;

/// <summary>
/// MQTT over WebSocket (RFC 6455, subprotocol "mqtt"). The payload of binary
/// frames is exposed to the adapter as a plain byte stream — MQTT does not care
/// about frame boundaries (a packet may span frames, a frame may carry several
/// packets), so the adapter's receive state machine works unchanged.
/// </summary>
public sealed class MqttWebSocketChannel : IMqttChannel
{
    WebSocket _webSocket;
    Socket _socket;

    readonly MqttNetSourceLogger _logger;

    /// <summary>
    /// The channel takes ownership of the WebSocket (and therefore the stream it
    /// wraps), the underlying socket and the peer certificate.
    /// </summary>
    public MqttWebSocketChannel(
        WebSocket webSocket,
        Socket socket,
        EndPoint localEndPoint,
        EndPoint remoteEndPoint,
        X509Certificate2 clientCertificate,
        bool isSecureConnection,
        IMqttNetLogger logger = null)
    {
        _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _socket = socket;

        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        ClientCertificate = clientCertificate;
        IsSecureConnection = isSecureConnection;

        _logger = (logger ?? MqttNetNullLogger.Instance).WithSource(nameof(MqttWebSocketChannel));
    }

    public X509Certificate2 ClientCertificate { get; }

    public bool IsSecureConnection { get; }

    public EndPoint LocalEndPoint { get; }

    public EndPoint RemoteEndPoint { get; }

    public void Dispose()
    {
        _logger.Debug("WebSocketChannel.Dispose (Remote={0})", RemoteEndPoint);

        var webSocket = _webSocket;
        _webSocket = null;
        if (webSocket != null)
        {
            // Abort (not CloseAsync): Dispose must not block on the peer. Abort also
            // disposes the wrapped stream (SslStream / NetworkStream).
            try { webSocket.Abort(); } catch (ObjectDisposedException) { }
            try { webSocket.Dispose(); } catch (ObjectDisposedException) { }
        }

        var socket = _socket;
        _socket = null;
        if (socket != null)
        {
            try
            {
                socket.Close();
                socket.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try { ClientCertificate?.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Begin a single receive. The callback is invoked exactly once. Caller must not
    /// issue another BeginReceive until the callback fires.
    /// </summary>
    public void BeginReceive(Memory<byte> buffer, ReceiveCompletionHandler callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var webSocket = _webSocket;
        if (webSocket == null)
        {
            _logger.Debug("BeginReceive on closed channel (Remote={0})", RemoteEndPoint);
            callback(0, new MqttCommunicationException("The WebSocket connection is closed."));
            return;
        }

        ValueTask<ValueWebSocketReceiveResult> vt;
        try
        {
            vt = webSocket.ReceiveAsync(buffer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Debug("WS ReceiveAsync threw (Remote={0}, Ex={1})", RemoteEndPoint, ex.GetType().Name);
            callback(0, ex);
            return;
        }

        if (vt.IsCompleted)
        {
            ValueWebSocketReceiveResult result;
            try { result = vt.Result; }
            catch (Exception ex)
            {
                _logger.Debug("WS sync-completed read faulted (Remote={0}, Ex={1})", RemoteEndPoint, ex.GetType().Name);
                callback(0, IsPeerGone(ex) ? null : ex);
                return;
            }

            CompleteReceive(webSocket, result, buffer, callback);
            return;
        }

        vt.AsTask().ContinueWith(t =>
        {
            var cb = callback;
            if (t.IsFaulted)
            {
                _logger.Debug("WS async read faulted (Remote={0}, Ex={1})", RemoteEndPoint, t.Exception?.InnerException?.GetType().Name);
                var ex = t.Exception?.InnerException ?? t.Exception;
                cb(0, IsPeerGone(ex) ? null : ex);
            }
            else if (t.IsCanceled)
            {
                _logger.Debug("WS async read canceled (Remote={0})", RemoteEndPoint);
                cb(0, new OperationCanceledException());
            }
            else
            {
                CompleteReceive(webSocket, t.Result, buffer, cb);
            }
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    void CompleteReceive(WebSocket webSocket, ValueWebSocketReceiveResult result, Memory<byte> buffer, ReceiveCompletionHandler callback)
    {
        switch (result.MessageType)
        {
            case WebSocketMessageType.Binary:
                // A zero-length binary frame is legal but a 0 byte count means
                // "peer half-closed" to the adapter. Skip it and read again.
                if (result.Count == 0)
                {
                    BeginReceive(buffer, callback);
                    return;
                }

                callback(result.Count, null);
                return;

            case WebSocketMessageType.Close:
                _logger.Debug("WS recv close frame (Remote={0}, Status={1})", RemoteEndPoint, webSocket.CloseStatus);

                // Best-effort close handshake reply; never block the receive path on it.
                try
                {
                    _ = webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch
                {
                    // Ignore — the adapter is about to tear the channel down anyway.
                }

                callback(0, null);
                return;

            default:
                _logger.Debug("WS recv text frame (Remote={0})", RemoteEndPoint);
                callback(0, new MqttProtocolViolationException("MQTT over WebSocket requires binary frames."));
                return;
        }
    }

    // A peer that drops the TCP connection without a close frame is the WebSocket
    // equivalent of a TCP RST/FIN; report it as a half-close (0 bytes, no error)
    // like MqttTcpChannel does, instead of surfacing an error-level exception.
    static bool IsPeerGone(Exception ex) =>
        ex is WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely };

    /// <summary>
    /// WebSocket framing sits on top of the socket, so a writable socket says nothing
    /// about whether SendAsync will complete inline. Always route through the sender pool.
    /// </summary>
    public bool IsWritable() => false;

    /// <summary>
    /// Sends the buffer as one binary message. Blocks until sent; callers (the sender
    /// pool and the adapter's cold path) already serialize writes under the adapter's
    /// lock, which also satisfies WebSocket's single-outstanding-send rule.
    /// </summary>
    public void Write(ReadOnlyMemory<byte> buffer)
    {
        var webSocket = _webSocket;
        if (webSocket == null)
        {
            _logger.Debug("Write on closed channel (Remote={0}, Bytes={1})", RemoteEndPoint, buffer.Length);
            throw new MqttCommunicationException("The WebSocket connection is closed.");
        }

        try
        {
            var vt = webSocket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            if (vt.IsCompletedSuccessfully)
            {
                return;
            }

            vt.AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Debug("WS Write threw (Remote={0}, Bytes={1}, Ex={2})", RemoteEndPoint, buffer.Length, ex.GetType().Name);
            throw;
        }
    }
}
