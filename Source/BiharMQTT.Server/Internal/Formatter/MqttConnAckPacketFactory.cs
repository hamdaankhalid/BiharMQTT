// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Formatter;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server.Internal.Formatter;

public static class MqttConnAckPacketFactory
{
    public static MqttConnAckPacket Create(
        MqttConnectReasonCode reasonCode,
        ArraySegment<byte> authenticationMethod = default,
        ArraySegment<byte> responseAuthenticationData = default,
        ArraySegment<byte> assignedClientIdentifier = default,
        ArraySegment<byte> reasonString = default,
        ArraySegment<byte> serverReference = default,
        List<MqttUserProperty> responseUserProperties = null)
    {
        var connAckPacket = new MqttConnAckPacket
        {
            ReasonCode = reasonCode,
            RetainAvailable = true,
            SubscriptionIdentifiersAvailable = true,
            SharedSubscriptionAvailable = false,
            TopicAliasMaximum = ushort.MaxValue,
            MaximumQoS = MqttQualityOfServiceLevel.ExactlyOnce,
            WildcardSubscriptionAvailable = true,

            AuthenticationMethod = authenticationMethod,
            AuthenticationData = responseAuthenticationData,
            AssignedClientIdentifier = assignedClientIdentifier,
            ReasonString = reasonString,
            ServerReference = serverReference,
            UserProperties = responseUserProperties,

            ResponseInformation = default,
            MaximumPacketSize = 0, // Unlimited,
            ReceiveMaximum = 0 // Unlimited
        };

        return connAckPacket;
    }
}