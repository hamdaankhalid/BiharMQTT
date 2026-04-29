// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BiharMQTT.Adapter;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Exceptions;
using BiharMQTT.Formatter;
using BiharMQTT.Formatter.V5;
using BiharMQTT.Internal;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;
using BiharMQTT.Server.Internal.Formatter;
using MqttPublishPacketFactory = BiharMQTT.Server.Internal.Formatter.MqttPublishPacketFactory;
using static BiharMQTT.Internal.MqttSegmentHelper;

namespace BiharMQTT.Server.Internal;

public sealed class MqttClientSessionsManager : ISubscriptionChangedNotification, IDisposable
{
    readonly Dictionary<string, MqttConnectedClient> _clients = new(4096);
    readonly AsyncLock _createConnectionSyncRoot = new();
    readonly MqttNetSourceLogger _logger;
    readonly MqttServerOptions _options;
    readonly MqttRetainedMessagesManager _retainedMessagesManager;
    readonly IMqttNetLogger _rootLogger;
    readonly ReaderWriterLockSlim _sessionsManagementLock = new();

    // The _sessions dictionary contains all session, the _subscriberSessions hash set contains subscriber sessions only.
    // See the MqttSubscription object for a detailed explanation.
    readonly MqttSessionsStorage _sessionsStorage = new();
    readonly HashSet<MqttSession> _subscriberSessions = [];

    // Copy-on-write snapshot rebuilt on subscribe/unsubscribe (under write lock).
    // Publish dispatch reads this via a single volatile read — zero allocation.
    volatile MqttSession[] _subscriberSessionsSnapshot = [];

    readonly HugeNativeMemoryPool _hugeNativeMemoryPool;
    readonly MqttSenderPool _senderPool;


    public MqttClientSessionsManager(
        MqttServerOptions options,
        MqttRetainedMessagesManager retainedMessagesManager,
        IMqttNetLogger logger,
        HugeNativeMemoryPool hugeNativeMemoryPool,
        MqttSenderPool senderPool)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hugeNativeMemoryPool);
        ArgumentNullException.ThrowIfNull(senderPool);

        _logger = logger.WithSource(nameof(MqttClientSessionsManager));
        _rootLogger = logger;

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _retainedMessagesManager = retainedMessagesManager ?? throw new ArgumentNullException(nameof(retainedMessagesManager));
        _hugeNativeMemoryPool = hugeNativeMemoryPool;
        _senderPool = senderPool;
    }

    public async Task CloseAllConnections(MqttServerClientDisconnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<MqttConnectedClient> connections;
        lock (_clients)
        {
            connections = _clients.Values.ToList();
            _clients.Clear();
        }

        foreach (var connection in connections)
        {
            await connection.StopAsync(options).ConfigureAwait(false);
        }
    }

    public async Task DeleteSessionAsync(string clientId)
    {
        _logger.Verbose("Deleting session for client '{0}'.", clientId);

        MqttConnectedClient connection;
        lock (_clients)
        {
            _clients.TryGetValue(clientId, out connection);
        }

        MqttSession session;
        _sessionsManagementLock.EnterWriteLock();
        try
        {
            if (_sessionsStorage.TryRemoveSession(clientId, out session))
            {
                _subscriberSessions.Remove(session);
                _subscriberSessionsSnapshot = [.. _subscriberSessions];
            }
        }
        finally
        {
            _sessionsManagementLock.ExitWriteLock();
        }

        try
        {
            if (connection != null)
            {
                await connection.StopAsync(new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.NormalDisconnection }).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Error while deleting session '{0}'", clientId);
        }

        session?.Dispose();

        _logger.Verbose("Session of client '{0}' deleted", clientId);
    }

    public unsafe DispatchApplicationMessageResult DispatchPublishPacketDirect(
        string senderId,
        MqttPublishPacket publishPacket)
    {
        var interceptor = _options.PublishInterceptor;
        if (interceptor != null)
        {
            // Snapshot the args on the stack — ref struct, no allocation.
            var args = new MqttPublishInterceptArgs
            {
                SenderClientId = senderId,
                Topic = publishPacket.Topic.AsSpan(),
                Payload = publishPacket.Payload,
                QualityOfServiceLevel = publishPacket.QualityOfServiceLevel,
                Retain = publishPacket.Retain,
            };

            switch (interceptor(in args))
            {
                case PublishInterceptResult.Consume:
                    return new DispatchApplicationMessageResult(
                        (int)MqttPubAckReasonCode.Success, closeConnection: false, reasonString: null, userProperties: null);
                case PublishInterceptResult.Reject:
                    return new DispatchApplicationMessageResult(
                        (int)MqttPubAckReasonCode.NotAuthorized, closeConnection: false, reasonString: null, userProperties: null);
                case PublishInterceptResult.Allow:
                default:
                    break;
            }
        }

        return Dispatch(
            senderId,
            publishPacket.Topic,
            publishPacket.Payload,
            publishPacket.QualityOfServiceLevel,
            publishPacket.Retain,
            publishPacket.ContentType,
            publishPacket.ResponseTopic,
            publishPacket.CorrelationData,
            publishPacket.MessageExpiryInterval,
            publishPacket.PayloadFormatIndicator,
            publishPacket.TopicAlias,
            publishPacket.SubscriptionIdentifiers,
            publishPacket.UserProperties);
    }

    // Per-thread topic scratch for the server-publish path. The dispatch chain
    // requires an ArraySegment<byte> for the topic, which the encoder copies
    // bytes out of synchronously. We grow this once per thread (typical MQTT
    // topic stays well under 256 bytes), reuse forever.
    [ThreadStatic] static byte[] t_topicScratch;

    /// <summary>
    /// Server-side publish entry point. Materializes the borrowed topic span
    /// into a thread-static byte[] for one synchronous trip through
    /// <see cref="Dispatch"/>; payload flows by reference. Both
    /// buffers are reusable as soon as this method returns.
    /// </summary>
    internal unsafe void DispatchSpan(
        string senderId,
        ReadOnlySpan<byte> topic,
        ReadOnlySequence<byte> payload,
        MqttQualityOfServiceLevel qos,
        bool retain)
    {
        var interceptor = _options.PublishInterceptor;
        if (interceptor != null)
        {
            var args = new MqttPublishInterceptArgs
            {
                SenderClientId = senderId,
                Topic = topic,
                Payload = payload,
                QualityOfServiceLevel = qos,
                Retain = retain,
            };

            // Server-published messages also go through the interceptor. Consume
            // and Reject both short-circuit — there's no client-side ack to
            // produce here, so the result code is dropped.
            if (interceptor(in args) != PublishInterceptResult.Allow)
            {
                return;
            }
        }

        var scratch = t_topicScratch;
        if (scratch == null || scratch.Length < topic.Length)
        {
            scratch = t_topicScratch = new byte[Math.Max(256, topic.Length)];
        }
        topic.CopyTo(scratch);
        var topicSegment = new ArraySegment<byte>(scratch, 0, topic.Length);

        _ = Dispatch(
            senderId,
            topicSegment,
            payload,
            qos,
            retain,
            contentType: default,
            responseTopic: default,
            correlationData: default,
            messageExpiryInterval: 0,
            payloadFormatIndicator: default,
            topicAlias: 0,
            subscriptionIdentifiers: null,
            userProperties: null);
        // After Dispatch returns every matching session has
        // already encoded + copied the topic bytes (MqttBufferWriter.WriteString
        // does a memcpy into the encoder's owned buffer), so the scratch
        // buffer is safe to reuse on the next call from this thread.
    }

    static ArraySegment<byte> ToSegment(string s) => string.IsNullOrEmpty(s) ? default : new ArraySegment<byte>(Encoding.UTF8.GetBytes(s));

    /// <summary>User inject application message from in-process in server</summary>
    public DispatchApplicationMessageResult DispatchApplicationMessage(
        string senderId,
        string senderUserName,
        IDictionary senderSessionItems,
        MqttApplicationMessage applicationMessage)
    {
        return Dispatch(
            senderId,
            ToSegment(applicationMessage.Topic),
            applicationMessage.Payload,
            applicationMessage.QualityOfServiceLevel,
            applicationMessage.Retain,
            ToSegment(applicationMessage.ContentType),
            ToSegment(applicationMessage.ResponseTopic),
            applicationMessage.CorrelationData == null ? default : new ArraySegment<byte>(applicationMessage.CorrelationData),
            applicationMessage.MessageExpiryInterval,
            applicationMessage.PayloadFormatIndicator,
            applicationMessage.TopicAlias,
            applicationMessage.SubscriptionIdentifiers,
            applicationMessage.UserProperties);
    }

    public DispatchApplicationMessageResult DispatchApplicationMessage(
        string senderId,
        string senderUserName,
        IDictionary senderSessionItems,
        MqttBufferedApplicationMessage message)
    {
        return Dispatch(
            senderId,
            ToSegment(message.Topic),
            message.Payload.Length > 0
                ? new ReadOnlySequence<byte>(message.Payload)
                : ReadOnlySequence<byte>.Empty,
            message.QualityOfServiceLevel,
            message.Retain,
            ToSegment(message.ContentType),
            ToSegment(message.ResponseTopic),
            message.CorrelationData == null ? default : new ArraySegment<byte>(message.CorrelationData),
            message.MessageExpiryInterval,
            message.PayloadFormatIndicator,
            0,
            message.SubscriptionIdentifiers,
            message.UserProperties);
    }

    public void Dispose()
    {
        _createConnectionSyncRoot.Dispose();

        _sessionsManagementLock.EnterWriteLock();
        try
        {
            _sessionsStorage.Dispose();
        }
        finally
        {
            _sessionsManagementLock.ExitWriteLock();
        }

        _sessionsManagementLock?.Dispose();
    }

    public MqttConnectedClient GetClient(string id)
    {
        lock (_clients)
        {
            if (!_clients.TryGetValue(id, out var client))
            {
                throw new InvalidOperationException($"Client with ID '{id}' not found.");
            }

            return client;
        }
    }

    public List<MqttConnectedClient> GetClients()
    {
        lock (_clients)
        {
            return _clients.Values.ToList();
        }
    }

    public Task<IList<MqttClientStatus>> GetClientsStatus()
    {
        var result = new List<MqttClientStatus>();

        lock (_clients)
        {
            foreach (var client in _clients.Values)
            {
                var clientStatus = new MqttClientStatus(client)
                {
                    Session = new MqttSessionStatus(client.Session)
                };

                result.Add(clientStatus);
            }
        }

        return Task.FromResult((IList<MqttClientStatus>)result);
    }

    public Task<IList<MqttSessionStatus>> GetSessionsStatus()
    {
        var result = new List<MqttSessionStatus>();

        _sessionsManagementLock.EnterReadLock();
        try
        {
            foreach (var session in _sessionsStorage.ReadAllSessions())
            {
                var sessionStatus = new MqttSessionStatus(session);
                result.Add(sessionStatus);
            }
        }
        finally
        {
            _sessionsManagementLock.ExitReadLock();
        }

        return Task.FromResult((IList<MqttSessionStatus>)result);
    }

    public Task<MqttSessionStatus> GetSessionStatus(string id)
    {
        _sessionsManagementLock.EnterReadLock();
        try
        {
            if (!_sessionsStorage.TryGetSession(id, out var session))
            {
                throw new InvalidOperationException($"Session with ID '{id}' not found.");
            }

            var sessionStatus = new MqttSessionStatus(session);
            return Task.FromResult(sessionStatus);
        }
        finally
        {
            _sessionsManagementLock.ExitReadLock();
        }
    }

    // Do MQTT handshake 
    public async Task HandleClientConnectionAsync(MqttChannelAdapter channelAdapter, CancellationToken cancellationToken)
    {
        MqttConnectedClient connectedClient = null;

        try
        {
            var (success, connectPacket) = await ReceiveConnectPacket(channelAdapter, cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                // Nothing was received in time etc.
                return;
            }

            bool clientIdWasEmpty = connectPacket.ClientId.Count == 0;
            MqttConnectReasonCode reason = ValidateConnection(ref connectPacket, channelAdapter);

            // User-supplied validator runs only after the broker's own checks
            // pass — re-using the function-pointer pattern from PublishInterceptor.
            // Lets a host re-add Firebase-token / username-password gating on the
            // plain-TCP (1883) path while leaving mTLS-trusted (8883) clients alone.
            if (reason == MqttConnectReasonCode.Success)
            {
                reason = InvokeConnectionValidator(_options, ref connectPacket, channelAdapter);
            }

            var connAckPacket = new MqttConnAckPacket
            {
                ReasonCode = reason,
                RetainAvailable = true,
                SubscriptionIdentifiersAvailable = true,
                SharedSubscriptionAvailable = false,
                TopicAliasMaximum = ushort.MaxValue,
                MaximumQoS = MqttQualityOfServiceLevel.ExactlyOnce,
                WildcardSubscriptionAvailable = true,

                AuthenticationMethod = connectPacket.AuthenticationMethod,
                AuthenticationData = connectPacket.AuthenticationData,
                // Per MQTT v5 §3.2.2.3.7: only include AssignedClientIdentifier when server assigned the ID
                AssignedClientIdentifier = clientIdWasEmpty ? connectPacket.ClientId : default,
                ReasonString = default,
                ServerReference = default,
                UserProperties = connectPacket.UserProperties,

                ResponseInformation = default,
                MaximumPacketSize = 0, // Unlimited,
                ReceiveMaximum = 0 // Unlimited
            };

            if (reason != MqttConnectReasonCode.Success)
            {
                // Send failure response here without preparing a connection and session!
                var encoder = channelAdapter.PacketFormatterAdapter.Encoder;
                var failBuffer = encoder.Encode(ref connAckPacket);
                channelAdapter.SendPacket(failBuffer.Packet.AsMemory(), failBuffer.Payload, cancellationToken);
                return;
            }

            // Pass connAckPacket so that IsSessionPresent flag can be set if the client session already exists.
            connectedClient = await CreateClientConnection(connectPacket, connAckPacket, channelAdapter).ConfigureAwait(false);

            MqttV5PacketEncoder connAckEncoder = channelAdapter.PacketFormatterAdapter.Encoder;
            MqttPacketBuffer connAckBuffer = connAckEncoder.Encode(ref connAckPacket);
            connectedClient.SendPacket(connAckBuffer.Packet, connAckBuffer.Payload, cancellationToken);

            // Fire after CONNACK is on the wire and the session is installed,
            // before the receive loop arms — so the host's connect-side bookkeeping
            // sees a fully-installed client.
            InvokeClientConnected(_options, connectedClient);

            await connectedClient.RunAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error(exception, exception.Message);
        }
        finally
        {
            if (connectedClient != null)
            {
                if (connectedClient.Id != null)
                {
                    // in case it is a takeover _clientConnections already contains the new connection
                    if (!connectedClient.IsTakenOver)
                    {
                        lock (_clients)
                        {
                            _clients.Remove(connectedClient.Id);
                        }

                        if (!_options.EnablePersistentSessions || !ShouldPersistSession(connectedClient))
                        {
                            await DeleteSessionAsync(connectedClient.Id).ConfigureAwait(false);
                        }
                    }

                    // Fire only when ClientConnected fired earlier (Id assignment
                    // happens inside CreateClientConnection, same point the connect
                    // hook runs). Takeover and a peer-sent DISCONNECT are
                    // distinguished from an abrupt drop here.
                    var disconnectType = connectedClient.IsTakenOver
                        ? MqttClientDisconnectType.Takeover
                        : connectedClient.DisconnectPacket != null
                            ? MqttClientDisconnectType.Clean
                            : MqttClientDisconnectType.NotClean;
                    InvokeClientDisconnected(_options, connectedClient, disconnectType);
                }
            }

            using var timeout = new CancellationTokenSource(_options.DefaultCommunicationTimeout);
            await channelAdapter.DisconnectAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    public void OnSubscriptionsAdded(MqttSession clientSession, List<string> subscriptionsTopics)
    {
        _sessionsManagementLock.EnterWriteLock();
        try
        {
            if (!clientSession.HasSubscribedTopics)
            {
                // first subscribed topic
                _subscriberSessions.Add(clientSession);
            }

            foreach (var topic in subscriptionsTopics)
            {
                clientSession.AddSubscribedTopic(topic);
            }

            _subscriberSessionsSnapshot = [.. _subscriberSessions];
        }
        finally
        {
            _sessionsManagementLock.ExitWriteLock();
        }
    }

    public void OnSubscriptionsRemoved(MqttSession clientSession, List<string> subscriptionTopics)
    {
        _sessionsManagementLock.EnterWriteLock();
        try
        {
            foreach (var subscriptionTopic in subscriptionTopics)
            {
                clientSession.RemoveSubscribedTopic(subscriptionTopic);
            }

            if (!clientSession.HasSubscribedTopics)
            {
                // last subscription removed
                _subscriberSessions.Remove(clientSession);
            }

            _subscriberSessionsSnapshot = [.. _subscriberSessions];
        }
        finally
        {
            _sessionsManagementLock.ExitWriteLock();
        }
    }

    public void Start()
    {
        if (!_options.EnablePersistentSessions)
        {
            _sessionsStorage.Clear();
        }
    }

    public async Task SubscribeAsync(string clientId, ICollection<MqttTopicFilter> topicFilters)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(topicFilters);

        var fakeSubscribePacket = new MqttSubscribePacket { TopicFilters = new List<MqttTopicFilter>(topicFilters) };

        var clientSession = GetClientSession(clientId);

        var subscribeResult = clientSession.Subscribe(ref fakeSubscribePacket);
        if (subscribeResult.RetainedMessages == null)
        {
            return;
        }

        foreach (var retainedMessageMatch in subscribeResult.RetainedMessages)
        {
            var publishPacket = MqttPublishPacketFactory.Create(retainedMessageMatch);
            clientSession.EnqueuePublishPacket(ref publishPacket);
        }
    }

    public Task UnsubscribeAsync(string clientId, ICollection<string> topicFilters)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(topicFilters);

        var fakeUnsubscribePacket = new MqttUnsubscribePacket { TopicFilters = new List<ArraySegment<byte>>(topicFilters.Select(t => ToSegment(t))) };

        GetClientSession(clientId).Unsubscribe(fakeUnsubscribePacket, CancellationToken.None);
        return Task.CompletedTask;
    }

    async Task<MqttConnectedClient> CreateClientConnection(
        MqttConnectPacket connectPacket,
        MqttConnAckPacket connAckPacket,
        MqttChannelAdapter channelAdapter)
    {
        MqttConnectedClient connectedClient;
        // Materialize it once and store it
        string clientId = SegmentToString(connectPacket.ClientId);

        using (await _createConnectionSyncRoot.EnterAsync().ConfigureAwait(false))
        {
            MqttSession oldSession;
            MqttConnectedClient oldConnectedClient;

            _sessionsManagementLock.EnterWriteLock();
            try
            {
                MqttSession session;
                // Create a new session (if required).
                if (!_sessionsStorage.TryGetSession(clientId, out oldSession))
                {
                    session = CreateSession(connectPacket, new ConcurrentDictionary<object, object>());
                }
                else
                {
                    if (connectPacket.CleanSession)
                    {
                        _logger.Verbose("Deleting existing session of client '{0}' due to clean start", clientId);
                        _subscriberSessions.Remove(oldSession);
                        _subscriberSessionsSnapshot = [.. _subscriberSessions];
                        session = CreateSession(connectPacket, new ConcurrentDictionary<object, object>());
                    }
                    else
                    {
                        _logger.Verbose("Reusing existing session of client '{0}'", clientId);
                        session = oldSession;
                        oldSession = null;

                        session.DisconnectedTimestamp = null;
                        session.Recover();

                        connAckPacket.IsSessionPresent = true;
                    }
                }

                _sessionsStorage.UpdateSession(clientId, session);

                // Create a new client (always required).
                lock (_clients)
                {
                    _clients.TryGetValue(clientId, out oldConnectedClient);
                    if (oldConnectedClient != null)
                    {
                        // This will stop the current client from sending and receiving but remains the connection active
                        // for a later DISCONNECT packet.
                        oldConnectedClient.IsTakenOver = true;
                    }

                    connectedClient = new MqttConnectedClient(connectPacket, channelAdapter, session, _options, this, _senderPool, _rootLogger);
                    _clients[clientId] = connectedClient;
                }
            }
            finally
            {
                _sessionsManagementLock.ExitWriteLock();
            }

            if (oldConnectedClient != null)
            {
                // TODO: Consider event here for session takeover to allow manipulation of user properties etc.
                await oldConnectedClient.StopAsync(new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.SessionTakenOver }).ConfigureAwait(false);
            }

            oldSession?.Dispose();
        }

        return connectedClient;
    }

    MqttSession CreateSession(MqttConnectPacket connectPacket, IDictionary sessionItems)
    {
        _logger.Verbose("Created new session for client '{0}'", SegmentToString(connectPacket.ClientId));
        return new MqttSession(connectPacket, sessionItems, _options, _retainedMessagesManager, this, _hugeNativeMemoryPool);
    }

    DispatchApplicationMessageResult Dispatch(
        string senderId,
        ArraySegment<byte> topicSegment,
        ReadOnlySequence<byte> payload,
        MqttQualityOfServiceLevel qos,
        bool retain,
        ArraySegment<byte> contentType,
        ArraySegment<byte> responseTopic,
        ArraySegment<byte> correlationData,
        uint messageExpiryInterval,
        MqttPayloadFormatIndicator payloadFormatIndicator,
        ushort topicAlias,
        List<uint> subscriptionIdentifiers,
        List<MqttUserProperty> userProperties)
    {
        int matchingSubscribersCount = 0;
        try
        {
            MqttSession[] subscriberSessions = _subscriberSessionsSnapshot;
            Span<byte> topicSpan = topicSegment.AsSpan<byte>();

            // NOTE: Topics always must be ASCII only
            MqttTopicHash.Calculate(topicSpan, out var topicHash, out _, out _);

            foreach (MqttSession session in subscriberSessions)
            {
                if (!session.TryCheckSubscriptions(topicSpan, topicHash, qos, senderId, out CheckSubscriptionsResult checkSubscriptionsResult))
                {
                    continue;
                }

                if (!checkSubscriptionsResult.IsSubscribed)
                {
                    continue;
                }

                MqttPublishPacket publishPacketCopy = new MqttPublishPacket
                {
                    Topic = topicSegment,
                    Payload = payload,
                    QualityOfServiceLevel = checkSubscriptionsResult.QualityOfServiceLevel,
                    SubscriptionIdentifiers = checkSubscriptionsResult.SubscriptionIdentifiers,
                    Retain = checkSubscriptionsResult.RetainAsPublished && retain,
                    ContentType = contentType,
                    ResponseTopic = responseTopic,
                    CorrelationData = correlationData,
                    MessageExpiryInterval = messageExpiryInterval,
                    PayloadFormatIndicator = payloadFormatIndicator,
                    TopicAlias = topicAlias,
                    UserProperties = userProperties
                };

                if (publishPacketCopy.QualityOfServiceLevel > 0)
                {
                    publishPacketCopy.PacketIdentifier = session.PacketIdentifierProvider.GetNextPacketIdentifier();
                }

                matchingSubscribersCount++;

                session.EnqueuePublishPacket(ref publishPacketCopy);

                // _logger.Verbose("Client '{0}': Queued PUBLISH packet with topic '{1}'", session.Id, topicSpan);
            }


            if (matchingSubscribersCount == 0)
            {
                return new DispatchApplicationMessageResult(
                    (int)MqttPubAckReasonCode.NoMatchingSubscribers,
                    closeConnection: false,
                    reasonString: null,
                    userProperties: null);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Error while processing next queued application message");
        }

        return new DispatchApplicationMessageResult(
            (int)MqttPubAckReasonCode.Success,
            closeConnection: false,
            reasonString: null,
            userProperties: null);
    }

    MqttSession GetClientSession(string clientId)
    {
        _sessionsManagementLock.EnterReadLock();
        try
        {
            if (!_sessionsStorage.TryGetSession(clientId, out var session))
            {
                throw new InvalidOperationException($"Client session '{clientId}' is unknown.");
            }

            return session;
        }
        finally
        {
            _sessionsManagementLock.ExitReadLock();
        }
    }

    async Task<(bool Success, MqttConnectPacket Packet)> ReceiveConnectPacket(MqttChannelAdapter channelAdapter, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutToken = new CancellationTokenSource(_options.DefaultCommunicationTimeout);
            using var effectiveCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(timeoutToken.Token, cancellationToken);
            var receivedPacket = await channelAdapter.ReceivePacketAsync(effectiveCancellationToken.Token).ConfigureAwait(false);
            if (receivedPacket.TotalLength > 0 && receivedPacket.PacketType == MqttControlPacketType.Connect)
            {
                var decoder = channelAdapter.PacketFormatterAdapter.Decoder;
                var connectPacket = decoder.DecodeConnectPacket(receivedPacket.Body);
                return (true, connectPacket);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Client '{0}': Connected but did not sent a CONNECT packet.", channelAdapter.RemoteEndPoint);
        }
        catch (MqttCommunicationTimedOutException)
        {
            _logger.Warning("Client '{0}': Connected but did not sent a CONNECT packet.", channelAdapter.RemoteEndPoint);
        }

        _logger.Warning("Client '{0}': First received packet was no 'CONNECT' packet [MQTT-3.1.0-1].", channelAdapter.RemoteEndPoint);
        return (false, default);
    }

    static bool ShouldPersistSession(MqttConnectedClient connectedClient)
    {
        switch (connectedClient.ChannelAdapter.PacketFormatterAdapter.ProtocolVersion)
        {
            case MqttProtocolVersion.V500:
                {
                    // MQTT 5.0 section 3.1.2.11.2
                    // The Client and Server MUST store the Session State after the Network Connection is closed if the Session Expiry Interval is greater than 0 [MQTT-3.1.2-23].
                    //
                    // A Client that only wants to process messages while connected will set the Clean Start to 1 and set the Session Expiry Interval to 0.
                    // It will not receive Application Messages published before it connected and has to subscribe afresh to any topics that it is interested
                    // in each time it connects.

                    var effectiveSessionExpiryInterval = connectedClient.DisconnectPacket?.SessionExpiryInterval ?? 0U;
                    if (effectiveSessionExpiryInterval == 0U)
                    {
                        // From RFC: If the Session Expiry Interval is absent, the Session Expiry Interval in the CONNECT packet is used.
                        effectiveSessionExpiryInterval = connectedClient.ConnectPacket.SessionExpiryInterval;
                    }

                    return effectiveSessionExpiryInterval != 0U;
                }

            default:
                throw new NotSupportedException();
        }
    }

    private static MqttConnectReasonCode ValidateConnection(ref MqttConnectPacket connectPacket, MqttChannelAdapter channelAdapter)
    {
        // Check the client ID and set a random one if supported.
        if (connectPacket.ClientId.Count == 0 && channelAdapter.PacketFormatterAdapter.ProtocolVersion == MqttProtocolVersion.V500)
        {
            connectPacket.ClientId = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));
        }

        if (connectPacket.ClientId.Count == 0)
        {
            return MqttConnectReasonCode.ClientIdentifierNotValid;
        }

        return MqttConnectReasonCode.Success;
    }

    // Function-pointer call must happen in an unsafe synchronous helper —
    // C# disallows unsafe blocks inside async methods on net8.0. Args are a
    // ref struct so they can't cross an await boundary anyway.
    private static unsafe MqttConnectReasonCode InvokeConnectionValidator(
        MqttServerOptions options,
        ref MqttConnectPacket connectPacket,
        MqttChannelAdapter channelAdapter)
    {
        var validator = options.ConnectionValidator;
        if (validator == null)
        {
            return MqttConnectReasonCode.Success;
        }

        var args = new MqttConnectionValidatorArgs
        {
            ClientId = connectPacket.ClientId.AsSpan(),
            Username = connectPacket.Username.AsSpan(),
            Password = connectPacket.Password.AsSpan(),
            AuthenticationMethod = connectPacket.AuthenticationMethod.AsSpan(),
            AuthenticationData = connectPacket.AuthenticationData.AsSpan(),
            IsSecureConnection = channelAdapter.IsSecureConnection,
            ProtocolVersion = channelAdapter.PacketFormatterAdapter.ProtocolVersion,
            RemoteEndPoint = channelAdapter.RemoteEndPoint,
            ClientCertificate = channelAdapter.ClientCertificate,
            CleanSession = connectPacket.CleanSession,
        };
        return validator(in args);
    }

    private static unsafe void InvokeClientConnected(MqttServerOptions options, MqttConnectedClient connectedClient)
    {
        var hook = options.ClientConnectedInterceptor;
        if (hook == null)
        {
            return;
        }

        var args = new MqttClientConnectedArgs
        {
            ClientId = connectedClient.Id,
            UserName = connectedClient.UserName,
            IsSecureConnection = connectedClient.ChannelAdapter.IsSecureConnection,
            RemoteEndPoint = connectedClient.RemoteEndPoint,
        };
        hook(in args);
    }

    private static unsafe void InvokeClientDisconnected(
        MqttServerOptions options,
        MqttConnectedClient connectedClient,
        MqttClientDisconnectType disconnectType)
    {
        var hook = options.ClientDisconnectedInterceptor;
        if (hook == null)
        {
            return;
        }

        var args = new MqttClientDisconnectedArgs
        {
            ClientId = connectedClient.Id,
            UserName = connectedClient.UserName,
            IsSecureConnection = connectedClient.ChannelAdapter.IsSecureConnection,
            RemoteEndPoint = connectedClient.RemoteEndPoint,
            DisconnectType = disconnectType,
        };
        hook(in args);
    }
}