// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server.Internal;

/// <summary>
///     Minimal replacement for the deleted event args class.
///     Used internally by the subscription manager to carry interceptor results.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix")]
public sealed class InterceptingSubscriptionEventArgs
{
    public InterceptingSubscriptionEventArgs(
        string clientId,
        string userName,
        MqttSessionStatus sessionStatus,
        MqttTopicFilter topicFilter,
        List<MqttUserProperty> userProperties,
        CancellationToken cancellationToken)
    {
        ClientId = clientId;
        UserName = userName;
        SessionStatus = sessionStatus;
        TopicFilter = topicFilter;
        UserProperties = userProperties;
        CancellationToken = cancellationToken;
    }

    public string ClientId { get; }

    public string UserName { get; }

    public MqttSessionStatus SessionStatus { get; }

    public MqttTopicFilter TopicFilter { get; set; }

    public List<MqttUserProperty> UserProperties { get; set; }

    public CancellationToken CancellationToken { get; }

    public bool CloseConnection { get; set; }

    public bool ProcessSubscription { get; set; } = true;

    public string ReasonString { get; set; }

    public SubscribeResponse Response { get; } = new();

    public sealed class SubscribeResponse
    {
        public MqttSubscribeReasonCode ReasonCode { get; set; }
    }
}
