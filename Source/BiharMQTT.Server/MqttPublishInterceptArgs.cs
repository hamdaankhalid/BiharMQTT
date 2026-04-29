// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server;

/// <summary>
/// Arguments passed to a <see cref="MqttServerOptions.PublishInterceptor"/>
/// hook. <see cref="Topic"/> and <see cref="Payload"/> are <strong>borrowed</strong>
/// for the duration of the call only — the broker reuses the underlying
/// memory for the next packet as soon as the hook returns. Copy via
/// <c>.ToArray()</c> if you need to keep anything.
/// <para>
/// The <c>readonly ref struct</c> shape blocks capture in lambdas, fields,
/// async/iterator locals, boxing, and generic type arguments — the lifetime
/// contract is enforced by the type system, not by documentation.
/// </para>
/// </summary>
public readonly ref struct MqttPublishInterceptArgs
{
    /// <summary>
    /// Client ID of the publisher. Empty string when the message was
    /// injected via <c>MqttServer.Publish</c>.
    /// </summary>
    public string SenderClientId { get; init; }

    /// <summary>Topic name, raw UTF-8 bytes. Borrowed; do not retain.</summary>
    public ReadOnlySpan<byte> Topic { get; init; }

    /// <summary>Payload bytes (possibly multi-segment). Borrowed; do not retain.</summary>
    public ReadOnlySequence<byte> Payload { get; init; }

    public MqttQualityOfServiceLevel QualityOfServiceLevel { get; init; }

    public bool Retain { get; init; }
}
