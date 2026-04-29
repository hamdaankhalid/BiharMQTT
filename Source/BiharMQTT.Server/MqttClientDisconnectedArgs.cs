// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace BiharMQTT.Server;

/// <summary>
/// Arguments passed to a <see cref="MqttServerOptions.ClientDisconnectedInterceptor"/>
/// hook, fired once the per-connection lifetime task ends — peer close, server
/// disconnect, takeover, or error path. Always pairs 1:1 with a prior
/// <see cref="MqttClientConnectedArgs"/> firing.
/// </summary>
public readonly ref struct MqttClientDisconnectedArgs
{
    /// <summary>Client ID assigned to this connection.</summary>
    public string ClientId { get; init; }

    /// <summary>Username from the CONNECT packet, or empty.</summary>
    public string UserName { get; init; }

    /// <summary>True when the underlying socket was wrapped in TLS (8883 path).</summary>
    public bool IsSecureConnection { get; init; }

    /// <summary>Remote endpoint that was connected.</summary>
    public EndPoint RemoteEndPoint { get; init; }

    /// <summary>How the connection ended — clean, abrupt, or takeover.</summary>
    public MqttClientDisconnectType DisconnectType { get; init; }
}
