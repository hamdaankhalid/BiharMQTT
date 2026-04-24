// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using BiharMQTT.Protocol;

namespace BiharMQTT.Packets;

public struct MqttTopicFilter
{
    // Bit layout: NoLocal[0], RetainAsPublished[1], QoS[2-3], RetainHandling[4-5]
    byte _flags;

    public ArraySegment<byte> Topic { get; set; }

    public bool NoLocal
    {
        readonly get => (_flags & 0x01) != 0;
        set => _flags = (byte)(value ? (_flags | 0x01) : (_flags & ~0x01));
    }

    public bool RetainAsPublished
    {
        readonly get => (_flags & 0x02) != 0;
        set => _flags = (byte)(value ? (_flags | 0x02) : (_flags & ~0x02));
    }

    public MqttQualityOfServiceLevel QualityOfServiceLevel
    {
        readonly get => (MqttQualityOfServiceLevel)((_flags >> 2) & 0x03);
        set => _flags = (byte)((_flags & ~0x0C) | (((int)value & 0x03) << 2));
    }

    public MqttRetainHandling RetainHandling
    {
        readonly get => (MqttRetainHandling)((_flags >> 4) & 0x03);
        set => _flags = (byte)((_flags & ~0x30) | (((int)value & 0x03) << 4));
    }
}