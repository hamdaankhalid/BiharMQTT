// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using BiharMQTT.Exceptions;
using BiharMQTT.Packets;

namespace BiharMQTT.Server.Internal.Formatter;

public static class MqttPublishPacketFactory
{
    public static MqttPublishPacket Create(MqttConnectPacket connectPacket)
    {
        ArgumentNullException.ThrowIfNull(connectPacket);

        if (!connectPacket.WillFlag)
        {
            throw new MqttProtocolViolationException("The CONNECT packet contains no will message (WillFlag).");
        }

        ArraySegment<byte> willMessageBuffer = default;
        if (connectPacket.WillMessage?.Length > 0)
        {
            willMessageBuffer = new ArraySegment<byte>(connectPacket.WillMessage);
        }

        var packet = new MqttPublishPacket
        {
            Topic = connectPacket.WillTopic,
            PayloadSegment = willMessageBuffer,
            QualityOfServiceLevel = connectPacket.WillQoS,
            Retain = connectPacket.WillRetain,
            ContentType = connectPacket.WillContentType,
            CorrelationData = connectPacket.WillCorrelationData,
            MessageExpiryInterval = connectPacket.WillMessageExpiryInterval,
            PayloadFormatIndicator = connectPacket.WillPayloadFormatIndicator,
            ResponseTopic = connectPacket.WillResponseTopic,
            UserProperties = connectPacket.WillUserProperties
        };

        return packet;
    }

    public static MqttPublishPacket Create(MqttApplicationMessage applicationMessage)
    {
        ArgumentNullException.ThrowIfNull(applicationMessage);

        // Copy all values to their matching counterparts.
        // The not supported values in MQTT 3.1.1 are not serialized (excluded) later.
        var packet = new MqttPublishPacket
        {
            Topic = applicationMessage.Topic,
            Payload = applicationMessage.Payload,
            QualityOfServiceLevel = applicationMessage.QualityOfServiceLevel,
            Retain = applicationMessage.Retain,
            Dup = applicationMessage.Dup,
            ContentType = applicationMessage.ContentType,
            CorrelationData = applicationMessage.CorrelationData,
            MessageExpiryInterval = applicationMessage.MessageExpiryInterval,
            PayloadFormatIndicator = applicationMessage.PayloadFormatIndicator,
            ResponseTopic = applicationMessage.ResponseTopic,
            TopicAlias = applicationMessage.TopicAlias,
            SubscriptionIdentifiers = applicationMessage.SubscriptionIdentifiers,
            UserProperties = applicationMessage.UserProperties
        };

        return packet;
    }

    /// <summary>
    ///     Creates a publish packet from a buffered message using a pre-snapshotted payload.
    ///     The caller must supply a <paramref name="payloadSnapshot" /> that outlives any
    ///     enqueued packets (i.e. a copied byte[] rather than the caller's pooled buffer).
    /// </summary>
    public static MqttPublishPacket Create(MqttBufferedApplicationMessage message, ReadOnlySequence<byte> payloadSnapshot)
    {
        var packet = new MqttPublishPacket
        {
            Topic = message.Topic,
            Payload = payloadSnapshot,
            QualityOfServiceLevel = message.QualityOfServiceLevel,
            Retain = message.Retain,
            ContentType = message.ContentType,
            CorrelationData = message.CorrelationData,
            MessageExpiryInterval = message.MessageExpiryInterval,
            PayloadFormatIndicator = message.PayloadFormatIndicator,
            ResponseTopic = message.ResponseTopic,
            SubscriptionIdentifiers = message.SubscriptionIdentifiers,
            UserProperties = message.UserProperties
        };

        return packet;
    }

    public static MqttPublishPacket Create(MqttRetainedMessageMatch retainedMessage)
    {
        ArgumentNullException.ThrowIfNull(retainedMessage);

        var publishPacket = Create(retainedMessage.ApplicationMessage);
        publishPacket.QualityOfServiceLevel = retainedMessage.SubscriptionQualityOfServiceLevel;
        return publishPacket;
    }
}