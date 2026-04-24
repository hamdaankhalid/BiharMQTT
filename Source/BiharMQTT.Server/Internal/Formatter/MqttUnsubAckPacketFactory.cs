// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Packets;

namespace BiharMQTT.Server.Internal.Formatter;

public static class MqttUnsubAckPacketFactory
{
    public static MqttUnsubAckPacket Create(MqttUnsubscribePacket unsubscribePacket, UnsubscribeResult unsubscribeResult)
    {
        ArgumentNullException.ThrowIfNull(unsubscribeResult);

        var unsubAckPacket = new MqttUnsubAckPacket
        {
            PacketIdentifier = unsubscribePacket.PacketIdentifier
        };

        // MQTTv5.0.0 only.
        unsubAckPacket.ReasonCodes = unsubscribeResult.ReasonCodes;

        return unsubAckPacket;
    }
}