// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Security.Cryptography;
using BiharMQTT.Formatter;
using BiharMQTT.Formatter.V5;
using BiharMQTT.Internal;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;
using BiharMQTT.Server.Exceptions;
using static BiharMQTT.Internal.MqttSegmentHelper;

namespace BiharMQTT.Server.Internal;

public delegate void ActionRef2<T>(ref MqttPacketBusItem packetBusItem, T state);

public sealed class MqttSession : IDisposable
{
    readonly MqttClientSessionsManager _clientSessionsManager;
    readonly MqttConnectPacket _connectPacket;
    readonly MqttPacketBus _packetBus = new();
    readonly MqttPacketIdentifierProvider _packetIdentifierProvider = new();
    readonly MqttServerOptions _serverOptions;
    readonly MqttClientSubscriptionsManager _subscriptionsManager;

    readonly MqttV5PacketEncoder _encoder = new(new MqttBufferWriter(4096, 65535));
    readonly object _encoderLock = new();

    // Do not use a dictionary in order to keep the ordering of the messages.
    readonly List<MqttPublishPacket> _unacknowledgedPublishPackets = new();

    // Bookkeeping to know if this is a subscribing client; lazy initialize later.
    HashSet<string> _subscribedTopics;

    public MqttSession(
        MqttConnectPacket connectPacket,
        IDictionary items,
        MqttServerOptions serverOptions,
        MqttRetainedMessagesManager retainedMessagesManager,
        MqttClientSessionsManager clientSessionsManager)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));

        _connectPacket = connectPacket;
        _serverOptions = serverOptions ?? throw new ArgumentNullException(nameof(serverOptions));
        _clientSessionsManager = clientSessionsManager ?? throw new ArgumentNullException(nameof(clientSessionsManager));

        _subscriptionsManager = new MqttClientSubscriptionsManager(this, retainedMessagesManager, clientSessionsManager);
    }

    public DateTime CreatedTimestamp { get; } = DateTime.UtcNow;

    public DateTime? DisconnectedTimestamp { get; set; }

    public uint ExpiryInterval => _connectPacket.SessionExpiryInterval;

    public bool HasSubscribedTopics => _subscribedTopics != null && _subscribedTopics.Count > 0;

    public string Id => SegmentToString(_connectPacket.ClientId);

    public string UserName => SegmentToString(_connectPacket.Username);

    public IDictionary Items { get; }

    public MqttConnectPacket LatestConnectPacket { get; set; }

    public MqttPacketIdentifierProvider PacketIdentifierProvider { get; } = new();

    public long PendingDataPacketsCount => _packetBus.PartitionItemsCount(MqttPacketBusPartition.Data);

    public bool WillMessageSent { get; set; }

    public MqttPublishPacket AcknowledgePublishPacket(ushort packetIdentifier)
    {
        MqttPublishPacket publishPacket;

        lock (_unacknowledgedPublishPackets)
        {
            publishPacket = _unacknowledgedPublishPackets.FirstOrDefault(p => p.PacketIdentifier.Equals(packetIdentifier));
            _unacknowledgedPublishPackets.Remove(publishPacket);
        }

        return publishPacket;
    }

    public void AddSubscribedTopic(string topic)
    {
        if (_subscribedTopics == null)
        {
            _subscribedTopics = new HashSet<string>();
        }

        _subscribedTopics.Add(topic);
    }

    public Task DeleteAsync()
    {
        return _clientSessionsManager.DeleteSessionAsync(Id);
    }

    public Task<MqttPacketBusItem> DequeuePacketAsync(CancellationToken cancellationToken)
    {
        return _packetBus.DequeueItemAsync(cancellationToken);
    }

    public void Dispose()
    {
        _packetBus.Dispose();
        _subscriptionsManager.Dispose();
    }

    public void EnqueueControlPacket(ref MqttPacketBusItem packetBusItem)
    {
        _packetBus.EnqueueItem(ref packetBusItem, MqttPacketBusPartition.Control);
    }

    public EnqueueDataPacketResult EnqueueDataPacket(MqttPacketBusItem packetBusItem) => EnqueueDataPacket(ref packetBusItem);

    public EnqueueDataPacketResult EnqueueDataPacket(ref MqttPacketBusItem packetBusItem)
    {
        if (PendingDataPacketsCount >= _serverOptions.MaxPendingMessagesPerClient)
        {
            if (_serverOptions.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropNewMessage)
            {
                packetBusItem.Fail(new MqttPendingMessagesOverflowException(Id, _serverOptions.PendingMessagesOverflowStrategy));
                return EnqueueDataPacketResult.Dropped;
            }

            if (_serverOptions.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
            {
                // Only drop from the data partition. Dropping from control partition might break the connection
                // because the client does not receive PINGREQ packets etc. any longer.
                var firstItem = _packetBus.DropFirstItem(MqttPacketBusPartition.Data);
                if (firstItem.HasValue)
                {
                    var item = firstItem.Value;
                    item.Fail(new MqttPendingMessagesOverflowException(Id, _serverOptions.PendingMessagesOverflowStrategy));
                }
            }
        }

        _packetBus.EnqueueItem(ref packetBusItem, MqttPacketBusPartition.Data);
        return EnqueueDataPacketResult.Enqueued;
    } 

    /// <summary>
    ///     Encodes a publish packet and enqueues it while allowing an explicit state
    ///     parameter for callback configuration to avoid closure allocations.
    /// </summary>
    public EnqueueDataPacketResult EnqueuePublishPacket(
        ref MqttPublishPacket publishPacket,
        (MessageRingBuffer, MessageSlot) state = default,
        ActionRef2<(MessageRingBuffer, MessageSlot)> configureBusItem = null)
    {
        if (publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
        {
            publishPacket.PacketIdentifier = _packetIdentifierProvider.GetNextPacketIdentifier();

            lock (_unacknowledgedPublishPackets)
            {
                _unacknowledgedPublishPackets.Add(publishPacket);
            }
        }

        MqttPacketBuffer buffer;
         
        lock (_encoderLock)
        {
            var encoded = _encoder.Encode(ref publishPacket);

            // The Packet segment references the encoder's shared internal buffer
            // which is overwritten on the next Encode call. Copy the header bytes
            // so the enqueued packet owns stable memory.
            // HK TODO: This is an annoying allocation
            var headerCopy = encoded.Packet.AsSpan().ToArray();
            buffer = encoded.Payload.Length > 0
                ? new MqttPacketBuffer(new ArraySegment<byte>(headerCopy), encoded.Payload)
                : new MqttPacketBuffer(new ArraySegment<byte>(headerCopy));
        }

        var busItem = new MqttPacketBusItem(buffer);
        configureBusItem?.Invoke(ref busItem, state);

        return EnqueueDataPacket(ref busItem);
    }

    public void EnqueueHealthPacket(MqttPacketBusItem packetBusItem)
    {
        _packetBus.EnqueueItem(ref packetBusItem, MqttPacketBusPartition.Health);
    }

    public MqttPublishPacket PeekAcknowledgePublishPacket(ushort packetIdentifier)
    {
        // This will only return the matching PUBLISH packet but does not remove it.
        // This is required for QoS 2.
        lock (_unacknowledgedPublishPackets)
        {
            return _unacknowledgedPublishPackets.FirstOrDefault(p => p.PacketIdentifier.Equals(packetIdentifier));
        }
    }

    // HK TODO: Understand when and how this is used
    public void Recover()
    {
        // TODO: Keep the bus and only insert pending items again.
        // TODO: Check if packet identifier must be restarted or not.
        // TODO: Recover package identifier.

        /*
            The Session state in the Client consists of:
            ·         QoS 1 and QoS 2 messages which have been sent to the Server, but have not been completely acknowledged.
            ·         QoS 2 messages which have been received from the Server, but have not been completely acknowledged.

            The Session state in the Server consists of:
            ·         The existence of a Session, even if the rest of the Session state is empty.
            ·         The Client’s subscriptions.
            ·         QoS 1 and QoS 2 messages which have been sent to the Client, but have not been completely acknowledged.
            ·         QoS 1 and QoS 2 messages pending transmission to the Client.
            ·         QoS 2 messages which have been received from the Client, but have not been completely acknowledged.
            ·         Optionally, QoS 0 messages pending transmission to the Client.
         */

        // Create a copy of all currently unacknowledged publish packets and clear the storage.
        // We must re-enqueue them in order to trigger other code.
        List<MqttPublishPacket> unacknowledgedPublishPackets;
        lock (_unacknowledgedPublishPackets)
        {
            unacknowledgedPublishPackets = _unacknowledgedPublishPackets.ToList();
            _unacknowledgedPublishPackets.Clear();
        }

        _packetBus.Clear();

        foreach (var publishPacket in unacknowledgedPublishPackets)
        {
            var pkt = publishPacket;
            EnqueuePublishPacket(ref pkt);
        }
    }

    public void RemoveSubscribedTopic(string topic)
    {
        _subscribedTopics?.Remove(topic);
    }

    public Task<SubscribeResult> Subscribe(MqttSubscribePacket subscribePacket, CancellationToken cancellationToken)
    {
        return _subscriptionsManager.Subscribe(subscribePacket, cancellationToken);
    }

    public bool TryCheckSubscriptions(string topic, ulong topicHash, MqttQualityOfServiceLevel qualityOfServiceLevel, string senderId, out CheckSubscriptionsResult result)
    {
        result = null;

        try
        {
            result = _subscriptionsManager.CheckSubscriptions(topic, topicHash, qualityOfServiceLevel, senderId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<UnsubscribeResult> Unsubscribe(MqttUnsubscribePacket unsubscribePacket, CancellationToken cancellationToken)
    {
        return _subscriptionsManager.Unsubscribe(unsubscribePacket, cancellationToken);
    }
}