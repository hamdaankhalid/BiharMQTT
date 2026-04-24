// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Protocol;

namespace BiharMQTT.Packets;

public struct MqttSubAckPacket
{
    public ushort PacketIdentifier { get; set; }
    public List<MqttSubscribeReasonCode> ReasonCodes { get; set; }
    public ArraySegment<byte> ReasonString { get; set; }
    public List<MqttUserProperty> UserProperties { get; set; }
}