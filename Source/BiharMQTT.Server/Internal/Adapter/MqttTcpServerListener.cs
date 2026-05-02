// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using BiharMQTT.Adapter;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Formatter;
using BiharMQTT.Implementations;
using BiharMQTT.Internal;

namespace BiharMQTT.Server.Internal.Adapter;

public sealed class MqttTcpServerListener : IDisposable
{
    readonly MqttNetSourceLogger _logger;
    readonly IMqttNetLogger _rootLogger;
    readonly AddressFamily _addressFamily;
    readonly MqttServerOptions _serverOptions;
    readonly MqttServerTcpEndpointBaseOptions _options;
    readonly MqttServerTlsTcpEndpointOptions _tlsOptions;
    readonly Func<MqttChannelAdapter, Task> _clientHandler;

    // Accept loop socket and event args
    SocketAsyncEventArgs _acceptAsyncEventArgs;
    Socket _socket;
    IPEndPoint _localEndPoint;
    CancellationToken _listenerCancellationToken;

    public MqttTcpServerListener(
        AddressFamily addressFamily,
        MqttServerOptions serverOptions,
        MqttServerTcpEndpointBaseOptions tcpEndpointOptions,
        Func<MqttChannelAdapter, Task> clientHandler,
        IMqttNetLogger logger)
    {
        _addressFamily = addressFamily;
        _serverOptions = serverOptions ?? throw new ArgumentNullException(nameof(serverOptions));
        _options = tcpEndpointOptions ?? throw new ArgumentNullException(nameof(tcpEndpointOptions));
        _clientHandler = clientHandler ?? throw new ArgumentNullException(nameof(clientHandler));
        _rootLogger = logger;
        _logger = logger.WithSource(nameof(MqttTcpServerListener));

        if (_options is MqttServerTlsTcpEndpointOptions tlsOptions)
        {
            _tlsOptions = tlsOptions;
        }
    }

    public bool Start(bool treatErrorsAsWarning, CancellationToken cancellationToken)
    {
        try
        {
            var boundIp = _options.BoundInterNetworkAddress;
            if (_addressFamily == AddressFamily.InterNetworkV6)
            {
                boundIp = _options.BoundInterNetworkV6Address;
            }

            _localEndPoint = new IPEndPoint(boundIp, _options.Port);

            _logger.Info("Starting TCP listener (Endpoint={0}, TLS={1})", _localEndPoint, _tlsOptions?.CertificateProvider != null);

            _socket = new Socket(_addressFamily, SocketType.Stream, ProtocolType.Tcp);

            // Usage of socket options is described here: https://docs.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.setsocketoption?view=netcore-2.2
            if (_options.ReuseAddress)
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            if (_options.NoDelay)
            {
                _socket.NoDelay = true;
            }

            if (_options.LingerState != null)
            {
                _socket.LingerState = _options.LingerState;
            }

            if (_options.KeepAlive.HasValue)
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, _options.KeepAlive.Value);
            }

            if (_options.TcpKeepAliveInterval.HasValue)
            {
                _socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, _options.TcpKeepAliveInterval.Value);
            }

            if (_options.TcpKeepAliveRetryCount.HasValue)
            {
                _socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, _options.TcpKeepAliveRetryCount.Value);
            }

            if (_options.TcpKeepAliveTime.HasValue)
            {
                _socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, _options.TcpKeepAliveTime.Value);
            }

            _socket.Bind(_localEndPoint);

            // Get the local endpoint back from the socket. The port may have changed.
            // This can happen when port 0 is used. Then the OS will choose the next free port.
            _localEndPoint = (IPEndPoint)_socket.LocalEndPoint;
            _options.Port = _localEndPoint.Port;

            _socket.Listen(_options.ConnectionBacklog);

            _logger.Verbose("TCP listener started (Endpoint={0})", _localEndPoint);

            _listenerCancellationToken = cancellationToken;
            _acceptAsyncEventArgs = new SocketAsyncEventArgs();
            _acceptAsyncEventArgs.Completed += OnAcceptCompleted;

            // Kick off the initial accept on a worker so Start does not have to drain
            // any pre-queued backlog inline. Subsequent re-arms run on the IO completion thread.
            Task.Run(StartAccept, cancellationToken).RunInBackground(_logger);

            return true;
        }
        catch (Exception exception)
        {
            if (!treatErrorsAsWarning)
            {
                throw;
            }

            _logger.Warning(exception, "Error while starting TCP listener (Endpoint={0})", _localEndPoint);
            return false;
        }
    }

    public void Dispose()
    {
        _socket?.Dispose();
        _acceptAsyncEventArgs?.Dispose();
    }

    // Posts an accept on the listening socket. AcceptAsync returns false when the operation
    // completed synchronously (process inline and loop) and true when it will fire Completed.
    void StartAccept()
    {
        var socket = _socket;
        if (socket == null)
        {
            return;
        }

        try
        {
            while (!_listenerCancellationToken.IsCancellationRequested)
            {
                _acceptAsyncEventArgs.AcceptSocket = null;

                if (socket.AcceptAsync(_acceptAsyncEventArgs))
                {
                    // Pending — OnAcceptCompleted will resume the loop.
                    _logger.Debug("AcceptAsync pending (Endpoint={0})", _localEndPoint);
                    return;
                }

                _logger.Debug("AcceptAsync completed synchronously (Endpoint={0})", _localEndPoint);
                ProcessAccept(_acceptAsyncEventArgs);
            }
        }
        catch (ObjectDisposedException)
        {
            // Listener socket disposed during shutdown.
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Error while issuing AcceptAsync (Endpoint={0})", _localEndPoint);
        }
    }

    void OnAcceptCompleted(object sender, SocketAsyncEventArgs e)
    {
        _logger.Debug("OnAcceptCompleted fired (SocketError={0}, Endpoint={1})", e.SocketError, _localEndPoint);
        ProcessAccept(e);
        StartAccept();
    }

    void ProcessAccept(SocketAsyncEventArgs e)
    {
        if (e.SocketError != SocketError.Success)
        {
            if (e.SocketError is SocketError.ConnectionAborted or SocketError.OperationAborted)
            {
                return;
            }

            _logger.Error(null, "Error while accepting TCP connection (SocketError={0}, Endpoint={1})", e.SocketError, _localEndPoint);
            return;
        }

        var clientSocket = e.AcceptSocket;
        if (clientSocket == null)
        {
            return;
        }

        // Handling is put off to another task always so the accept loop is never blocked.
        _ = Task.Factory.StartNew(
            () => TryHandleClientConnectionAsync(clientSocket),
            _listenerCancellationToken,
            TaskCreationOptions.PreferFairness,
            TaskScheduler.Default);
    }

    async Task TryHandleClientConnectionAsync(Socket clientSocket)
    {
        Stream stream = null;
        SslStream sslStream = null;
        EndPoint remoteEndPoint = null;
        MqttTcpChannel tcpChannel = null;
        var channelOwnedByAdapter = false;

        try
        {
            remoteEndPoint = clientSocket.RemoteEndPoint;

            _logger.Verbose("TCP client '{0}' accepted (Local endpoint={1})", remoteEndPoint, _localEndPoint);

            clientSocket.NoDelay = _options.NoDelay;

            X509Certificate2 peerCertificate = null;

            if (_tlsOptions != null)
            {
                var serverCertificate = _tlsOptions.CertificateProvider?.GetCertificate();
                if (serverCertificate == null)
                {
                    // Adapter.Start eagerly validates this at boot, but a hot-rotating
                    // provider could still return null mid-flight. Fail this connection
                    // loudly rather than silently fall through to plaintext: a peer dialing
                    // 8883 expects TLS, and treating their ClientHello bytes as MQTT yields
                    // baffling decoder errors instead.
                    throw new InvalidOperationException(
                        "TLS endpoint configured but the certificate provider returned a null certificate.");
                }

                // NetworkStream owns the socket = false, since MqttTcpChannel will own the socket directly.
                stream = new NetworkStream(clientSocket, ownsSocket: false);
                sslStream = new SslStream(stream, leaveInnerStreamOpen: false, _tlsOptions.RemoteCertificateValidationCallback);

                var authOptions = new SslServerAuthenticationOptions
                {
                    ClientCertificateRequired = _tlsOptions.ClientCertificateRequired,
                    EnabledSslProtocols = _tlsOptions.SslProtocol,
                    CertificateRevocationCheckMode = _tlsOptions.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                    CipherSuitesPolicy = _tlsOptions.CipherSuitesPolicy
                };

                // Provider-cached context: built once per (offline) mode for the lifetime of
                // the provider. Pre-fix this was rebuilt per connection with offline:false,
                // which forced per-handshake chain validation and (on reachable networks)
                // synchronous OCSP fetches — the visible "TLS hangs" symptom.
                var ctx = _tlsOptions.CertificateProvider.GetServerCertificateContext(
                    offline: !_tlsOptions.CheckCertificateRevocation);

                if (ctx != null)
                {
                    authOptions.ServerCertificateContext = ctx;
                }
                else
                {
                    authOptions.ServerCertificate = serverCertificate;
                }

                using var timeoutCts = new CancellationTokenSource(_serverOptions.DefaultCommunicationTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _listenerCancellationToken);

                _logger.Debug(
                    "TLS handshake starting (Remote={0}, ClientCertRequired={1}, RevocationCheck={2})",
                    remoteEndPoint,
                    _tlsOptions.ClientCertificateRequired,
                    _tlsOptions.CheckCertificateRevocation);

                await sslStream.AuthenticateAsServerAsync(authOptions, linkedCts.Token).ConfigureAwait(false);

                _logger.Debug(
                    "TLS handshake completed (Remote={0}, Protocol={1}, Cipher={2})",
                    remoteEndPoint,
                    sslStream.SslProtocol,
                    sslStream.NegotiatedCipherSuite);

                stream = sslStream;

                // Always clone via Export+Load so the channel owns its peer certificate.
                // SslStream.RemoteCertificate is owned by SslStream — it gets disposed when
                // SslStream does, so consumers reading ClientCertificate after the connection
                // tears down would hit a use-after-free. Owning a copy lets MqttTcpChannel
                // dispose it deterministically.
                if (sslStream.RemoteCertificate != null)
                {
#if NET10_0_OR_GREATER
                    peerCertificate = X509CertificateLoader.LoadCertificate(sslStream.RemoteCertificate.Export(X509ContentType.Cert));
#else
                    peerCertificate = new X509Certificate2(sslStream.RemoteCertificate.Export(X509ContentType.Cert));
#endif
                }
            }

            // Channel takes ownership of socket, stream, and peer cert.
            tcpChannel = new MqttTcpChannel(clientSocket, sslStream, _localEndPoint, remoteEndPoint, peerCertificate, _rootLogger);
            _logger.Debug("Channel constructed (Remote={0}, TLS={1})", remoteEndPoint, sslStream != null);

            var bufferWriter = new MqttBufferWriter(_serverOptions.WriterBufferSize);
            var packetFormatterAdapter = new MqttPacketFormatterAdapter(bufferWriter);

            // Adapter takes ownership of the channel only once construction succeeds — its
            // Dispose (via the using below) is what releases socket/stream/peer cert. Setting
            // channelOwnedByAdapter after the ctor returns guards against the adapter's
            // constructor throwing and orphaning the already-built channel.
            using var clientAdapter = new MqttChannelAdapter(tcpChannel, packetFormatterAdapter, _rootLogger);
            channelOwnedByAdapter = true;
            clientAdapter.AllowPacketFragmentation = _options.AllowPacketFragmentation;
            await _clientHandler(clientAdapter).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is ObjectDisposedException)
            {
                // It can happen that the listener socket is accessed after the cancellation token is already set and the listener socket is disposed.
                return;
            }

            if (exception is SocketException socketException &&
                socketException.SocketErrorCode == SocketError.OperationAborted)
            {
                return;
            }

            _logger.Error(exception, "Error while handling TCP client connection");
        }
        finally
        {
            if (channelOwnedByAdapter)
            {
                // MqttChannelAdapter.Dispose (via the using above) released the channel,
                // which released socket/stream/peer cert. Nothing more to do.
                _logger.Debug("Cleanup branch=adapter-owned (Remote={0})", remoteEndPoint);
            }
            else if (tcpChannel != null)
            {
                // Channel was constructed but adapter ctor (or formatter/buffer-writer
                // construction) threw before the using could take effect. Dispose the
                // channel ourselves — it owns socket, stream, and peer cert.
                _logger.Debug("Cleanup branch=orphaned-channel (Remote={0})", remoteEndPoint);
                try { tcpChannel.Dispose(); }
                catch (Exception disposeException)
                {
                    _logger.Error(disposeException, "Error while disposing orphaned MqttTcpChannel");
                }
            }
            else
            {
                _logger.Debug("Cleanup branch=pre-channel (Remote={0}, HasSslStream={1}, HasStream={2})", remoteEndPoint, sslStream != null, stream != null);
                try
                {
                    // Pre-channel cleanup. On TLS-auth failure `stream` still points to the
                    // raw NetworkStream and `sslStream` was never assigned to `stream` (that
                    // only happens after a successful AuthenticateAsServerAsync). Dispose
                    // the SslStream — it owns the inner NetworkStream (leaveInnerStreamOpen:
                    // false above) so the stream chain is released. On non-TLS or pre-
                    // SslStream paths fall back to disposing whatever `stream` we have.
                    if (sslStream != null && !ReferenceEquals(stream, sslStream))
                    {
                        await sslStream.DisposeAsync().ConfigureAwait(false);
                    }
                    else if (stream != null)
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }

                    // NetworkStream above was constructed with ownsSocket:false, so the
                    // socket has to be closed explicitly regardless of which stream we just
                    // disposed.
                    clientSocket?.Dispose();
                }
                catch (Exception disposeException)
                {
                    _logger.Error(disposeException, "Error while cleaning up client connection");
                }
            }
        }

        _logger.Verbose("TCP client '{0}' disconnected (Local endpoint={1})", remoteEndPoint, _localEndPoint);
    }
}