// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace BiharMQTT.Server;

/// <summary>
/// Arguments passed to a <see cref="MqttServerOptions.ClientConnectedInterceptor"/>
/// hook, fired after a successful CONNACK has been sent and the session is
/// installed in the broker's client table.
/// </summary>
public readonly ref struct MqttClientConnectedArgs
{
    /// <summary>Client ID assigned to this connection.</summary>
    public string ClientId { get; init; }

    /// <summary>Username from the CONNECT packet, or empty.</summary>
    public string UserName { get; init; }

    /// <summary>True when the underlying socket is wrapped in TLS (8883 path).</summary>
    public bool IsSecureConnection { get; init; }

    /// <summary>Remote endpoint of the connected socket.</summary>
    public EndPoint RemoteEndPoint { get; init; }
}
