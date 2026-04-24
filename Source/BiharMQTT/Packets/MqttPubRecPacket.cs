// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Protocol;

namespace BiharMQTT.Packets;

public struct MqttPubRecPacket
{
    public ushort PacketIdentifier { get; set; }

    /// <summary>
    ///     Added in MQTTv5.
    /// </summary>
    public MqttPubRecReasonCode ReasonCode { get; set; }

    /// <summary>
    ///     Added in MQTTv5.
    /// </summary>
    public string ReasonString { get; set; }

    /// <summary>
    ///     Added in MQTTv5.
    /// </summary>
    public List<MqttUserProperty> UserProperties { get; set; }

    public override readonly string ToString()
    {
        return $"PubRec: [PacketIdentifier={PacketIdentifier}] [ReasonCode={ReasonCode}]";
    }
}