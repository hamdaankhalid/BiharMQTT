// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server;

/// <summary>
///     Delegate for the buffered publish interceptor.  The event args are passed by
///     <c>ref</c> to avoid heap-allocating the struct on every inbound message.
///     <para>
///         <b>Callers must not store or escape the ref</b> — the payload memory points
///         into ring buffer storage that is recycled after the callback returns.
///     </para>
/// </summary>
public delegate void BufferedPublishHandler(ref InterceptingPublishBufferedEventArgs args);

/// <summary>
///     Event args for the buffered publish interceptor.  Passed by <c>ref</c> on every
///     inbound PUBLISH — zero heap allocations per message.
///     <para>
///         The <see cref="Payload" /> is a <see cref="ReadOnlyMemory{T}" /> view into ring
///         buffer memory and is only valid for the duration of the callback.
///     </para>
/// </summary>
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix — kept for API consistency
public struct InterceptingPublishBufferedEventArgs
#pragma warning restore CA1711
{
    public InterceptingPublishBufferedEventArgs(
        string topic,
        ReadOnlyMemory<byte> payload,
        MqttQualityOfServiceLevel qualityOfServiceLevel,
        bool retain,
        string clientId,
        CancellationToken cancellationToken)
    {
        Topic = topic;
        Payload = payload;
        QualityOfServiceLevel = qualityOfServiceLevel;
        Retain = retain;
        ClientId = clientId;
        CancellationToken = cancellationToken;
        ProcessPublish = true;
    }

    /// <summary>
    ///     The message topic.
    /// </summary>
    public string Topic { get; set; }

    /// <summary>
    ///     The message payload as a direct view into ring buffer memory.
    ///     <remarks>
    ///         This memory is only valid during the event callback.  Do NOT store
    ///         this reference.  Call <c>.ToArray()</c> if you need a persistent copy.
    ///     </remarks>
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; }

    public MqttQualityOfServiceLevel QualityOfServiceLevel { get; set; }

    public bool Retain { get; set; }

    public string ClientId { get; }

    public CancellationToken CancellationToken { get; }

    public bool CloseConnection { get; set; }

    public bool ProcessPublish { get; set; }

    public MqttPubAckReasonCode ReasonCode { get; set; }

    public string ReasonString { get; set; }

    public List<MqttUserProperty> UserProperties { get; set; }
}
