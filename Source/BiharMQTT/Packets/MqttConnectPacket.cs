// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Protocol;

namespace BiharMQTT.Packets;

public struct MqttConnectPacket
{
    // Bit layout: CleanSession[0], ReqProbInfo[1], ReqRespInfo[2], WillFlag[3],
    //             WillRetain[4], TryPrivate[5], WillQoS[6-7], WillPayloadFmtInd[8]
    ushort _flags;

    public ArraySegment<byte> AuthenticationData { get; set; }
    public ArraySegment<byte> AuthenticationMethod { get; set; }
    public ArraySegment<byte> ClientId { get; set; }
    public ArraySegment<byte> WillCorrelationData { get; set; }
    public ushort KeepAlivePeriod { get; set; }
    public uint MaximumPacketSize { get; set; }
    public ArraySegment<byte> Password { get; set; }
    public ushort ReceiveMaximum { get; set; }
    public ArraySegment<byte> WillResponseTopic { get; set; }
    public uint SessionExpiryInterval { get; set; }
    public ushort TopicAliasMaximum { get; set; }
    public ArraySegment<byte> Username { get; set; }
    public List<MqttUserProperty> UserProperties { get; set; }
    public ArraySegment<byte> WillContentType { get; set; }
    public uint WillDelayInterval { get; set; }
    public ArraySegment<byte> WillMessage { get; set; }
    public uint WillMessageExpiryInterval { get; set; }
    public ArraySegment<byte> WillTopic { get; set; }
    public List<MqttUserProperty> WillUserProperties { get; set; }

    public bool CleanSession
    {
        readonly get => (_flags & 0x01) != 0;
        set => _flags = (ushort)(value ? (_flags | 0x01) : (_flags & ~0x01));
    }

    public bool RequestProblemInformation
    {
        readonly get => (_flags & 0x02) != 0;
        set => _flags = (ushort)(value ? (_flags | 0x02) : (_flags & ~0x02));
    }

    public bool RequestResponseInformation
    {
        readonly get => (_flags & 0x04) != 0;
        set => _flags = (ushort)(value ? (_flags | 0x04) : (_flags & ~0x04));
    }

    public bool WillFlag
    {
        readonly get => (_flags & 0x08) != 0;
        set => _flags = (ushort)(value ? (_flags | 0x08) : (_flags & ~0x08));
    }

    public bool WillRetain
    {
        readonly get => (_flags & 0x10) != 0;
        set => _flags = (ushort)(value ? (_flags | 0x10) : (_flags & ~0x10));
    }

    public bool TryPrivate
    {
        readonly get => (_flags & 0x20) != 0;
        set => _flags = (ushort)(value ? (_flags | 0x20) : (_flags & ~0x20));
    }

    public MqttQualityOfServiceLevel WillQoS
    {
        readonly get => (MqttQualityOfServiceLevel)((_flags >> 6) & 0x03);
        set => _flags = (ushort)((_flags & ~0xC0) | (((int)value & 0x03) << 6));
    }

    public MqttPayloadFormatIndicator WillPayloadFormatIndicator
    {
        readonly get => (MqttPayloadFormatIndicator)((_flags >> 8) & 0x01);
        set => _flags = (ushort)((_flags & ~0x100) | (((int)value & 0x01) << 8));
    }
}