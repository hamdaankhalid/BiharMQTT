// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Packets;

namespace BiharMQTT.Server.Internal.Formatter;

public static class MqttSubAckPacketFactory
{
    static ArraySegment<byte> ToSegment(string s) => s == null ? default : new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(s));

    public static MqttSubAckPacket Create(MqttSubscribePacket subscribePacket, SubscribeResult subscribeResult)
    {
        ArgumentNullException.ThrowIfNull(subscribeResult);

        var subAckPacket = new MqttSubAckPacket
        {
            PacketIdentifier = subscribePacket.PacketIdentifier,
            ReasonCodes = subscribeResult.ReasonCodes,
            ReasonString = ToSegment(subscribeResult.ReasonString),
            UserProperties = subscribeResult.UserProperties
        };

        return subAckPacket;
    }
}