// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server.Internal.Formatter;

public static class MqttPubRelPacketFactory
{
    public static MqttPubRelPacket Create(MqttPubRecPacket pubRecPacket, MqttPubRelReasonCode reasonCode)
    {
        return new MqttPubRelPacket
        {
            PacketIdentifier = pubRecPacket.PacketIdentifier,
            ReasonCode = reasonCode
        };
    }
}