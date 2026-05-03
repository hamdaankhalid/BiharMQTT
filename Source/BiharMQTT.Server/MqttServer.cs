using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BiharMQTT.Adapter;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Internal;
using BiharMQTT.Protocol;
using BiharMQTT.Server.Internal;

namespace BiharMQTT.Server;

public class MqttServer : Disposable
{
    readonly List<MqttTcpServerListener> _listeners = new List<MqttTcpServerListener>();

    readonly MqttClientSessionsManager _clientSessionsManager;
    readonly MqttServerKeepAliveMonitor _keepAliveMonitor;
    readonly MqttNetSourceLogger _logger;
    readonly MqttServerOptions _options;
    readonly MqttRetainedMessagesManager _retainedMessagesManager;
    readonly HugeNativeMemoryPool _nativeMemoryPool;
    readonly MqttSenderPool _senderPool;
    readonly IMqttNetLogger _rootLogger;

    CancellationTokenSource _cancellationTokenSource;


    private static MqttServerClientDisconnectOptions DefaultClientDisconnectOptions { get; } = new MqttServerClientDisconnectOptions()
    {
        ReasonCode = MqttDisconnectReasonCode.ServerShuttingDown,
        UserProperties = null,
        ReasonString = null,
        ServerReference = null
    };

    public MqttServer(MqttServerOptions options, IMqttNetLogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Server creates adapter, adapter creates listener, we can wire TCP listener with this rather easily. 
        _rootLogger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger = logger.WithSource(nameof(MqttServer));

        _nativeMemoryPool = new HugeNativeMemoryPool(
            new (uint bucketSize, int minNumBuckets)[]
            {
                (Constants.PreAllocatedQMemoryKb * 1024, Constants.MaxConcurrentConnections * 3) // Preallocated is 3 per Mqttpacketbus, and 1 per connected client
            });

        _retainedMessagesManager = new MqttRetainedMessagesManager(_rootLogger);
        _senderPool = new MqttSenderPool(options.SenderThreadCount, _rootLogger);
        _clientSessionsManager = new MqttClientSessionsManager(options, _retainedMessagesManager, _rootLogger, _nativeMemoryPool, _senderPool);
        _keepAliveMonitor = new MqttServerKeepAliveMonitor(options, _clientSessionsManager, _rootLogger);
    }

    /// <summary>
    ///     Gets or sets whether the server will accept new connections.
    ///     If not, the server will close the connection without any notification (DISCONNECT packet).
    ///     This feature can be used when the server is shutting down.
    /// </summary>
    public bool AcceptNewConnections { get; set; } = true;

    public bool IsStarted => _cancellationTokenSource != null;

    /// <summary>
    ///     Gives access to the session items which belong to this server. This session items are passed
    ///     to several events instead of the client session items if the event is caused by the server instead of a client.
    /// </summary>
    public IDictionary ServerSessionItems { get; } = new ConcurrentDictionary<object, object>();

    public Task DisconnectClientAsync(string id, MqttServerClientDisconnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfNotStarted();
        return _clientSessionsManager.GetClient(id).StopAsync(options);
    }

    /// <summary>
    /// Server-side publish entry point. Zero allocations on the call site:
    /// <paramref name="topic"/> and <paramref name="payload"/> are <strong>borrowed</strong>
    /// for the duration of this call only — the dispatch chain copies bytes into
    /// per-subscriber encoder buffers and the per-session bus before returning.
    /// <para>
    /// Goes through the same <see cref="MqttServerOptions.PublishInterceptor"/> as
    /// inbound PUBLISHes; if the interceptor returns <see cref="PublishInterceptResult.Consume"/>
    /// or <see cref="PublishInterceptResult.Reject"/>, fan-out is skipped silently.
    /// </para>
    /// <para>
    /// Do not call this method re-entrantly from inside the interceptor. I'm pretty sure it would create some sorta recursive loop.
    /// </para>
    /// </summary>
    public void Publish(
        ReadOnlySpan<byte> topic,
        ReadOnlySequence<byte> payload,
        MqttQualityOfServiceLevel qualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false)
    {
        ThrowIfNotStarted();

        _clientSessionsManager.DispatchSpan(
            senderId: string.Empty,
            topic,
            payload,
            qualityOfServiceLevel,
            retain);
    }

    public void Start()
    {
        ThrowIfStarted();

        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        _retainedMessagesManager.Start();
        _clientSessionsManager.Start();
        _keepAliveMonitor.Start(cancellationToken);

        if (_options.DefaultEndpointOptions.IsEnabled)
        {
            RegisterTcpListeners(_options.DefaultEndpointOptions, _rootLogger, cancellationToken);
        }

        if (_options.TlsEndpointOptions?.IsEnabled == true)
        {
            if (_options.TlsEndpointOptions.CertificateProvider == null)
            {
                throw new ArgumentException("TLS certificate is not set.");
            }

            // Resolve the certificate once at boot so misconfiguration (null cert, bad blob,
            // wrong PFX password) surfaces here instead of as a baffling first-handshake
            // failure later. Hot-rotating providers that legitimately produce null until a
            // later moment should override this — but those are not the common shape.
            if (_options.TlsEndpointOptions.CertificateProvider.GetCertificate() == null)
            {
                throw new ArgumentException("TLS certificate provider returned a null certificate.");
            }

            RegisterTcpListeners(_options.TlsEndpointOptions, _rootLogger, cancellationToken);
        }

        _logger.Info("Started");
    }


    void RegisterTcpListeners(MqttServerTcpEndpointBaseOptions tcpEndpointOptions, IMqttNetLogger logger, CancellationToken cancellationToken)
    {
        if (!tcpEndpointOptions.BoundInterNetworkAddress.Equals(IPAddress.None))
        {
            var listenerV4 = new MqttTcpServerListener(AddressFamily.InterNetwork, _options, tcpEndpointOptions, _clientSessionsManager, logger);

            if (listenerV4.Start(false, cancellationToken))
            {
                _listeners.Add(listenerV4);
            }
        }

        if (!tcpEndpointOptions.BoundInterNetworkV6Address.Equals(IPAddress.None))
        {
            var listenerV6 = new MqttTcpServerListener(AddressFamily.InterNetworkV6, _options, tcpEndpointOptions, _clientSessionsManager, logger);

            if (listenerV6.Start(false, cancellationToken))
            {
                _listeners.Add(listenerV6);
            }
        }
    }

    public async Task StopAsync()
    {
        var cts = _cancellationTokenSource;
        if (cts == null)
        {
            return;
        }

        try
        {
            // Cancel first so listener accept loops break out and any in-flight
            // HandleClientConnectionAsync (registered via the listener token in
            // MqttClientSessionsManager) gets RequestStop. CloseAllConnections then
            // sends graceful DISCONNECTs to the snapshot of currently-installed
            // clients; the registration handles anything that raced past the snapshot.
            cts.Cancel(false);
            await _clientSessionsManager.CloseAllConnections(DefaultClientDisconnectOptions).ConfigureAwait(false);

            foreach (var listener in _listeners)
            {
                listener.Dispose();
            }
            _listeners.Clear();
        }
        finally
        {
            cts.Dispose();
            _cancellationTokenSource = null;
        }

        _logger.Info("Stopped");
    }


    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Also cleans up tcp listeners
            StopAsync().GetAwaiter().GetResult();
            _senderPool.Dispose();
            _nativeMemoryPool.Dispose();
        }

        base.Dispose(disposing);
    }

    void ThrowIfNotStarted()
    {
        ThrowIfDisposed();

        if (_cancellationTokenSource == null)
        {
            throw new InvalidOperationException("The MQTT server is not started.");
        }
    }

    void ThrowIfStarted()
    {
        ThrowIfDisposed();

        if (_cancellationTokenSource != null)
        {
            throw new InvalidOperationException("The MQTT server is already started.");
        }
    }
}