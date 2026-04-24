// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
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

namespace BiharMQTT.Server.Internal;

public sealed class MqttClientSessionsManager : ISubscriptionChangedNotification, IDisposable
{
    readonly Dictionary<string, MqttConnectedClient> _clients = new(4096);
    readonly AsyncLock _createConnectionSyncRoot = new();
    readonly MqttNetSourceLogger _logger;
    readonly MqttServerOptions _options;
    readonly MqttRetainedMessagesManager _retainedMessagesManager;
    readonly MessageRingBuffer _ringBuffer;
    readonly IMqttNetLogger _rootLogger;
    readonly ReaderWriterLockSlim _sessionsManagementLock = new();

    // The _sessions dictionary contains all session, the _subscriberSessions hash set contains subscriber sessions only.
    // See the MqttSubscription object for a detailed explanation.
    readonly MqttSessionsStorage _sessionsStorage = new();
    readonly HashSet<MqttSession> _subscriberSessions = [];

    // Reusable snapshot list for dispatch — avoids per-publish List allocation.
    readonly List<MqttSession> _subscriberSessionsSnapshot = [];

    public MqttClientSessionsManager(
        MqttServerOptions options,
        MqttRetainedMessagesManager retainedMessagesManager,
        IMqttNetLogger logger,
        MessageRingBuffer ringBuffer)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger.WithSource(nameof(MqttClientSessionsManager));
        _rootLogger = logger;

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _retainedMessagesManager = retainedMessagesManager ?? throw new ArgumentNullException(nameof(retainedMessagesManager));
        _ringBuffer = ringBuffer ?? throw new ArgumentNullException(nameof(ringBuffer));
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

    static ArraySegment<byte> ToSegment(string s) => s == null ? default : new ArraySegment<byte>(Encoding.UTF8.GetBytes(s));
    static string SegmentToString(ArraySegment<byte> seg) => seg.Count == 0 ? string.Empty : Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count);

    public Task<DispatchApplicationMessageResult> DispatchPublishPacketDirect(
        string senderId,
        MqttPublishPacket publishPacket,
        CancellationToken cancellationToken)
    {
        return DispatchViaRingBuffer(
            senderId,
            SegmentToString(publishPacket.Topic),
            publishPacket.Payload,
            publishPacket.QualityOfServiceLevel,
            publishPacket.Retain,
            SegmentToString(publishPacket.ContentType),
            SegmentToString(publishPacket.ResponseTopic),
            publishPacket.CorrelationData.Count == 0 ? null : publishPacket.CorrelationData.ToArray(),
            publishPacket.MessageExpiryInterval,
            publishPacket.PayloadFormatIndicator,
            publishPacket.TopicAlias,
            publishPacket.SubscriptionIdentifiers,
            publishPacket.UserProperties,
            cancellationToken);
    }

    public Task<DispatchApplicationMessageResult> DispatchApplicationMessage(
        string senderId,
        string senderUserName,
        IDictionary senderSessionItems,
        MqttApplicationMessage applicationMessage,
        CancellationToken cancellationToken)
    {
        return DispatchViaRingBuffer(
            senderId,
            applicationMessage.Topic,
            applicationMessage.Payload,
            applicationMessage.QualityOfServiceLevel,
            applicationMessage.Retain,
            applicationMessage.ContentType,
            applicationMessage.ResponseTopic,
            applicationMessage.CorrelationData,
            applicationMessage.MessageExpiryInterval,
            applicationMessage.PayloadFormatIndicator,
            applicationMessage.TopicAlias,
            applicationMessage.SubscriptionIdentifiers,
            applicationMessage.UserProperties,
            cancellationToken);
    }

    /// <summary>
    ///     Dispatches a buffered application message with reduced heap allocations.
    ///     When no event interceptors are registered and the message is not retained,
    ///     this path avoids allocating <see cref="MqttApplicationMessage" /> entirely.
    ///     If interceptors or retained-message handling is needed, it falls back to
    ///     materializing an <see cref="MqttApplicationMessage" /> and using the standard path.
    /// </summary>
    public Task<DispatchApplicationMessageResult> DispatchApplicationMessage(
        string senderId,
        string senderUserName,
        IDictionary senderSessionItems,
        MqttBufferedApplicationMessage message,
        CancellationToken cancellationToken)
    {
        // ── Ring buffer path ──
        return DispatchViaRingBuffer(
            senderId,
            message.Topic,
            message.Payload.Length > 0
                ? new ReadOnlySequence<byte>(message.Payload)
                : ReadOnlySequence<byte>.Empty,
            message.QualityOfServiceLevel,
            message.Retain,
            message.ContentType,
            message.ResponseTopic,
            message.CorrelationData,
            message.MessageExpiryInterval,
            message.PayloadFormatIndicator,
            0,
            message.SubscriptionIdentifiers,
            message.UserProperties,
            cancellationToken);
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

            var reason = ValidateConnection(connectPacket, channelAdapter);

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
                AssignedClientIdentifier = connectPacket.ClientId,
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
                await channelAdapter.SendPacketAsync(failBuffer, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Pass connAckPacket so that IsSessionPresent flag can be set if the client session already exists.
            connectedClient = await CreateClientConnection(connectPacket, connAckPacket, channelAdapter).ConfigureAwait(false);

            var connAckEncoder = channelAdapter.PacketFormatterAdapter.Encoder;
            var connAckBuffer = connAckEncoder.Encode(ref connAckPacket);
            await connectedClient.SendPacketAsync(connAckBuffer, cancellationToken).ConfigureAwait(false);

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

        var subscribeResult = await clientSession.Subscribe(fakeSubscribePacket, CancellationToken.None).ConfigureAwait(false);

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

        return GetClientSession(clientId).Unsubscribe(fakeUnsubscribePacket, CancellationToken.None);
    }

    async Task<MqttConnectedClient> CreateClientConnection(
        MqttConnectPacket connectPacket,
        MqttConnAckPacket connAckPacket,
        MqttChannelAdapter channelAdapter)
    {
        MqttConnectedClient connectedClient;
        var clientId = SegmentToString(connectPacket.ClientId);

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

                    connectedClient = new MqttConnectedClient(connectPacket, channelAdapter, session, _options, this, _rootLogger);
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
        return new MqttSession(connectPacket, sessionItems, _options, _retainedMessagesManager, this);
    }

    /// <summary>
    ///     Ring-buffer-backed dispatch: acquires a slot, memcopies the payload once,
    ///     and fans out to subscribers using packets that reference ring buffer memory.
    ///     The slot is ref-counted — each subscriber holds a ref that is released
    ///     when the packet bus item terminates.
    /// </summary>
    async Task<DispatchApplicationMessageResult> DispatchViaRingBuffer(
        string senderId,
        string topic,
        ReadOnlySequence<byte> payload,
        MqttQualityOfServiceLevel qos,
        bool retain,
        string contentType,
        string responseTopic,
        byte[] correlationData,
        uint messageExpiryInterval,
        MqttPayloadFormatIndicator payloadFormatIndicator,
        ushort topicAlias,
        List<uint> subscriptionIdentifiers,
        List<MqttUserProperty> userProperties,
        CancellationToken cancellationToken)
    {
        // Acquire a ring buffer slot and memcopy the payload in.
        var slot = MessageSlot.Empty;
        ReadOnlySequence<byte> ringPayload = default;

        var payloadLength = (int)payload.Length;
        if (payloadLength > 0)
        {
            var (acquired, slotMemory) = await _ringBuffer.Acquire(payloadLength, cancellationToken).ConfigureAwait(false);
            slot = acquired;
            payload.CopyTo(slotMemory.Span);
            ringPayload = new ReadOnlySequence<byte>(_ringBuffer.GetPayload(slot));
        }

        var processPublish = true;
        var closeConnection = false;
        string reasonString = null;
        List<MqttUserProperty> interceptorUserProperties = null;
        var reasonCode = 0;

        if (!processPublish)
        {
            if (slot.IsValid)
            {
                _ringBuffer.Release(slot);
            }

            return new DispatchApplicationMessageResult(reasonCode, closeConnection, reasonString, interceptorUserProperties);
        }

        var matchingSubscribersCount = 0;

        try
        {
            List<MqttSession> subscriberSessions;
            _sessionsManagementLock.EnterReadLock();
            try
            {
                _subscriberSessionsSnapshot.Clear();
                _subscriberSessionsSnapshot.AddRange(_subscriberSessions);
                subscriberSessions = _subscriberSessionsSnapshot;
            }
            finally
            {
                _sessionsManagementLock.ExitReadLock();
            }

            MqttTopicHash.Calculate(topic, out var topicHash, out _, out _);

            foreach (var session in subscriberSessions)
            {
                if (!session.TryCheckSubscriptions(topic, topicHash, qos, senderId, out var checkSubscriptionsResult))
                {
                    continue;
                }

                if (!checkSubscriptionsResult.IsSubscribed)
                {
                    continue;
                }

                var publishPacketCopy = new MqttPublishPacket
                {
                    Topic = ToSegment(topic),
                    Payload = ringPayload,
                    QualityOfServiceLevel = checkSubscriptionsResult.QualityOfServiceLevel,
                    SubscriptionIdentifiers = checkSubscriptionsResult.SubscriptionIdentifiers,
                    Retain = checkSubscriptionsResult.RetainAsPublished && retain,
                    ContentType = ToSegment(contentType),
                    ResponseTopic = ToSegment(responseTopic),
                    CorrelationData = correlationData == null ? default : new ArraySegment<byte>(correlationData),
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

                if (slot.IsValid)
                {
                    _ringBuffer.AddRef(slot);
                    var capturedSlot = slot;
                    session.EnqueuePublishPacket(ref publishPacketCopy, busItem =>
                    {
                        busItem.OnTerminated = MessageRingBuffer.ReleaseCallback;
                        busItem.TerminationState = (_ringBuffer, capturedSlot);
                    });
                }
                else
                {
                    session.EnqueuePublishPacket(ref publishPacketCopy);
                }

                _logger.Verbose("Client '{0}': Queued PUBLISH packet with topic '{1}'", session.Id, topic);
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
        finally
        {
            // Drop the sentinel reference from Acquire.
            if (slot.IsValid)
            {
                _ringBuffer.Release(slot);
            }
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

    private static MqttConnectReasonCode ValidateConnection(MqttConnectPacket connectPacket, MqttChannelAdapter channelAdapter)
    {
        // Check the client ID and set a random one if supported.
        if (connectPacket.ClientId.Count == 0 && channelAdapter.PacketFormatterAdapter.ProtocolVersion == MqttProtocolVersion.V500)
        {
            connectPacket.ClientId = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes("TODO_GENERATE_RANDOM_CLIENT_ID"));
        }

        if (connectPacket.ClientId.Count == 0)
        {
            return MqttConnectReasonCode.ClientIdentifierNotValid;
        }

        return MqttConnectReasonCode.Success;
    }
}