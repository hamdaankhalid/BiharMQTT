// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using BiharMQTT.Protocol;

namespace BiharMQTT.Packets;

public struct MqttPublishPacket
{
    // Bit layout: Dup[0], Retain[1], PayloadFmtInd[2], QoS[3-4]
    byte _flags;

    public ushort PacketIdentifier { get; set; }
    public ArraySegment<byte> ContentType { get; set; }
    public ArraySegment<byte> CorrelationData { get; set; }
    public uint MessageExpiryInterval { get; set; }
    public ArraySegment<byte> PayloadSegment { set => Payload = new ReadOnlySequence<byte>(value); }
    public ReadOnlySequence<byte> Payload { get; set; }
    public ArraySegment<byte> ResponseTopic { get; set; }
    public List<uint> SubscriptionIdentifiers { get; set; }
    public ArraySegment<byte> Topic { get; set; }
    public ushort TopicAlias { get; set; }
    public List<MqttUserProperty> UserProperties { get; set; }

    public bool Dup
    {
        readonly get => (_flags & 0x01) != 0;
        set => _flags = (byte)(value ? (_flags | 0x01) : (_flags & ~0x01));
    }

    public bool Retain
    {
        readonly get => (_flags & 0x02) != 0;
        set => _flags = (byte)(value ? (_flags | 0x02) : (_flags & ~0x02));
    }

    public MqttPayloadFormatIndicator PayloadFormatIndicator
    {
        readonly get => (MqttPayloadFormatIndicator)((_flags >> 2) & 0x01);
        set => _flags = (byte)((_flags & ~0x04) | (((int)value & 0x01) << 2));
    }

    public MqttQualityOfServiceLevel QualityOfServiceLevel
    {
        readonly get => (MqttQualityOfServiceLevel)((_flags >> 3) & 0x03);
        set => _flags = (byte)((_flags & ~0x18) | (((int)value & 0x03) << 3));
    }
}