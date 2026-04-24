// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Text;
using BiharMQTT.Exceptions;
using BiharMQTT.Packets;

namespace BiharMQTT.Server.Internal.Formatter;

public static class MqttPublishPacketFactory
{
    static ArraySegment<byte> ToSegment(string value)
    {
        if (string.IsNullOrEmpty(value))
            return default;
        return new ArraySegment<byte>(Encoding.UTF8.GetBytes(value));
    }

    static ArraySegment<byte> ToSegment(byte[] value)
    {
        if (value == null || value.Length == 0)
            return default;
        return new ArraySegment<byte>(value);
    }

    public static MqttPublishPacket Create(MqttConnectPacket connectPacket)
    {
        if (!connectPacket.WillFlag)
        {
            throw new MqttProtocolViolationException("The CONNECT packet contains no will message (WillFlag).");
        }

        var packet = new MqttPublishPacket
        {
            Topic = connectPacket.WillTopic,
            PayloadSegment = connectPacket.WillMessage,
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

        var packet = new MqttPublishPacket
        {
            Topic = ToSegment(applicationMessage.Topic),
            Payload = applicationMessage.Payload,
            QualityOfServiceLevel = applicationMessage.QualityOfServiceLevel,
            Retain = applicationMessage.Retain,
            Dup = applicationMessage.Dup,
            ContentType = ToSegment(applicationMessage.ContentType),
            CorrelationData = ToSegment(applicationMessage.CorrelationData),
            MessageExpiryInterval = applicationMessage.MessageExpiryInterval,
            PayloadFormatIndicator = applicationMessage.PayloadFormatIndicator,
            ResponseTopic = ToSegment(applicationMessage.ResponseTopic),
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
            Topic = ToSegment(message.Topic),
            Payload = payloadSnapshot,
            QualityOfServiceLevel = message.QualityOfServiceLevel,
            Retain = message.Retain,
            ContentType = ToSegment(message.ContentType),
            CorrelationData = ToSegment(message.CorrelationData),
            MessageExpiryInterval = message.MessageExpiryInterval,
            PayloadFormatIndicator = message.PayloadFormatIndicator,
            ResponseTopic = ToSegment(message.ResponseTopic),
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