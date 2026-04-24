// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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

namespace BiharMQTT.Server.Internal;

public sealed class MqttConnectedClient : IDisposable
{
    readonly MqttNetSourceLogger _logger;
    readonly MqttServerOptions _serverOptions;
    readonly MqttClientSessionsManager _sessionsManager;
    readonly Dictionary<ushort, ArraySegment<byte>> _topicAlias = new();

    CancellationTokenSource _cancellationToken = new();
    bool _disconnectPacketSent;

    public MqttConnectedClient(
        MqttConnectPacket connectPacket,
        MqttChannelAdapter channelAdapter,
        MqttSession session,
        MqttServerOptions serverOptions,
        MqttClientSessionsManager sessionsManager,
        IMqttNetLogger logger)
    {
        _serverOptions = serverOptions ?? throw new ArgumentNullException(nameof(serverOptions));
        _sessionsManager = sessionsManager ?? throw new ArgumentNullException(nameof(sessionsManager));
        ConnectPacket = connectPacket;

        ChannelAdapter = channelAdapter ?? throw new ArgumentNullException(nameof(channelAdapter));
        RemoteEndPoint = channelAdapter.RemoteEndPoint;
        Session = session ?? throw new ArgumentNullException(nameof(session));

        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger.WithSource(nameof(MqttConnectedClient));
    }

    public MqttChannelAdapter ChannelAdapter { get; }

    public MqttConnectPacket ConnectPacket { get; }

    public MqttDisconnectPacket? DisconnectPacket { get; private set; }

    public string Id => ConnectPacket.ClientId;

    public bool IsRunning { get; private set; }

    public bool IsTakenOver { get; set; }

    public EndPoint RemoteEndPoint { get; }

    public MqttSession Session { get; }

    public MqttClientStatistics Statistics { get; } = new();

    public string UserName => ConnectPacket.Username;

    public void Dispose()
    {
        _cancellationToken?.Dispose();
    }

    public void ResetStatistics()
    {
        ChannelAdapter.ResetStatistics();
        Statistics.ResetStatistics();
    }

    public async Task RunAsync()
    {
        _logger.Info("Client '{0}': Session started", Id);

        Session.LatestConnectPacket = ConnectPacket;
        Session.WillMessageSent = false;

        try
        {
            var cancellationToken = _cancellationToken.Token;
            IsRunning = true;

            _ = Task.Factory.StartNew(() => SendPacketsLoop(cancellationToken), cancellationToken, TaskCreationOptions.PreferFairness, TaskScheduler.Default).ConfigureAwait(false);

            await ReceivePackagesLoop(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsRunning = false;

            Session.DisconnectedTimestamp = DateTime.UtcNow;

            _cancellationToken?.TryCancel();
            _cancellationToken?.Dispose();
            _cancellationToken = null;
        }

        var isCleanDisconnect = DisconnectPacket != null;

        if (!IsTakenOver && !isCleanDisconnect && Session.LatestConnectPacket.WillFlag && !Session.WillMessageSent)
        {
            var willPublishPacket = MqttPublishPacketFactory.Create(Session.LatestConnectPacket);
            var willApplicationMessage = MqttApplicationMessageFactory.Create(willPublishPacket);

            _ = _sessionsManager.DispatchApplicationMessage(Id, UserName, Session.Items, willApplicationMessage, CancellationToken.None);
            Session.WillMessageSent = true;

            _logger.Info("Client '{0}': Published will message", Id);
        }

        _logger.Info("Client '{0}': Connection stopped", Id);
    }

    public async Task SendPacketAsync(MqttPacketBuffer packetBuffer, CancellationToken cancellationToken)
    {
        await ChannelAdapter.SendPacketAsync(packetBuffer, cancellationToken).ConfigureAwait(false);
        Statistics.HandleSentPacket();
    }

    public async Task StopAsync(MqttServerClientDisconnectOptions disconnectOptions)
    {
        IsRunning = false;

        if (!_disconnectPacketSent)
        {
            // // Sending DISCONNECT packets from the server to the client is only supported when using MQTTv5+.
            if (ChannelAdapter.PacketFormatterAdapter.ProtocolVersion == MqttProtocolVersion.V500)
            {
                // From RFC: The Client or Server MAY send a DISCONNECT packet before closing the Network Connection.
                // This library does not sent a DISCONNECT packet for a normal disconnection.
                // TODO: Maybe adding a configuration option is requested in the future.
                if (disconnectOptions != null)
                {
                    if (disconnectOptions.ReasonCode != MqttDisconnectReasonCode.NormalDisconnection || disconnectOptions.UserProperties?.Count > 0 ||
                        !string.IsNullOrEmpty(disconnectOptions.ReasonString) || !string.IsNullOrEmpty(disconnectOptions.ServerReference))
                    {
                        // It is very important to send the DISCONNECT packet here BEFORE cancelling the
                        // token because the entire connection is closed (disposed) as soon as the cancellation
                        // token is cancelled. To there is no chance that the DISCONNECT packet will ever arrive
                        // at the client!
                        await TrySendDisconnectPacket(disconnectOptions).ConfigureAwait(false);
                    }
                }
            }
        }

        StopInternal();
    }

    void HandleIncomingPingReqPacket()
    {
        // See: The Server MUST send a PINGRESP packet in response to a PINGREQ packet [MQTT-3.12.4-1].
        var encoder = ChannelAdapter.PacketFormatterAdapter.Encoder;
        var buffer = encoder.EncodePingResp();
        Session.EnqueueHealthPacket(new MqttPacketBusItem(buffer));
    }

    void HandleIncomingPubAckPacket(MqttPubAckPacket pubAckPacket)
    {
        Session.AcknowledgePublishPacket(pubAckPacket.PacketIdentifier);
    }

    void HandleIncomingPubCompPacket(MqttPubCompPacket pubCompPacket)
    {
        Session.AcknowledgePublishPacket(pubCompPacket.PacketIdentifier);
    }

    async Task HandleIncomingPublishPacket(MqttPublishPacket publishPacket, CancellationToken cancellationToken)
    {
        HandleTopicAlias(publishPacket);

        DispatchApplicationMessageResult dispatchApplicationMessageResult;

        // When the ring buffer fast-path is available and the message is not
        // retained, dispatch directly from the MqttPublishPacket — this skips
        // the MqttApplicationMessage allocation entirely.
        if (!publishPacket.Retain && _sessionsManager.IsRingBufferFastPathAvailable)
        {
            dispatchApplicationMessageResult =
                await _sessionsManager.DispatchPublishPacketDirect(Id, publishPacket, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var applicationMessage = MqttApplicationMessageFactory.Create(publishPacket);
            dispatchApplicationMessageResult =
                await _sessionsManager.DispatchApplicationMessage(Id, UserName, Session.Items, applicationMessage, cancellationToken).ConfigureAwait(false);
        }

        if (dispatchApplicationMessageResult.CloseConnection)
        {
            await StopAsync(new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.UnspecifiedError });
            return;
        }

        switch (publishPacket.QualityOfServiceLevel)
        {
            case MqttQualityOfServiceLevel.AtMostOnce:
            {
                // Do nothing since QoS 0 has no ACK at all!
                break;
            }
            case MqttQualityOfServiceLevel.AtLeastOnce:
            {
                var pubAckPacket = MqttPubAckPacketFactory.Create(publishPacket, dispatchApplicationMessageResult);
                var buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref pubAckPacket);
                Session.EnqueueControlPacket(new MqttPacketBusItem(buffer));
                break;
            }
            case MqttQualityOfServiceLevel.ExactlyOnce:
            {
                var pubRecPacket = MqttPubRecPacketFactory.Create(publishPacket, dispatchApplicationMessageResult);
                var buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref pubRecPacket);
                Session.EnqueueControlPacket(new MqttPacketBusItem(buffer));
                break;
            }
            default:
            {
                throw new MqttCommunicationException("Received a not supported QoS level");
            }
        }
    }

    Task HandleIncomingPubRecPacket(MqttPubRecPacket pubRecPacket)
    {
        var pubRelPacket = MqttPubRelPacketFactory.Create(pubRecPacket, MqttPubRelReasonCode.Success);
        var buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref pubRelPacket);
        Session.EnqueueControlPacket(new MqttPacketBusItem(buffer));

        return CompletedTask.Instance;
    }

    void HandleIncomingPubRelPacket(MqttPubRelPacket pubRelPacket)
    {
        var pubCompPacket = MqttPubCompPacketFactory.Create(pubRelPacket, MqttPubCompReasonCode.Success);
        var buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref pubCompPacket);
        Session.EnqueueControlPacket(new MqttPacketBusItem(buffer));
    }

    async Task HandleIncomingSubscribePacket(MqttSubscribePacket subscribePacket, CancellationToken cancellationToken)
    {
        var subscribeResult = await Session.Subscribe(subscribePacket, cancellationToken).ConfigureAwait(false);

        var subAckPacket = MqttSubAckPacketFactory.Create(subscribePacket, subscribeResult);
        var buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref subAckPacket);
        Session.EnqueueControlPacket(new MqttPacketBusItem(buffer));

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
            var retainedBuffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref publishPacket);
            Session.EnqueueDataPacket(new MqttPacketBusItem(retainedBuffer));
        }
    }

    async Task HandleIncomingUnsubscribePacket(MqttUnsubscribePacket unsubscribePacket, CancellationToken cancellationToken)
    {
        var unsubscribeResult = await Session.Unsubscribe(unsubscribePacket, cancellationToken).ConfigureAwait(false);

        var unsubAckPacket = MqttUnsubAckPacketFactory.Create(unsubscribePacket, unsubscribeResult);
        var buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref unsubAckPacket);
        Session.EnqueueControlPacket(new MqttPacketBusItem(buffer));

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
                _topicAlias[publishPacket.TopicAlias] = publishPacket.Topic;
            }
            else
            {
                if (_topicAlias.TryGetValue(publishPacket.TopicAlias, out var topic))
                {
                    publishPacket.Topic = topic;
                }
                else
                {
                    _logger.Warning("Client '{0}': Received invalid topic alias ({1})", Id, publishPacket.TopicAlias);
                }
            }
        }
    }

    async Task ReceivePackagesLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Yield();

                var receivedPacket = await ChannelAdapter.ReceivePacketAsync(cancellationToken).ConfigureAwait(false);
                if (receivedPacket.TotalLength == 0)
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (IsTakenOver || !IsRunning)
                {
                    return;
                }

                var decoder = ChannelAdapter.PacketFormatterAdapter.Decoder;
                Statistics.HandleReceivedPacket(receivedPacket.PacketType);

                switch (receivedPacket.PacketType)
                {
                    case MqttControlPacketType.Publish:
                    {
                        var publishPacket = decoder.DecodePublishPacket(receivedPacket.FixedHeader, receivedPacket.Body);
                        await HandleIncomingPublishPacket(publishPacket, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case MqttControlPacketType.PubAck:
                    {
                        var pubAckPacket = decoder.DecodePubAckPacket(receivedPacket.Body);
                        HandleIncomingPubAckPacket(pubAckPacket);
                        break;
                    }
                    case MqttControlPacketType.PubComp:
                    {
                        var pubCompPacket = decoder.DecodePubCompPacket(receivedPacket.Body);
                        HandleIncomingPubCompPacket(pubCompPacket);
                        break;
                    }
                    case MqttControlPacketType.PubRec:
                    {
                        var pubRecPacket = decoder.DecodePubRecPacket(receivedPacket.Body);
                        await HandleIncomingPubRecPacket(pubRecPacket).ConfigureAwait(false);
                        break;
                    }
                    case MqttControlPacketType.PubRel:
                    {
                        var pubRelPacket = decoder.DecodePubRelPacket(receivedPacket.Body);
                        HandleIncomingPubRelPacket(pubRelPacket);
                        break;
                    }
                    case MqttControlPacketType.Subscribe:
                    {
                        var subscribePacket = decoder.DecodeSubscribePacket(receivedPacket.Body);
                        await HandleIncomingSubscribePacket(subscribePacket, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case MqttControlPacketType.Unsubscribe:
                    {
                        var unsubscribePacket = decoder.DecodeUnsubscribePacket(receivedPacket.Body);
                        await HandleIncomingUnsubscribePacket(unsubscribePacket, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case MqttControlPacketType.PingReq:
                    {
                        HandleIncomingPingReqPacket();
                        break;
                    }
                    case MqttControlPacketType.PingResp:
                    {
                        throw new MqttProtocolViolationException("A PINGRESP Packet is sent by the Server to the Client in response to a PINGREQ Packet only.");
                    }
                    case MqttControlPacketType.Disconnect:
                    {
                        var disconnectPacket = decoder.DecodeDisconnectPacket(receivedPacket.Body);
                        DisconnectPacket = disconnectPacket;
                        return;
                    }
                    default:
                    {
                        throw new MqttProtocolViolationException("Packet not allowed");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
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
    }

    async Task SendPacketsLoop(CancellationToken cancellationToken)
    {
        MqttPacketBusItem packetBusItem = default;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !IsTakenOver && IsRunning)
            {
                packetBusItem = await Session.DequeuePacketAsync(cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (IsTakenOver || !IsRunning)
                {
                    return;
                }

                try
                {
                    await SendPacketAsync(packetBusItem.PacketBuffer, cancellationToken).ConfigureAwait(false);
                    packetBusItem.Complete();
                }
                catch (OperationCanceledException)
                {
                    packetBusItem.Cancel();
                }
                catch (Exception exception)
                {
                    packetBusItem.Fail(exception);
                }
                finally
                {
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (exception is MqttCommunicationTimedOutException)
            {
                _logger.Warning(exception, "Client '{0}': Sending PUBLISH packet failed due to timeout", Id);
            }
            else if (exception is MqttCommunicationException)
            {
                _logger.Warning(exception, "Client '{0}': Sending PUBLISH packet failed due to communication exception", Id);
            }
            else
            {
                _logger.Error(exception, "Client '{0}': Sending PUBLISH packet failed", Id);
            }

            StopInternal();
        }
    }

    void StopInternal()
    {
        _cancellationToken?.TryCancel();
    }

    async Task TrySendDisconnectPacket(MqttServerClientDisconnectOptions options)
    {
        try
        {
            _disconnectPacketSent = true;

            var disconnectPacket = MqttDisconnectPacketFactory.Create(options);
            var buffer = ChannelAdapter.PacketFormatterAdapter.Encoder.Encode(ref disconnectPacket);

            using var timeout = new CancellationTokenSource(_serverOptions.DefaultCommunicationTimeout);
            await SendPacketAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Client '{0}': Error while sending DISCONNECT packet (ReasonCode = {1})", Id, options.ReasonCode);
        }
    }
}