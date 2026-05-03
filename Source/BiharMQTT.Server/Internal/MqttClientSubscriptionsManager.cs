// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;
using static BiharMQTT.Internal.MqttSegmentHelper;

namespace BiharMQTT.Server.Internal;

public sealed class MqttClientSubscriptionsManager : IDisposable
{
    static readonly List<uint> EmptySubscriptionIdentifiers = new List<uint>();

    readonly Dictionary<ulong, HashSet<MqttSubscription>> _noWildcardSubscriptionsByTopicHash = new Dictionary<ulong, HashSet<MqttSubscription>>();
    readonly MqttRetainedMessagesManager _retainedMessagesManager;

    readonly MqttSession _session;

    // Callback to maintain list of subscriber clients
    readonly ISubscriptionChangedNotification _subscriptionChangedNotification;

    // Subscriptions are stored in various dictionaries and use a "topic hash"; see the MqttSubscription object for a detailed explanation.
    // The additional lock is important to coordinate complex update logic with multiple steps, checks and interceptors.
    readonly Dictionary<string, MqttSubscription> _subscriptions = new Dictionary<string, MqttSubscription>();

    // Use subscription lock to maintain consistency across subscriptions and topic hash dictionaries
    readonly ReaderWriterLockSlim _subscriptionsLock = new ReaderWriterLockSlim();
    readonly Dictionary<ulong, TopicHashMaskSubscriptions> _wildcardSubscriptionsByTopicHash = new Dictionary<ulong, TopicHashMaskSubscriptions>();

    public MqttClientSubscriptionsManager(
        MqttSession session,
        MqttRetainedMessagesManager retainedMessagesManager,
        ISubscriptionChangedNotification subscriptionChangedNotification)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _retainedMessagesManager = retainedMessagesManager ?? throw new ArgumentNullException(nameof(retainedMessagesManager));
        _subscriptionChangedNotification = subscriptionChangedNotification;
    }

    [ThreadStatic] static List<MqttSubscription> _scratchPossibleSubscriptions;
    [ThreadStatic] static List<uint> _scratchSubscriptionIdentifiers;

    public CheckSubscriptionsResult CheckSubscriptions(Span<byte> topic, ulong topicHash, MqttQualityOfServiceLevel qualityOfServiceLevel, string senderId)
    {
        var possibleSubscriptions = _scratchPossibleSubscriptions ??= new List<MqttSubscription>();
        possibleSubscriptions.Clear();

        // Check for possible subscriptions. They might have collisions but this is fine.
        _subscriptionsLock.EnterReadLock();
        try
        {
            if (_noWildcardSubscriptionsByTopicHash.TryGetValue(topicHash, out HashSet<MqttSubscription> noWildcardSubscriptions))
            {
                possibleSubscriptions.AddRange(noWildcardSubscriptions);
            }

            foreach (KeyValuePair<ulong, TopicHashMaskSubscriptions> wcs in _wildcardSubscriptionsByTopicHash)
            {
                ulong subscriptionHash = wcs.Key;
                Dictionary<ulong, HashSet<MqttSubscription>> subscriptionsByHashMask = wcs.Value.SubscriptionsByHashMask;
                foreach (var shm in subscriptionsByHashMask)
                {
                    var subscriptionHashMask = shm.Key;
                    if ((topicHash & subscriptionHashMask) == subscriptionHash)
                    {
                        var subscriptions = shm.Value;
                        possibleSubscriptions.AddRange(subscriptions);
                    }
                }
            }
        }
        finally
        {
            _subscriptionsLock.ExitReadLock();
        }

        // The pre check has evaluated that nothing is subscribed.
        // If there were some possible candidates they get checked below
        // again to avoid collisions.
        if (possibleSubscriptions.Count == 0)
        {
            return default;
        }

        var senderIsReceiver = string.Equals(senderId, _session.Id, StringComparison.Ordinal);
        var maxQoSLevel = -1; // Not subscribed.

        var scratchIds = _scratchSubscriptionIdentifiers ??= new List<uint>();
        scratchIds.Clear();
        bool retainAsPublished = false;

        foreach (var subscription in possibleSubscriptions)
        {
            if (subscription.NoLocal && senderIsReceiver)
            {
                // This is a MQTTv5 feature!
                continue;
            }

            if (MqttTopicFilterComparer.Compare(topic, subscription.Topic) != MqttTopicFilterCompareResult.IsMatch)
            {
                continue;
            }

            if (subscription.RetainAsPublished)
            {
                // This is a MQTTv5 feature!
                retainAsPublished = true;
            }

            if ((int)subscription.GrantedQualityOfServiceLevel > maxQoSLevel)
            {
                maxQoSLevel = (int)subscription.GrantedQualityOfServiceLevel;
            }

            if (subscription.Identifier > 0)
            {
                if (!scratchIds.Contains(subscription.Identifier))
                {
                    scratchIds.Add(subscription.Identifier);
                }
            }
        }

        if (maxQoSLevel == -1)
        {
            return default;
        }

        var result = new CheckSubscriptionsResult
        {
            IsSubscribed = true,
            RetainAsPublished = retainAsPublished,
            // Reuse the thread-static list directly — consumed during encode before
            // the next CheckSubscriptions call on this thread.
            SubscriptionIdentifiers = scratchIds.Count > 0 ? scratchIds : EmptySubscriptionIdentifiers,

            // Start with the same QoS as the publisher.
            QualityOfServiceLevel = qualityOfServiceLevel
        };

        // Now downgrade if required.
        //
        // If a subscribing Client has been granted maximum QoS 1 for a particular Topic Filter, then a QoS 0 Application Message matching the filter is delivered
        // to the Client at QoS 0. This means that at most one copy of the message is received by the Client. On the other hand, a QoS 2 Message published to
        // the same topic is downgraded by the Server to QoS 1 for delivery to the Client, so that Client might receive duplicate copies of the Message.

        // Subscribing to a Topic Filter at QoS 2 is equivalent to saying "I would like to receive Messages matching this filter at the QoS with which they were published".
        // This means a publisher is responsible for determining the maximum QoS a Message can be delivered at, but a subscriber is able to require that the Server
        // downgrades the QoS to one more suitable for its usage.
        if (maxQoSLevel < (int)qualityOfServiceLevel)
        {
            result.QualityOfServiceLevel = (MqttQualityOfServiceLevel)maxQoSLevel;
        }

        return result;
    }

    public void Dispose()
    {
        _subscriptionsLock?.Dispose();
    }

    public SubscribeResult Subscribe(ref MqttSubscribePacket subscribePacket)
    {

        var retainedApplicationMessages = _retainedMessagesManager.GetMessages();
        var result = new SubscribeResult(subscribePacket.TopicFilters.Count);

        var addedSubscriptions = new List<string>();
        var finalTopicFilters = new List<MqttTopicFilter>();

        // The topic filters are order by its QoS so that the higher QoS will win over a
        // lower one.
        foreach (var topicFilterItem in subscribePacket.TopicFilters.OrderByDescending(f => f.QualityOfServiceLevel))
        {
            var interceptorEventArgs = InterceptSubscribe(topicFilterItem, subscribePacket.UserProperties);
            var topicFilter = interceptorEventArgs.TopicFilter;
            var processSubscription = interceptorEventArgs.ProcessSubscription && interceptorEventArgs.Response.ReasonCode <= MqttSubscribeReasonCode.GrantedQoS2;

            result.UserProperties = interceptorEventArgs.UserProperties;
            result.ReasonString = interceptorEventArgs.ReasonString;
            result.ReasonCodes.Add(interceptorEventArgs.Response.ReasonCode);

            if (interceptorEventArgs.CloseConnection)
            {
                // When any of the interceptor calls leads to a connection close the connection
                // must be closed. So do not revert to false!
                result.CloseConnection = true;
            }

            if (!processSubscription || topicFilter.Topic.Count == 0)
            {
                continue;
            }

            string topicString = SegmentToString(topicFilter.Topic);
            var createSubscriptionResult = CreateSubscription(topicFilter, topicString, subscribePacket.SubscriptionIdentifier, interceptorEventArgs.Response.ReasonCode);

            addedSubscriptions.Add(topicString);
            finalTopicFilters.Add(topicFilter);

            FilterRetainedApplicationMessages(retainedApplicationMessages, createSubscriptionResult, result);
        }

        // This call will add the new subscription to the internal storage.
        // So the event _ClientSubscribedTopicEvent_ must be called afterwards.
        _subscriptionChangedNotification?.OnSubscriptionsAdded(_session, addedSubscriptions);
        return result;
    }

    public UnsubscribeResult Unsubscribe(MqttUnsubscribePacket unsubscribePacket, CancellationToken cancellationToken)
    {
        var result = new UnsubscribeResult();

        var removedSubscriptions = new List<string>();

        _subscriptionsLock.EnterWriteLock();
        try
        {
            foreach (var topicFilterSegment in unsubscribePacket.TopicFilters)
            {
                var topicFilter = SegmentToString(topicFilterSegment);
                _subscriptions.TryGetValue(topicFilter, out var existingSubscription);

                var interceptorEventArgs = InterceptUnsubscribe(topicFilter, existingSubscription, unsubscribePacket.UserProperties, cancellationToken);
                var acceptUnsubscription = interceptorEventArgs.Response.ReasonCode == MqttUnsubscribeReasonCode.Success;

                result.UserProperties = interceptorEventArgs.UserProperties;
                result.ReasonCodes.Add(interceptorEventArgs.Response.ReasonCode);

                if (interceptorEventArgs.CloseConnection)
                {
                    // When any of the interceptor calls leads to a connection close the connection
                    // must be closed. So do not revert to false!
                    result.CloseConnection = true;
                }

                if (!acceptUnsubscription)
                {
                    continue;
                }

                if (interceptorEventArgs.ProcessUnsubscription)
                {
                    _subscriptions.Remove(topicFilter);

                    // must remove subscription object from topic hash dictionary also
                    if (existingSubscription != null)
                    {
                        var topicHash = existingSubscription.TopicHash;

                        if (existingSubscription.TopicHasWildcard)
                        {
                            if (_wildcardSubscriptionsByTopicHash.TryGetValue(topicHash, out var subscriptions))
                            {
                                subscriptions.RemoveSubscription(existingSubscription);
                                if (subscriptions.SubscriptionsByHashMask.Count == 0)
                                {
                                    _wildcardSubscriptionsByTopicHash.Remove(topicHash);
                                }
                            }
                        }
                        else
                        {
                            if (_noWildcardSubscriptionsByTopicHash.TryGetValue(topicHash, out var subscriptions))
                            {
                                subscriptions.Remove(existingSubscription);
                                if (subscriptions.Count == 0)
                                {
                                    _noWildcardSubscriptionsByTopicHash.Remove(topicHash);
                                }
                            }
                        }
                    }

                    removedSubscriptions.Add(topicFilter);
                }
            }
        }
        finally
        {
            _subscriptionsLock.ExitWriteLock();
            _subscriptionChangedNotification?.OnSubscriptionsRemoved(_session, removedSubscriptions);
        }

        return result;
    }

    CreateSubscriptionResult CreateSubscription(MqttTopicFilter topicFilter, string topicString, uint subscriptionIdentifier, MqttSubscribeReasonCode reasonCode)
    {
        MqttQualityOfServiceLevel grantedQualityOfServiceLevel;

        if (reasonCode == MqttSubscribeReasonCode.GrantedQoS0)
        {
            grantedQualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce;
        }
        else if (reasonCode == MqttSubscribeReasonCode.GrantedQoS1)
        {
            grantedQualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce;
        }
        else if (reasonCode == MqttSubscribeReasonCode.GrantedQoS2)
        {
            grantedQualityOfServiceLevel = MqttQualityOfServiceLevel.ExactlyOnce;
        }
        else
        {
            throw new InvalidOperationException();
        }

        var subscription = new MqttSubscription(
            topicString,
            topicFilter.NoLocal,
            topicFilter.RetainHandling,
            topicFilter.RetainAsPublished,
            grantedQualityOfServiceLevel,
            subscriptionIdentifier);

        bool isNewSubscription;

        // Add to subscriptions and maintain topic hash dictionaries

        _subscriptionsLock.EnterWriteLock();
        try
        {
            MqttTopicHash.Calculate(topicString, out var topicHash, out _, out var hasWildcard);

            if (_subscriptions.TryGetValue(topicString, out var existingSubscription))
            {
                // must remove object from topic hash dictionary first
                if (hasWildcard)
                {
                    if (_wildcardSubscriptionsByTopicHash.TryGetValue(topicHash, out var subscriptions))
                    {
                        subscriptions.RemoveSubscription(existingSubscription);
                        // no need to remove empty entry because we'll be adding subscription again below
                    }
                }
                else
                {
                    if (_noWildcardSubscriptionsByTopicHash.TryGetValue(topicHash, out var subscriptions))
                    {
                        subscriptions.Remove(existingSubscription);
                        // no need to remove empty entry because we'll be adding subscription again below
                    }
                }
            }

            isNewSubscription = existingSubscription == null;
            _subscriptions[topicString] = subscription;

            // Add or re-add to topic hash dictionary
            if (hasWildcard)
            {
                if (!_wildcardSubscriptionsByTopicHash.TryGetValue(topicHash, out var subscriptions))
                {
                    subscriptions = new TopicHashMaskSubscriptions();
                    _wildcardSubscriptionsByTopicHash.Add(topicHash, subscriptions);
                }

                subscriptions.AddSubscription(subscription);
            }
            else
            {
                if (!_noWildcardSubscriptionsByTopicHash.TryGetValue(topicHash, out var subscriptions))
                {
                    subscriptions = new HashSet<MqttSubscription>();
                    _noWildcardSubscriptionsByTopicHash.Add(topicHash, subscriptions);
                }

                subscriptions.Add(subscription);
            }
        }
        finally
        {
            _subscriptionsLock.ExitWriteLock();
        }

        return new CreateSubscriptionResult
        {
            IsNewSubscription = isNewSubscription,
            Subscription = subscription
        };
    }

    static void FilterRetainedApplicationMessages(
        IList<MqttApplicationMessage> retainedMessages,
        CreateSubscriptionResult createSubscriptionResult,
        SubscribeResult subscribeResult)
    {
        if (createSubscriptionResult.Subscription.RetainHandling == MqttRetainHandling.DoNotSendOnSubscribe)
        {
            // This is a MQTT V5+ feature.
            return;
        }

        if (createSubscriptionResult.Subscription.RetainHandling == MqttRetainHandling.SendAtSubscribeIfNewSubscriptionOnly && !createSubscriptionResult.IsNewSubscription)
        {
            // This is a MQTT V5+ feature.
            return;
        }

        for (var index = retainedMessages.Count - 1; index >= 0; index--)
        {
            var retainedMessage = retainedMessages[index];
            if (retainedMessage == null)
            {
                continue;
            }

            if (MqttTopicFilterComparer.Compare(retainedMessage.Topic, createSubscriptionResult.Subscription.Topic) != MqttTopicFilterCompareResult.IsMatch)
            {
                continue;
            }

            var retainedMessageMatch = new MqttRetainedMessageMatch(retainedMessage, createSubscriptionResult.Subscription.GrantedQualityOfServiceLevel);
            if (retainedMessageMatch.SubscriptionQualityOfServiceLevel > retainedMessageMatch.ApplicationMessage.QualityOfServiceLevel)
            {
                // UPGRADING the QoS is not allowed!
                // From MQTT spec: Subscribing to a Topic Filter at QoS 2 is equivalent to saying
                // "I would like to receive Messages matching this filter at the QoS with which they were published".
                // This means a publisher is responsible for determining the maximum QoS a Message can be delivered at,
                // but a subscriber is able to require that the Server downgrades the QoS to one more suitable for its usage.
                retainedMessageMatch.SubscriptionQualityOfServiceLevel = retainedMessageMatch.ApplicationMessage.QualityOfServiceLevel;
            }

            if (subscribeResult.RetainedMessages == null)
            {
                subscribeResult.RetainedMessages = new List<MqttRetainedMessageMatch>();
            }

            subscribeResult.RetainedMessages.Add(retainedMessageMatch);

            // Clear the retained message from the list because the client should receive every message only
            // one time even if multiple subscriptions affect them.
            retainedMessages[index] = null;
        }
    }

    InterceptingSubscriptionEventArgs InterceptSubscribe(
        MqttTopicFilter topicFilter,
        List<MqttUserProperty> userProperties)
    {
        var eventArgs = new InterceptingSubscriptionEventArgs(_session.Id, _session.UserName, topicFilter, userProperties);

        if (topicFilter.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce)
        {
            eventArgs.Response.ReasonCode = MqttSubscribeReasonCode.GrantedQoS0;
        }
        else if (topicFilter.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce)
        {
            eventArgs.Response.ReasonCode = MqttSubscribeReasonCode.GrantedQoS1;
        }
        else if (topicFilter.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)
        {
            eventArgs.Response.ReasonCode = MqttSubscribeReasonCode.GrantedQoS2;
        }

        var topicString = SegmentToString(topicFilter.Topic);
        if (topicString.StartsWith("$share/", StringComparison.InvariantCulture))
        {
            eventArgs.Response.ReasonCode = MqttSubscribeReasonCode.SharedSubscriptionsNotSupported;
        }

        return eventArgs;
    }

    InterceptingUnsubscriptionEventArgs InterceptUnsubscribe(
        string topicFilter,
        MqttSubscription mqttSubscription,
        List<MqttUserProperty> userProperties,
        CancellationToken cancellationToken)
    {
        InterceptingUnsubscriptionEventArgs clientUnsubscribingTopicEventArgs = new InterceptingUnsubscriptionEventArgs(_session.Id, _session.UserName, _session.Items, topicFilter, userProperties, cancellationToken)
        {
            Response =
            {
                ReasonCode = mqttSubscription == null ? MqttUnsubscribeReasonCode.NoSubscriptionExisted : MqttUnsubscribeReasonCode.Success
            }
        };

        return clientUnsubscribingTopicEventArgs;
    }

    sealed class CreateSubscriptionResult
    {
        public bool IsNewSubscription { get; set; }

        public MqttSubscription Subscription { get; set; }
    }
}