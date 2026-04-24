// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server.Internal.Formatter;

public static class MqttDisconnectPacketFactory
{
    static readonly MqttDisconnectPacket DefaultNormalDisconnection = new()
    {
        ReasonCode = MqttDisconnectReasonCode.NormalDisconnection,
        UserProperties = null,
        ReasonString = default,
        ServerReference = default,
        SessionExpiryInterval = 0
    };

    static ArraySegment<byte> ToSegment(string s) => s == null ? default : new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(s));

    public static MqttDisconnectPacket Create(MqttServerClientDisconnectOptions clientDisconnectOptions)
    {
        if (clientDisconnectOptions == null)
        {
            return DefaultNormalDisconnection;
        }

        return new MqttDisconnectPacket
        {
            ReasonCode = clientDisconnectOptions.ReasonCode,
            UserProperties = clientDisconnectOptions.UserProperties,
            ReasonString = ToSegment(clientDisconnectOptions.ReasonString),
            ServerReference = ToSegment(clientDisconnectOptions.ServerReference),
            SessionExpiryInterval = 0 // TODO: Not yet supported!
        };
    }
}