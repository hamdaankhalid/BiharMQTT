// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using BiharMQTT.Formatter;

namespace BiharMQTT.Server;

/// <summary>
/// Arguments passed to a <see cref="MqttServerOptions.ConnectionValidator"/>
/// hook. All <see cref="ReadOnlySpan{T}"/> fields point at the inbound CONNECT
/// packet's pooled body buffer and are <strong>borrowed</strong> for the
/// duration of the call only. Copy via <c>.ToArray()</c> if you need to keep
/// anything past the return.
/// <para>
/// The <c>readonly ref struct</c> shape blocks capture in lambdas, fields,
/// async/iterator locals, boxing, and generic type arguments — the lifetime
/// contract is enforced by the type system, not by documentation.
/// </para>
/// </summary>
public readonly ref struct MqttConnectionValidatorArgs
{
    /// <summary>Client ID, raw UTF-8 bytes. Borrowed; do not retain.</summary>
    public ReadOnlySpan<byte> ClientId { get; init; }

    /// <summary>Username, raw UTF-8 bytes. Empty if the client did not send one.</summary>
    public ReadOnlySpan<byte> Username { get; init; }

    /// <summary>Password, raw bytes. Empty if the client did not send one.</summary>
    public ReadOnlySpan<byte> Password { get; init; }

    /// <summary>MQTT v5 enhanced-auth method name, raw UTF-8 bytes.</summary>
    public ReadOnlySpan<byte> AuthenticationMethod { get; init; }

    /// <summary>MQTT v5 enhanced-auth data, raw bytes.</summary>
    public ReadOnlySpan<byte> AuthenticationData { get; init; }

    /// <summary>True when the underlying socket is wrapped in TLS (8883 path).</summary>
    public bool IsSecureConnection { get; init; }

    /// <summary>Negotiated MQTT protocol version.</summary>
    public MqttProtocolVersion ProtocolVersion { get; init; }

    /// <summary>Remote endpoint of the connecting socket. Reference-typed.</summary>
    public EndPoint RemoteEndPoint { get; init; }

    /// <summary>
    /// Client certificate presented during TLS handshake (mTLS path), or
    /// <c>null</c> on plain-TCP and TLS-without-client-cert connections.
    /// </summary>
    public X509Certificate2 ClientCertificate { get; init; }

    /// <summary>True when the client requested a clean session (no resumed state).</summary>
    public bool CleanSession { get; init; }
}
