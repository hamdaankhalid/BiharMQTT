// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Packets;

public struct MqttSubscribePacket
{
    public ushort PacketIdentifier { get; set; }
    public uint SubscriptionIdentifier { get; set; }
    public List<MqttTopicFilter> TopicFilters { get; set; }
    public List<MqttUserProperty> UserProperties { get; set; }
}