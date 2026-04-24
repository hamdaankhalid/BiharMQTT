// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Collections.Concurrent;
using BiharMQTT.Adapter;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Internal;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;
using BiharMQTT.Server.Internal;
using BiharMQTT.Server.Internal.Adapter;

namespace BiharMQTT.Server;

public class MqttServer : Disposable
{
    readonly MqttTcpServerAdapter _adapter;
    readonly MqttClientSessionsManager _clientSessionsManager;
    readonly MqttServerKeepAliveMonitor _keepAliveMonitor;
    readonly MqttNetSourceLogger _logger;
    readonly MqttServerOptions _options;
    readonly MqttRetainedMessagesManager _retainedMessagesManager;
    readonly MessageRingBuffer _ringBuffer;
    readonly IMqttNetLogger _rootLogger;

    CancellationTokenSource _cancellationTokenSource;
    bool _isStopping;

    public MqttServer(MqttServerOptions options, MqttTcpServerAdapter adapter, IMqttNetLogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        ArgumentNullException.ThrowIfNull(adapter);

        _adapter = adapter;

        _rootLogger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger = logger.WithSource(nameof(MqttServer));

        _ringBuffer = new MessageRingBuffer(options.RingBufferCapacityBytes, options.RingBufferMaxSlots);

        _retainedMessagesManager = new MqttRetainedMessagesManager(_rootLogger);
        _clientSessionsManager = new MqttClientSessionsManager(options, _retainedMessagesManager, _rootLogger, _ringBuffer);
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
    ///     Gets ring buffer diagnostic counters.
    /// </summary>
    public MessageRingBufferDiagnostics GetRingBufferDiagnostics()
    {
        return new MessageRingBufferDiagnostics
        {
            CapacityBytes = _ringBuffer.Capacity,
            TotalAcquired = _ringBuffer.TotalAcquired,
            TotalReleased = _ringBuffer.TotalReleased
        };
    }

    /// <summary>
    ///     Gives access to the session items which belong to this server. This session items are passed
    ///     to several events instead of the client session items if the event is caused by the server instead of a client.
    /// </summary>
    public IDictionary ServerSessionItems { get; } = new ConcurrentDictionary<object, object>();

    public Task DeleteRetainedMessagesAsync()
    {
        ThrowIfNotStarted();

        return _retainedMessagesManager?.ClearMessages() ?? CompletedTask.Instance;
    }

    public Task DisconnectClientAsync(string id, MqttServerClientDisconnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(options);

        ThrowIfNotStarted();

        return _clientSessionsManager.GetClient(id).StopAsync(options);
    }

    public Task<IList<MqttClientStatus>> GetClientsAsync()
    {
        ThrowIfNotStarted();

        return _clientSessionsManager.GetClientsStatus();
    }

    public Task<MqttApplicationMessage> GetRetainedMessageAsync(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);

        ThrowIfNotStarted();

        return _retainedMessagesManager.GetMessage(topic);
    }

    public Task<IList<MqttApplicationMessage>> GetRetainedMessagesAsync()
    {
        ThrowIfNotStarted();

        return _retainedMessagesManager.GetMessages();
    }

    public Task<IList<MqttSessionStatus>> GetSessionsAsync()
    {
        ThrowIfNotStarted();

        return _clientSessionsManager.GetSessionsStatus();
    }

    public Task<MqttSessionStatus> GetSessionAsync(string id)
    {
        ThrowIfNotStarted();

        return _clientSessionsManager.GetSessionStatus(id);
    }

    public Task InjectApplicationMessage(InjectedMqttApplicationMessage injectedApplicationMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(injectedApplicationMessage);
        ArgumentNullException.ThrowIfNull(injectedApplicationMessage.ApplicationMessage);

        MqttTopicValidator.ThrowIfInvalid(injectedApplicationMessage.ApplicationMessage.Topic);

        ThrowIfNotStarted();

        if (string.IsNullOrEmpty(injectedApplicationMessage.ApplicationMessage.Topic))
        {
            throw new NotSupportedException("Injected application messages must contain a topic (topic alias is not supported)");
        }

        var sessionItems = injectedApplicationMessage.CustomSessionItems ?? ServerSessionItems;

        return _clientSessionsManager.DispatchApplicationMessage(
            injectedApplicationMessage.SenderClientId,
            injectedApplicationMessage.SenderUserName,
            sessionItems,
            injectedApplicationMessage.ApplicationMessage,
            cancellationToken);
    }

    /// <summary>
    ///     Injects an application message using a stack-allocated <see cref="MqttBufferedApplicationMessage" />.
    ///     This overload avoids the heap allocations of <see cref="InjectedMqttApplicationMessage" /> and
    ///     <see cref="MqttApplicationMessage" /> and allows the caller to supply a pooled
    ///     <see cref="ReadOnlyMemory{T}" /> payload that can be reclaimed once this method completes.
    /// </summary>
    public Task InjectApplicationMessage(
        MqttBufferedApplicationMessage message,
        string senderClientId = "",
        string senderUserName = null,
        IDictionary customSessionItems = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(message.Topic))
        {
            throw new NotSupportedException("Injected application messages must contain a topic (topic alias is not supported)");
        }

        MqttTopicValidator.ThrowIfInvalid(message.Topic);

        ThrowIfNotStarted();

        var sessionItems = customSessionItems ?? ServerSessionItems;

        return _clientSessionsManager.DispatchApplicationMessage(
            senderClientId ?? string.Empty,
            senderUserName,
            sessionItems,
            message,
            cancellationToken);
    }

    public async Task StartAsync()
    {
        ThrowIfStarted();

        _isStopping = false;

        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        await _retainedMessagesManager.Start().ConfigureAwait(false);
        _clientSessionsManager.Start();
        _keepAliveMonitor.Start(cancellationToken);

        _adapter.ClientHandler = c => OnHandleClient(c, cancellationToken);
        await _adapter.StartAsync(_options, _rootLogger).ConfigureAwait(false);

        _logger.Info("Started");
    }

    public async Task StopAsync(MqttServerStopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            if (_cancellationTokenSource == null)
            {
                return;
            }

            _isStopping = true;

            _cancellationTokenSource.Cancel(false);

            await _clientSessionsManager.CloseAllConnections(options.DefaultClientDisconnectOptions).ConfigureAwait(false);

            _adapter.ClientHandler = null;
            await _adapter.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        _logger.Info("Stopped");
    }

    public Task SubscribeAsync(string clientId, ICollection<MqttTopicFilter> topicFilters)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(topicFilters);

        foreach (var topicFilter in topicFilters)
        {
            MqttTopicValidator.ThrowIfInvalidSubscribe(topicFilter.Topic);
        }

        ThrowIfDisposed();
        ThrowIfNotStarted();

        return _clientSessionsManager.SubscribeAsync(clientId, topicFilters);
    }

    public Task UnsubscribeAsync(string clientId, ICollection<string> topicFilters)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(topicFilters);

        ThrowIfDisposed();
        ThrowIfNotStarted();

        return _clientSessionsManager.UnsubscribeAsync(clientId, topicFilters);
    }

    public Task UpdateRetainedMessageAsync(MqttApplicationMessage retainedMessage)
    {
        ArgumentNullException.ThrowIfNull(retainedMessage);

        ThrowIfDisposed();
        ThrowIfNotStarted();

        return _retainedMessagesManager?.UpdateMessage(string.Empty, retainedMessage);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAsync(new MqttServerStopOptions()).GetAwaiter().GetResult();
            _adapter.Dispose();
            _ringBuffer?.Dispose();
        }

        base.Dispose(disposing);
    }

    Task OnHandleClient(MqttChannelAdapter channelAdapter, CancellationToken cancellationToken)
    {
        if (_isStopping || !AcceptNewConnections)
        {
            return CompletedTask.Instance;
        }

        return _clientSessionsManager.HandleClientConnectionAsync(channelAdapter, cancellationToken);
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