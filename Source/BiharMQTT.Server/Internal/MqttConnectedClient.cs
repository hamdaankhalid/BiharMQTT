// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Net;
using BiharMQTT.Adapter;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Exceptions;
using BiharMQTT.Formatter;
using BiharMQTT.Internal;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;
using BiharMQTT.Server.Internal.Formatter;
using MqttDisconnectPacketFactory = BiharMQTT.Server.Internal.Formatter.MqttDisconnectPacketFactory;
using MqttPubAckPacketFactory = BiharMQTT.Server.Internal.Formatter.MqttPubAckPacketFactory;
using MqttPubCompPacketFactory = BiharMQTT.Server.Internal.Formatter.MqttPubCompPacketFactory;
using MqttPublishPacketFactory = BiharMQTT.Server.Internal.Formatter.MqttPublishPacketFactory;
using MqttPubRecPacketFactory = BiharMQTT.Server.Internal.Formatter.MqttPubRecPacketFactory;
using MqttPubRelPacketFactory = BiharMQTT.Server.Internal.Formatter.MqttPubRelPacketFactory;
using static BiharMQTT.Internal.MqttSegmentHelper;

namespace BiharMQTT.Server.Internal;

/*
    Represents a connected MQTT client on the server side. The receive path is
    callback-driven: BeginReceiveNext arms the channel adapter's SAEA-backed
    state machine, OnPacketReceived processes the result synchronously and
    re-arms. The lifecycle Task returned by RunAsync resolves when the receive
    chain terminates (peer close, disconnect, takeover, error).
*/
public sealed class MqttConnectedClient : IDisposable
{
    readonly MqttNetSourceLogger _logger;
    readonly MqttSenderPool _senderPool;
    readonly Action _scheduleSendCallback;
    // Cached delegate so re-arming the receive chain costs no allocation.
    readonly ReceivePacketCompletionHandler _onPacketReceivedCallback;
    readonly MqttServerOptions _serverOptions;
    readonly MqttClientSessionsManager _sessionsManager;
    readonly Dictionary<ushort, byte[]> _topicAlias = new();

    CancellationTokenSource _cancellationToken = new();
    bool _disconnectPacketSent;

    // Resolved when the receive chain terminates. RunAsync returns its Task.
    TaskCompletionSource _runCompletion;

    // 0 = not in the sender pool's ready queue; 1 = enqueued, owner of the
    // next drain. Producers (session enqueue) flip 0→1 to claim a scheduling
    // slot; the worker flips 1→0 before draining so concurrent producers can
    // re-claim. Caches a delegate so per-enqueue notifies don't allocate.
    int _isScheduledForSend;

    public MqttConnectedClient(
        MqttConnectPacket connectPacket,
        MqttChannelAdapter channelAdapter,
        MqttSession session,
        MqttServerOptions serverOptions,
        MqttClientSessionsManager sessionsManager,
        MqttSenderPool senderPool,
        IMqttNetLogger logger)
    {
        _serverOptions = serverOptions ?? throw new ArgumentNullException(nameof(serverOptions));
        _sessionsManager = sessionsManager ?? throw new ArgumentNullException(nameof(sessionsManager));
        _senderPool = senderPool ?? throw new ArgumentNullException(nameof(senderPool));
        ConnectPacket = connectPacket;
        Id = SegmentToString(connectPacket.ClientId);
        UserName = SegmentToString(connectPacket.Username);

        ChannelAdapter = channelAdapter ?? throw new ArgumentNullException(nameof(channelAdapter));
        RemoteEndPoint = channelAdapter.RemoteEndPoint;
        Session = session ?? throw new ArgumentNullException(nameof(session));

        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger.WithSource(nameof(MqttConnectedClient));
        _scheduleSendCallback = ScheduleSend;
        _onPacketReceivedCallback = OnPacketReceived;
    }

    public MqttChannelAdapter ChannelAdapter { get; }

    public MqttConnectPacket ConnectPacket { get; }

    public MqttDisconnectPacket? DisconnectPacket { get; private set; }

    public string Id { get; init; }

    public bool IsRunning { get; private set; }

    public bool IsTakenOver { get; set; }

    public EndPoint RemoteEndPoint { get; }

    public MqttSession Session { get; }

    public MqttClientStatistics Statistics { get; } = new();

    public string UserName { get; init; }

    public void Dispose()
    {
        _cancellationToken?.Dispose();
    }

    public void ResetStatistics()
    {
        ChannelAdapter.ResetStatistics();
        Statistics.ResetStatistics();
    }

    public Task RunAsync()
    {
        _logger.Info("Client '{0}': Session started", Id);

        Session.LatestConnectPacket = ConnectPacket;
        Session.WillMessageSent = false;

        _runCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsRunning = true;

        // Attach to the sender pool. After this, every Enqueue* on the session
        // schedules this client onto the pool's ready queue.
        Session.SetReadyNotifier(_scheduleSendCallback);

        // Wire the inline-send fast path: enqueues that find the bus empty and
        // the channel send-lock uncontended will write directly to the socket
        // and bypass the bus entirely.
        Session.SetAttachedAdapter(ChannelAdapter);

        // A recovered persistent session may already have unacknowledged QoS>0
        // packets re-enqueued before any new producer enqueue arrives — kick
        // the pool once so they get drained without waiting on a fresh enqueue.
        if (Session.HasPendingPackets)
        {
            ScheduleSend();
        }

        // Start the callback-driven receive chain.
        BeginReceiveNext();

        return _runCompletion.Task;
    }

    public void SendPacket(Memory<byte> fullPacketBuffer, CancellationToken cancellationToken) =>
        ChannelAdapter.SendPacket(fullPacketBuffer, cancellationToken);

    public void SendPacket(Memory<byte> header, ReadOnlySequence<byte> payload, CancellationToken cancellationToken) =>
        ChannelAdapter.SendPacket(header, payload, cancellationToken);

    public Task StopAsync(MqttServerClientDisconnectOptions disconnectOptions)
    {
        IsRunning = false;

        if (!_disconnectPacketSent)
        {
            // Sending DISCONNECT packets from the server to the client is only supported when using MQTTv5+.
            if (ChannelAdapter.PacketFormatterAdapter.ProtocolVersion == MqttProtocolVersion.V500)
            {
                // From RFC: The Client or Server MAY send a DISCONNECT packet before closing the Network Connection.
                // This library does not sent a DISCONNECT packet for a normal disconnection.
                if (disconnectOptions != null)
                {
                    if (disconnectOptions.ReasonCode != MqttDisconnectReasonCode.NormalDisconnection || disconnectOptions.UserProperties?.Count > 0 ||
                        !string.IsNullOrEmpty(disconnectOptions.ReasonString) || !string.IsNullOrEmpty(disconnectOptions.ServerReference))
                    {
                        // Send DISCONNECT BEFORE cancelling the token: cancel disposes the channel
                        // and the DISCONNECT bytes would never arrive at the client.
                        TrySendDisconnectPacket(disconnectOptions);
                    }
                }
            }
        }

        StopInternal();
        return Task.CompletedTask;
    }

    void HandleIncomingPingReqPacket()
    {
        // See: The Server MUST send a PINGRESP packet in response to a PINGREQ packet [MQTT-3.12.4-1].
        var encoder = ChannelAdapter.PacketFormatterAdapter.Encoder;
        MqttPacketBuffer buffer = encoder.EncodePingResp();
        Session.EnqueueHealthPacket(ref buffer);
    }

    void HandleIncomingPubAckPacket(MqttPubAckPacket pubAckPacket)
    {
        Session.AcknowledgePublishPacket(pubAckPacket.PacketIdentifier);
    }

    void HandleIncomingPubCompPacket(MqttPubCompPacket pubCompPacket)
    {
        Session.AcknowledgePublishPacket(pubCompPacket.PacketIdentifier);
    }

    // Returns true if the caller should continue receiving, false if the connection
    // was stopped (don't re-arm).
    bool HandleIncomingPublishPacket(MqttPublishPacket publishPacket)
    {
        HandleTopicAlias(publishPacket);

        // Dispatch directly from the MqttPublishPacket — no MqttApplicationMessage
        // round-trip. Retained-message storage is handled outside the dispatch path.
        DispatchApplicationMessageResult dispatchApplicationMessageResult =
            _sessionsManager.DispatchPublishPacketDirect(Id, publishPacket);

        if (dispatchApplicationMessageResult.CloseConnection)
        {
            StopAsync(new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.UnspecifiedError });
            return false;
        }

        switch (publishPacket.QualityOfServiceLevel)
        {
            case MqttQualityOfServiceLevel.AtMostOnce:
                // Do nothing since QoS 0 has no ACK at all!
                break;
            case MqttQualityOfServiceLevel.AtLeastOnce:
                {
                    MqttPubAckPacket pubAckPacket = MqttPubAckPacketFactory.Create(publishPacket, dispatchApplicationMessageResult);
                    MqttPacketBuffer buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref pubAckPacket);
                    Session.EnqueueControlPacket(ref buffer);
                    break;
                }
            case MqttQualityOfServiceLevel.ExactlyOnce:
                {
                    MqttPubRecPacket pubRecPacket = MqttPubRecPacketFactory.Create(publishPacket, dispatchApplicationMessageResult);
                    MqttPacketBuffer buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref pubRecPacket);
                    Session.EnqueueControlPacket(ref buffer);
                    break;
                }
            default:
                throw new MqttCommunicationException("Received a not supported QoS level");
        }

        return true;
    }

    void HandleIncomingPubRecPacket(MqttPubRecPacket pubRecPacket)
    {
        var pubRelPacket = MqttPubRelPacketFactory.Create(pubRecPacket, MqttPubRelReasonCode.Success);
        MqttPacketBuffer buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref pubRelPacket);
        Session.EnqueueControlPacket(ref buffer);
    }

    void HandleIncomingPubRelPacket(MqttPubRelPacket pubRelPacket)
    {
        var pubCompPacket = MqttPubCompPacketFactory.Create(pubRelPacket, MqttPubCompReasonCode.Success);
        MqttPacketBuffer buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref pubCompPacket);
        Session.EnqueueControlPacket(ref buffer);
    }

    void HandleIncomingSubscribePacket(ref MqttSubscribePacket subscribePacket)
    {
        var subscribeResult = Session.Subscribe(ref subscribePacket);

        var subAckPacket = MqttSubAckPacketFactory.Create(ref subscribePacket, subscribeResult);
        MqttPacketBuffer buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref subAckPacket);
        Session.EnqueueControlPacket(ref buffer);

        if (subscribeResult.CloseConnection)
        {
            StopInternal();
            return;
        }

        if (subscribeResult.RetainedMessages == null)
        {
            return;
        }

        foreach (var retainedMessageMatch in subscribeResult.RetainedMessages)
        {
            var publishPacket = MqttPublishPacketFactory.Create(retainedMessageMatch);
            MqttPacketBuffer retainedBuffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref publishPacket);
            Session.EnqueueDataPacket(ref retainedBuffer);
        }
    }

    void HandleIncomingUnsubscribePacket(MqttUnsubscribePacket unsubscribePacket, CancellationToken cancellationToken)
    {
        var unsubscribeResult = Session.Unsubscribe(unsubscribePacket, cancellationToken);

        var unsubAckPacket = MqttUnsubAckPacketFactory.Create(unsubscribePacket, unsubscribeResult);
        MqttPacketBuffer buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref unsubAckPacket);
        Session.EnqueueControlPacket(ref buffer);

        if (unsubscribeResult.CloseConnection)
        {
            StopInternal();
        }
    }

    void HandleTopicAlias(MqttPublishPacket publishPacket)
    {
        if (publishPacket.TopicAlias == 0)
        {
            return;
        }

        lock (_topicAlias)
        {
            if (publishPacket.Topic.Count > 0)
            {
                _topicAlias[publishPacket.TopicAlias] = publishPacket.Topic.ToArray();
            }
            else
            {
                if (_topicAlias.TryGetValue(publishPacket.TopicAlias, out var topic))
                {
                    publishPacket.Topic = new ArraySegment<byte>(topic);
                }
                else
                {
                    _logger.Warning("Client '{0}': Received invalid topic alias ({1})", Id, publishPacket.TopicAlias);
                }
            }
        }
    }

    void BeginReceiveNext()
    {
        var cts = _cancellationToken;
        if (cts == null || cts.IsCancellationRequested || IsTakenOver || !IsRunning)
        {
            FinishRun();
            return;
        }

        try
        {
            ChannelAdapter.BeginReceivePacket(_onPacketReceivedCallback);
        }
        catch (ObjectDisposedException)
        {
            FinishRun();
        }
        catch (Exception ex)
        {
            LogReceiveError(ex);
            FinishRun();
        }
    }

    void OnPacketReceived(ReceivedMqttPacket receivedPacket, Exception error)
    {
        if (error != null)
        {
            LogReceiveError(error);
            FinishRun();
            return;
        }

        if (receivedPacket.TotalLength == 0)
        {
            FinishRun();
            return;
        }

        var cts = _cancellationToken;
        if (cts == null || cts.IsCancellationRequested || IsTakenOver || !IsRunning)
        {
            FinishRun();
            return;
        }

        var cancellationToken = cts.Token;
        var decoder = ChannelAdapter.PacketFormatterAdapter.Decoder;
        Statistics.HandleReceivedPacket(receivedPacket.PacketType);

        bool keepReceiving;
        try
        {
            switch (receivedPacket.PacketType)
            {
                case MqttControlPacketType.Publish:
                    {
                        MqttPublishPacket publishPacket = decoder.DecodePublishPacket(receivedPacket.FixedHeader, receivedPacket.Body);
                        keepReceiving = HandleIncomingPublishPacket(publishPacket);
                        break;
                    }
                case MqttControlPacketType.PubAck:
                    {
                        var pubAckPacket = decoder.DecodePubAckPacket(receivedPacket.Body);
                        HandleIncomingPubAckPacket(pubAckPacket);
                        keepReceiving = true;
                        break;
                    }
                case MqttControlPacketType.PubComp:
                    {
                        var pubCompPacket = decoder.DecodePubCompPacket(receivedPacket.Body);
                        HandleIncomingPubCompPacket(pubCompPacket);
                        keepReceiving = true;
                        break;
                    }
                case MqttControlPacketType.PubRec:
                    {
                        var pubRecPacket = decoder.DecodePubRecPacket(receivedPacket.Body);
                        HandleIncomingPubRecPacket(pubRecPacket);
                        keepReceiving = true;
                        break;
                    }
                case MqttControlPacketType.PubRel:
                    {
                        var pubRelPacket = decoder.DecodePubRelPacket(receivedPacket.Body);
                        HandleIncomingPubRelPacket(pubRelPacket);
                        keepReceiving = true;
                        break;
                    }
                case MqttControlPacketType.Subscribe:
                    {
                        MqttSubscribePacket subscribePacket = decoder.DecodeSubscribePacket(receivedPacket.Body);
                        HandleIncomingSubscribePacket(ref subscribePacket);
                        keepReceiving = true;
                        break;
                    }
                case MqttControlPacketType.Unsubscribe:
                    {
                        var unsubscribePacket = decoder.DecodeUnsubscribePacket(receivedPacket.Body);
                        HandleIncomingUnsubscribePacket(unsubscribePacket, cancellationToken);
                        keepReceiving = true;
                        break;
                    }
                case MqttControlPacketType.PingReq:
                    {
                        HandleIncomingPingReqPacket();
                        keepReceiving = true;
                        break;
                    }
                case MqttControlPacketType.PingResp:
                    throw new MqttProtocolViolationException("A PINGRESP Packet is sent by the Server to the Client in response to a PINGREQ Packet only.");
                case MqttControlPacketType.Disconnect:
                    {
                        var disconnectPacket = decoder.DecodeDisconnectPacket(receivedPacket.Body);
                        DisconnectPacket = disconnectPacket;
                        FinishRun();
                        return;
                    }
                default:
                    throw new MqttProtocolViolationException("Packet not allowed");
            }
        }
        catch (OperationCanceledException)
        {
            FinishRun();
            return;
        }
        catch (Exception exception)
        {
            LogReceiveError(exception);
            FinishRun();
            return;
        }

        if (!keepReceiving)
        {
            FinishRun();
            return;
        }

        BeginReceiveNext();
    }

    void LogReceiveError(Exception exception)
    {
        if (exception is OperationCanceledException || exception is ObjectDisposedException)
        {
            return;
        }

        if (exception is MqttCommunicationException)
        {
            _logger.Warning(exception, "Client '{0}': Communication exception while receiving packets", Id);
            return;
        }

        var logLevel = MqttNetLogLevel.Error;
        if (!IsRunning)
        {
            logLevel = MqttNetLogLevel.Warning;
        }

        _logger.Publish(logLevel, exception, "Client '{0}': Error while receiving packets", Id);
    }

    // Idempotent: multiple paths into the receive chain can race to terminate
    // (peer close, server StopAsync, send-side failure). The TCS guards against
    // double-completion; the rest is naturally idempotent.
    void FinishRun()
    {
        var tcs = _runCompletion;
        if (tcs == null) return;
        if (tcs.Task.IsCompleted) return;

        IsRunning = false;

        // Detach so a late producer (cross-client dispatch in flight) cannot
        // re-schedule this dying client or write to a torn-down channel.
        Session.SetReadyNotifier(null);
        Session.SetAttachedAdapter(null);

        Session.DisconnectedTimestamp = DateTime.UtcNow;

        _cancellationToken?.TryCancel();
        _cancellationToken?.Dispose();
        _cancellationToken = null;

        var isCleanDisconnect = DisconnectPacket != null;

        if (!IsTakenOver && !isCleanDisconnect && Session.LatestConnectPacket.WillFlag && !Session.WillMessageSent)
        {
            try
            {
                var willPublishPacket = MqttPublishPacketFactory.Create(Session.LatestConnectPacket);
                _ = _sessionsManager.DispatchPublishPacketDirect(Id, willPublishPacket);
                Session.WillMessageSent = true;
                _logger.Info("Client '{0}': Published will message", Id);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Client '{0}': Will message dispatch failed", Id);
            }
        }

        _logger.Info("Client '{0}': Connection stopped", Id);
        tcs.TrySetResult();
    }

    // Called by the sender pool worker AFTER it has finished a drain, releasing
    // the scheduling slot. Critically the flag stays at 1 for the entire drain:
    // producers that arrive during the drain see flag=1 and skip their CAS, so
    // they don't re-enqueue this client. After the worker clears, those new
    // packets are picked up either by the worker's own re-claim check below or
    // by the next producer that wins the 0→1 CAS.
    public void ResetScheduledFlag() => Volatile.Write(ref _isScheduledForSend, 0);

    // Called by producers (session enqueue) and by the worker when it detects
    // pending packets after clearing the flag. Returns true if the caller now
    // owns the scheduling slot; false means another caller has already scheduled.
    public bool TrySetScheduledFlag() => Interlocked.CompareExchange(ref _isScheduledForSend, 1, 0) == 0;

    // Called by the sender pool when a send fails non-recoverably.
    public void RequestStop() => StopInternal();

    void ScheduleSend()
    {
        if (TrySetScheduledFlag())
        {
            _senderPool.Schedule(this);
        }
    }

    void StopInternal()
    {
        _cancellationToken?.TryCancel();
    }

    void TrySendDisconnectPacket(MqttServerClientDisconnectOptions options)
    {
        try
        {
            _disconnectPacketSent = true;
            var disconnectPacket = MqttDisconnectPacketFactory.Create(options);
            MqttPacketBuffer buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref disconnectPacket);
            using var timeout = new CancellationTokenSource(_serverOptions.DefaultCommunicationTimeout);
            SendPacket(buffer.Packet, buffer.Payload, timeout.Token);
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Client '{0}': Error while sending DISCONNECT packet (ReasonCode = {1})", Id, options.ReasonCode);
        }
    }
}
