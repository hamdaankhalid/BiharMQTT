// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Protocol;

namespace BiharMQTT.Server;

public static class MqttServerExtensions
{
    public static Task DisconnectClientAsync(this MqttServer server, string id, MqttDisconnectReasonCode reasonCode = MqttDisconnectReasonCode.NormalDisconnection)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.DisconnectClientAsync(id, new MqttServerClientDisconnectOptions { ReasonCode = reasonCode });
    }

    public static Task StopAsync(this MqttServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.StopAsync(new MqttServerStopOptions());
    }
}