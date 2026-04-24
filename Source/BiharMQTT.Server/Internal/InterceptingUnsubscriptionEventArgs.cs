// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server.Internal;

/// <summary>
///     Minimal replacement for the deleted event args class.
///     Used internally by the subscription manager to carry interceptor results.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix")]
public sealed class InterceptingUnsubscriptionEventArgs
{
    public InterceptingUnsubscriptionEventArgs(
        string clientId,
        string userName,
        IDictionary items,
        string topicFilter,
        List<MqttUserProperty> userProperties,
        CancellationToken cancellationToken)
    {
        ClientId = clientId;
        UserName = userName;
        Items = items;
        TopicFilter = topicFilter;
        UserProperties = userProperties;
        CancellationToken = cancellationToken;
    }

    public string ClientId { get; }

    public string UserName { get; }

    public IDictionary Items { get; }

    public string TopicFilter { get; }

    public List<MqttUserProperty> UserProperties { get; set; }

    public CancellationToken CancellationToken { get; }

    public bool CloseConnection { get; set; }

    public bool ProcessUnsubscription { get; set; } = true;

    public UnsubscribeResponse Response { get; } = new();

    public sealed class UnsubscribeResponse
    {
        public MqttUnsubscribeReasonCode ReasonCode { get; set; }
    }
}
