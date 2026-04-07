// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Protocol;

namespace MQTTnet.Server;

/// <summary>
///     Event args for the <c>InterceptingPublishAsync</c> event when using the ring
///     buffer message store.  The payload is exposed as <see cref="ReadOnlyMemory{T}" />
///     pointing directly into ring buffer memory.
///     <para>
///         <b>IMPORTANT</b>: The <see cref="Payload" /> memory is only valid for the
///         duration of the event callback.  If you need to keep the payload data beyond
///         the callback, you must copy it (e.g. <c>Payload.ToArray()</c>).
///         Storing the <see cref="ReadOnlyMemory{T}" /> in a field and reading it later
///         will produce undefined results because the ring buffer may have reused the
///         underlying memory for a different message.
///     </para>
/// </summary>
public sealed class InterceptingPublishBufferedEventArgs : EventArgs
{
    public InterceptingPublishBufferedEventArgs(
        string topic,
        ReadOnlyMemory<byte> payload,
        MqttQualityOfServiceLevel qualityOfServiceLevel,
        bool retain,
        string clientId,
        string userName,
        IDictionary sessionItems,
        CancellationToken cancellationToken)
    {
        Topic = topic ?? throw new ArgumentNullException(nameof(topic));
        Payload = payload;
        QualityOfServiceLevel = qualityOfServiceLevel;
        Retain = retain;
        ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        UserName = userName;
        SessionItems = sessionItems ?? throw new ArgumentNullException(nameof(sessionItems));
        CancellationToken = cancellationToken;
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

    public string UserName { get; }

    public CancellationToken CancellationToken { get; }

    public bool CloseConnection { get; set; }

    public bool ProcessPublish { get; set; } = true;

    public PublishResponse Response { get; } = new();

    public IDictionary SessionItems { get; }
}
