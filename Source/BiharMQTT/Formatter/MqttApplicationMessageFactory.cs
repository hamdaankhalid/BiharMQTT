// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using BiharMQTT.Packets;

namespace BiharMQTT.Formatter;

public static class MqttApplicationMessageFactory
{
    static string SegmentToString(ArraySegment<byte> segment)
    {
        if (segment.Count == 0)
            return null;
        return Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
    }

    public static MqttApplicationMessage Create(MqttPublishPacket publishPacket)
    {
        return new MqttApplicationMessage
        {
            Topic = SegmentToString(publishPacket.Topic),
            Payload = publishPacket.Payload,
            QualityOfServiceLevel = publishPacket.QualityOfServiceLevel,
            Retain = publishPacket.Retain,
            Dup = publishPacket.Dup,
            ResponseTopic = SegmentToString(publishPacket.ResponseTopic),
            ContentType = SegmentToString(publishPacket.ContentType),
            CorrelationData = publishPacket.CorrelationData.Count > 0
                ? publishPacket.CorrelationData.AsSpan().ToArray()
                : null,
            MessageExpiryInterval = publishPacket.MessageExpiryInterval,
            SubscriptionIdentifiers = publishPacket.SubscriptionIdentifiers,
            TopicAlias = publishPacket.TopicAlias,
            PayloadFormatIndicator = publishPacket.PayloadFormatIndicator,
            UserProperties = publishPacket.UserProperties
        };
    }
}