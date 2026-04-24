// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using BiharMQTT.Exceptions;

namespace BiharMQTT.Implementations;

public sealed class MqttTcpChannel
{
    Socket _socket;
    Stream _stream;

    /// <summary>
    /// Server-side constructor. The channel takes ownership of both socket and stream.
    /// For non-TLS: pass sslStream = null. For TLS: pass the authenticated SslStream.
    /// </summary>
    public MqttTcpChannel(Socket socket, SslStream sslStream, EndPoint localEndPoint, EndPoint remoteEndPoint, X509Certificate2 clientCertificate)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _stream = sslStream;

        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;

        IsSecureConnection = sslStream != null;
        ClientCertificate = clientCertificate;
    }

    public X509Certificate2 ClientCertificate { get; }

    public bool IsSecureConnection { get; }

    public EndPoint LocalEndPoint { get; }

    public EndPoint RemoteEndPoint { get; }

    public void Dispose()
    {
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
    }

    /// <summary>
    /// Zero-alloc hot-path read. For non-TLS, calls Socket.ReceiveAsync directly
    /// (internally uses cached SAEA, returns ValueTask). For TLS, calls
    /// SslStream.ReadAsync(Memory) which also returns ValueTask.
    /// </summary>
    public ValueTask<int> ReadAsync(Memory<byte> buffer)
    {
        var stream = _stream;
        if (stream != null)
        {
            return stream.ReadAsync(buffer);
        }

        var socket = _socket;
        if (socket == null)
        {
            return new ValueTask<int>(0);
        }

        return socket.ReceiveAsync(buffer, SocketFlags.None);
    }

    /// <summary>
    /// Zero-alloc hot-path write. Guarantees all bytes are sent.
    /// </summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer)
    {
        var stream = _stream;
        if (stream != null)
        {
            return stream.WriteAsync(buffer);
        }

        var socket = _socket;
        if (socket == null)
        {
            throw new MqttCommunicationException("The TCP connection is closed.");
        }

        return SendAllAsync(socket, buffer);
    }

    static async ValueTask SendAllAsync(Socket socket, ReadOnlyMemory<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var sent = await socket.SendAsync(buffer, SocketFlags.None).ConfigureAwait(false);
            if (sent == 0)
            {
                throw new MqttCommunicationException("The TCP connection is closed.");
            }

            buffer = buffer.Slice(sent);
        }
    }
}