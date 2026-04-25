// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using BiharMQTT.Internal;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server;

public static class MqttServerExtensions
{
    public static Task DisconnectClientAsync(this MqttServer server, string id, MqttDisconnectReasonCode reasonCode = MqttDisconnectReasonCode.NormalDisconnection)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.DisconnectClientAsync(id, new MqttServerClientDisconnectOptions { ReasonCode = reasonCode });
    }

    /// <summary>
    ///     Convenience extension that injects an application message using the low-allocation
    ///     <see cref="MqttBufferedApplicationMessage" /> path with a <see cref="ReadOnlyMemory{T}" /> payload.
    /// </summary>
    public static Task InjectApplicationMessage(
        this MqttServer server,
        string topic,
        ReadOnlyMemory<byte> payload,
        MqttQualityOfServiceLevel qualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(topic);

        return server.InjectApplicationMessage(
            new MqttBufferedApplicationMessage
            {
                Topic = topic,
                Payload = payload,
                QualityOfServiceLevel = qualityOfServiceLevel,
                Retain = retain
            });
    }

    public static Task StopAsync(this MqttServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.StopAsync(new MqttServerStopOptions());
    }
}