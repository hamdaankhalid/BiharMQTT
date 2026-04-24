// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server.Internal.Formatter;

public static class MqttPubRecPacketFactory
{
    static ArraySegment<byte> ToSegment(string s) => s == null ? default : new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(s));

    public static MqttPubRecPacket Create(MqttPublishPacket publishPacket, DispatchApplicationMessageResult dispatchApplicationMessageResult)
    {
        var pubRecPacket = new MqttPubRecPacket
        {
            PacketIdentifier = publishPacket.PacketIdentifier,
            ReasonCode = (MqttPubRecReasonCode)dispatchApplicationMessageResult.ReasonCode,
            ReasonString = ToSegment(dispatchApplicationMessageResult.ReasonString),
            UserProperties = dispatchApplicationMessageResult.UserProperties
        };

        return pubRecPacket;
    }
}