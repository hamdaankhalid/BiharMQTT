// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Exceptions;

namespace BiharMQTT;

/// <summary>
/// Invoked when a BeginReceive completes. Exactly one of (bytesRead, error) is
/// meaningful: error is non-null on failure, otherwise bytesRead is the count
/// (0 means the peer half-closed).
/// </summary>
public delegate void ReceiveCompletionHandler(int bytesRead, Exception error);

public sealed class MqttTcpChannel : IDisposable
{
    Socket _socket;
    SslStream _stream;

    // Persistent SAEA for the non-TLS receive path. One per channel, allocated
    // once, re-armed for every read. The Completed event is hooked once in the
    // constructor; we don't re-subscribe per operation.
    readonly SocketAsyncEventArgs _recvSaea;

    readonly MqttNetSourceLogger _logger;

    // The current receive completion callback. Cleared before invoking so a
    // synchronous re-arm from the callback can install a new handler without
    // racing with the previous SAEA completion.
    ReceiveCompletionHandler _recvCallback;

    /// <summary>
    /// Server-side constructor. The channel takes ownership of both socket and stream.
    /// For non-TLS: pass sslStream = null. For TLS: pass the authenticated SslStream.
    /// The logger is only consulted on hot paths when <see cref="BiharMQTT.Diagnostics.BiharMqttDiagnostics.DebugEnabled"/>
    /// is true, so it's safe to pass an always-on adapter.
    /// </summary>
    public MqttTcpChannel(
        Socket socket,
        SslStream sslStream,
        EndPoint localEndPoint,
        EndPoint remoteEndPoint,
        X509Certificate2 clientCertificate,
        IMqttNetLogger logger = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _stream = sslStream;

        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;

        IsSecureConnection = sslStream != null;
        ClientCertificate = clientCertificate;

        _recvSaea = new SocketAsyncEventArgs();
        _recvSaea.Completed += OnRecvCompleted;

        _logger = (logger ?? MqttNetNullLogger.Instance).WithSource(nameof(MqttTcpChannel));
    }

    public X509Certificate2 ClientCertificate { get; }

    public bool IsSecureConnection { get; }

    public EndPoint LocalEndPoint { get; }

    public EndPoint RemoteEndPoint { get; }

    public void Dispose()
    {
        _logger.Debug("Channel.Dispose (Remote={0})", RemoteEndPoint);

        try
        {
            _stream?.Close();
            _stream?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (NullReferenceException)
        {
        }
        finally
        {
            _stream = null;
        }

        try
        {
            _socket?.Close();
            _socket?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _socket = null;
        }

        // Disposing the SAEA is safe even if a receive was pending — closing
        // the socket above triggers the Completed event with OperationAborted,
        // and the user's callback observes the error.
        try { _recvSaea.Dispose(); } catch (ObjectDisposedException) { }

        // Channel owns the peer cert (cloned from SslStream.RemoteCertificate at handshake).
        // Releasing it here is what makes the per-connection X509Certificate2 leak go away.
        try { ClientCertificate?.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Begin a single receive. The callback is invoked exactly once — either
    /// inline (synchronous completion) or from the SocketAsyncEventArgs.Completed
    /// handler (async completion). Caller must not issue another BeginReceive
    /// until the callback fires.
    /// </summary>
    public void BeginReceive(Memory<byte> buffer, ReceiveCompletionHandler callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var stream = _stream;
        if (stream != null)
        {
            BeginReceiveTls(stream, buffer, callback);
            return;
        }

        var socket = _socket;
        if (socket == null)
        {
            _logger.Debug("BeginReceive on closed channel (Remote={0})", RemoteEndPoint);
            callback(0, new MqttCommunicationException("The TCP connection is closed."));
            return;
        }

        _recvCallback = callback;
        _recvSaea.SetBuffer(buffer);

        bool pending;
        try
        {
            pending = socket.ReceiveAsync(_recvSaea);
        }
        catch (Exception ex)
        {
            _logger.Debug("ReceiveAsync threw (Remote={0}, Ex={1})", RemoteEndPoint, ex.GetType().Name);
            _recvCallback = null;
            callback(0, ex);
            return;
        }

        if (!pending)
        {
            // Synchronous completion. Invoke directly; the depth guard at the
            // adapter level breaks any unbounded recursion onto the thread pool.
            CompleteRecv(_recvSaea);
        }
    }

    void OnRecvCompleted(object sender, SocketAsyncEventArgs e) => CompleteRecv(e);

    void CompleteRecv(SocketAsyncEventArgs e)
    {
        var cb = _recvCallback;
        _recvCallback = null;
        if (cb == null)
        {
            // Anomaly: SAEA fired but no callback was registered. Either the channel
            // was disposed mid-flight or someone re-entered BeginReceive without
            // waiting for the previous completion.
            _logger.Debug("CompleteRecv with no callback (Remote={0}, SocketError={1}, Bytes={2})",
                RemoteEndPoint, e.SocketError, e.BytesTransferred);
            return;
        }

        if (e.SocketError != SocketError.Success)
        {
            _logger.Debug("Recv socket error (Remote={0}, SocketError={1})", RemoteEndPoint, e.SocketError);
            cb(0, new SocketException((int)e.SocketError));
            return;
        }

        if (e.BytesTransferred == 0)
        {
            // Peer half-close. Not strictly an anomaly but worth tracing — every
            // healthy disconnect funnels through this path exactly once.
            _logger.Debug("Recv peer half-close (Remote={0})", RemoteEndPoint);
        }

        cb(e.BytesTransferred, null);
    }

    void BeginReceiveTls(SslStream stream, Memory<byte> buffer, ReceiveCompletionHandler callback)
    {
        ValueTask<int> vt;
        try
        {
            vt = stream.ReadAsync(buffer);
        }
        catch (Exception ex)
        {
            _logger.Debug("TLS ReadAsync threw (Remote={0}, Ex={1})", RemoteEndPoint, ex.GetType().Name);
            callback(0, ex);
            return;
        }

        if (vt.IsCompletedSuccessfully)
        {
            // Bind Result to a local — ValueTask must be consumed exactly once.
            var bytes = vt.Result;
            if (bytes == 0) _logger.Debug("TLS recv peer half-close sync (Remote={0})", RemoteEndPoint);
            callback(bytes, null);
            return;
        }

        if (vt.IsCompleted)
        {
            int bytes;
            try { bytes = vt.Result; }
            catch (Exception ex)
            {
                _logger.Debug("TLS sync-completed read faulted on result (Remote={0}, Ex={1})", RemoteEndPoint, ex.GetType().Name);
                callback(0, ex);
                return;
            }
            callback(bytes, null);
            return;
        }

        // Async path on TLS — bridge ValueTask completion to the callback.
        // SslStream's ValueTask is not always cached, so this can allocate;
        // the non-TLS hot path avoids it entirely via SAEA. Captures `this` so
        // anomalies can be logged through the channel logger.
        vt.AsTask().ContinueWith((t, state) =>
        {
            var cb = (ReceiveCompletionHandler)state;
            if (t.IsFaulted)
            {
                _logger.Debug("TLS async read faulted (Remote={0}, Ex={1})", RemoteEndPoint, t.Exception?.InnerException?.GetType().Name);
                cb(0, t.Exception?.InnerException ?? t.Exception);
            }
            else if (t.IsCanceled)
            {
                _logger.Debug("TLS async read canceled (Remote={0})", RemoteEndPoint);
                cb(0, new OperationCanceledException());
            }
            else
            {
                if (t.Result == 0) _logger.Debug("TLS recv peer half-close async (Remote={0})", RemoteEndPoint);
                cb(t.Result, null);
            }
        }, callback, TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// Cheap probe: is the kernel send buffer accepting more bytes right now?
    /// Used by the inline-send fast path to skip the queue when the socket is
    /// uncongested. Always returns false on TLS (we cannot inspect SslStream's
    /// internal state) and on disposed channels.
    /// </summary>
    public bool IsWritable()
    {
        // SslStream has its own buffering layered on top of the socket; a writable
        // underlying socket does not imply the SslStream will send without blocking.
        // Skip the inline path on TLS rather than guess.
        if (_stream != null) return false;

        var socket = _socket;
        if (socket == null) return false;

        try
        {
            return socket.Poll(0, SelectMode.SelectWrite);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Zero-alloc hot-path write. Guarantees all bytes are sent.
    /// </summary>
    public void Write(ReadOnlyMemory<byte> buffer)
    {
        var stream = _stream;
        if (stream != null)
        {
            try
            {
                stream.Write(buffer.Span);
            }
            catch (Exception ex)
            {
                _logger.Debug("TLS Write threw (Remote={0}, Bytes={1}, Ex={2})", RemoteEndPoint, buffer.Length, ex.GetType().Name);
                throw;
            }
            return;
        }

        var socket = _socket;
        if (socket == null)
        {
            _logger.Debug("Write on closed channel (Remote={0}, Bytes={1})", RemoteEndPoint, buffer.Length);
            throw new MqttCommunicationException("The TCP connection is closed.");
        }

        Send(socket, buffer);
    }

    void Send(Socket socket, ReadOnlyMemory<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int sent;
            try
            {
                sent = socket.Send(buffer.Span, SocketFlags.None);
            }
            catch (Exception ex)
            {
                _logger.Debug("Socket.Send threw (Remote={0}, Remaining={1}, Ex={2})", RemoteEndPoint, buffer.Length, ex.GetType().Name);
                throw;
            }

            if (sent == 0)
            {
                _logger.Debug("Socket.Send returned 0 (Remote={0}, Remaining={1})", RemoteEndPoint, buffer.Length);
                throw new MqttCommunicationException("The TCP connection is closed.");
            }

            buffer = buffer.Slice(sent);
        }
    }
}
