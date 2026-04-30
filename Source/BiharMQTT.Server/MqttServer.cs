// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
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
    readonly HugeNativeMemoryPool _nativeMemoryPool;
    readonly MqttSenderPool _senderPool;
    readonly IMqttNetLogger _rootLogger;

    // HK TODO: give user ability to add an intercept here

    CancellationTokenSource _cancellationTokenSource;
    bool _isStopping;

    public MqttServer(MqttServerOptions options, MqttTcpServerAdapter adapter, IMqttNetLogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        ArgumentNullException.ThrowIfNull(adapter);

        _adapter = adapter;

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

    public Task DeleteRetainedMessagesAsync()
    {
        ThrowIfNotStarted();

        return _retainedMessagesManager?.ClearMessages() ?? Task.CompletedTask;
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
    /// Do not call this method re-entrantly from inside the interceptor.
    /// </para>
    /// </summary>
    public unsafe void Publish(
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

        _isStopping = false;

        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        _retainedMessagesManager.Start();
        _clientSessionsManager.Start();
        _keepAliveMonitor.Start(cancellationToken);

        _adapter.Start(_options, _rootLogger, c => OnHandleClient(c, cancellationToken));

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
            var topicString = topicFilter.Topic.Count == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(topicFilter.Topic.Array!, topicFilter.Topic.Offset, topicFilter.Topic.Count);
            MqttTopicValidator.ThrowIfInvalidSubscribe(topicString);
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
            _senderPool.Dispose();
            _nativeMemoryPool.Dispose();
        }

        base.Dispose(disposing);
    }

    // Callback called by Adapter when a new client connects
    Task OnHandleClient(MqttChannelAdapter channelAdapter, CancellationToken cancellationToken)
    {
        if (_isStopping || !AcceptNewConnections)
        {
            return Task.CompletedTask;
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