// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Protocol;

namespace BiharMQTT.Packets;

public struct MqttConnAckPacket
{
    // Bit layout: IsSessionPresent[0], RetainAvailable[1], SharedSubAvail[2],
    //             SubIdAvail[3], WildcardSubAvail[4], MaxQoS[5-6]
    byte _flags;

    public ArraySegment<byte> AssignedClientIdentifier { get; set; }
    public ArraySegment<byte> AuthenticationData { get; set; }
    public ArraySegment<byte> AuthenticationMethod { get; set; }
    public uint MaximumPacketSize { get; set; }
    public MqttConnectReasonCode ReasonCode { get; set; }
    public ArraySegment<byte> ReasonString { get; set; }
    public ushort ReceiveMaximum { get; set; }
    public ArraySegment<byte> ResponseInformation { get; set; }
    public ushort ServerKeepAlive { get; set; }
    public ArraySegment<byte> ServerReference { get; set; }
    public uint SessionExpiryInterval { get; set; }
    public ushort TopicAliasMaximum { get; set; }
    public List<MqttUserProperty> UserProperties { get; set; }

    public bool IsSessionPresent
    {
        readonly get => (_flags & 0x01) != 0;
        set => _flags = (byte)(value ? (_flags | 0x01) : (_flags & ~0x01));
    }

    public bool RetainAvailable
    {
        readonly get => (_flags & 0x02) != 0;
        set => _flags = (byte)(value ? (_flags | 0x02) : (_flags & ~0x02));
    }

    public bool SharedSubscriptionAvailable
    {
        readonly get => (_flags & 0x04) != 0;
        set => _flags = (byte)(value ? (_flags | 0x04) : (_flags & ~0x04));
    }

    public bool SubscriptionIdentifiersAvailable
    {
        readonly get => (_flags & 0x08) != 0;
        set => _flags = (byte)(value ? (_flags | 0x08) : (_flags & ~0x08));
    }

    public bool WildcardSubscriptionAvailable
    {
        readonly get => (_flags & 0x10) != 0;
        set => _flags = (byte)(value ? (_flags | 0x10) : (_flags & ~0x10));
    }

    public MqttQualityOfServiceLevel MaximumQoS
    {
        readonly get => (MqttQualityOfServiceLevel)((_flags >> 5) & 0x03);
        set => _flags = (byte)((_flags & ~0x60) | (((int)value & 0x03) << 5));
    }
}