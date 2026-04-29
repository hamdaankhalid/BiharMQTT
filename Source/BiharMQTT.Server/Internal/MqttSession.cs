// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using BiharMQTT.Adapter;
using BiharMQTT.Formatter;
using BiharMQTT.Formatter.V5;
using BiharMQTT.Internal;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;
using static BiharMQTT.Internal.MqttSegmentHelper;

namespace BiharMQTT.Server.Internal;

public sealed class MqttSession : IDisposable
{
    readonly MqttClientSessionsManager _clientSessionsManager;
    readonly MqttConnectPacket _connectPacket;
    readonly MqttPacketBus _packetBus;
    readonly MqttPacketIdentifierProvider _packetIdentifierProvider = new();
    readonly MqttServerOptions _serverOptions;
    readonly MqttClientSubscriptionsManager _subscriptionsManager;

    readonly MqttV5PacketEncoder _encoder = new(new MqttBufferWriter(Constants.PerBufferWriterMemoryAllocatedbytes * 4));
    readonly object _encoderLock = new();

    // Stores encoded bytes for QoS > 0 retransmission on session recovery.
    // We store (packetIdentifier, encodedBytes) instead of raw MqttPublishPacket
    // so that segment fields don't need to outlive the publisher's body buffer.
    readonly List<UnacknowledgedPublishEntry> _unacknowledgedPublishPackets = new();

    // Bookkeeping to know if this is a subscribing client; lazy initialize later.
    HashSet<string> _subscribedTopics;

    // Invoked after every successful enqueue so the multiplexed sender pool can
    // schedule the owning connected client. Set by the client when it attaches
    // (RunAsync) and cleared on detach so a dead client cannot be re-scheduled.
    // Volatile read; assignment is plain — at most one client owns a session at a time.
    Action _onPacketReady;

    // Currently-attached channel adapter for the inline-send fast path. When
    // non-null, enqueue calls try a direct write to the socket instead of
    // routing bytes through the bus + sender pool. Cleared on detach.
    MqttChannelAdapter _attachedAdapter;

    public MqttSession(
        MqttConnectPacket connectPacket,
        IDictionary items,
        MqttServerOptions serverOptions,
        MqttRetainedMessagesManager retainedMessagesManager,
        MqttClientSessionsManager clientSessionsManager,
        HugeNativeMemoryPool hugeNativeMemoryPool)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Id = SegmentToString(connectPacket.ClientId);
        UserName = SegmentToString(connectPacket.Username);

        _connectPacket = connectPacket;
        _serverOptions = serverOptions ?? throw new ArgumentNullException(nameof(serverOptions));
        _clientSessionsManager = clientSessionsManager ?? throw new ArgumentNullException(nameof(clientSessionsManager));

        _subscriptionsManager = new MqttClientSubscriptionsManager(this, retainedMessagesManager, clientSessionsManager);
        _packetBus = new MqttPacketBus(hugeNativeMemoryPool);
    }

    public DateTime CreatedTimestamp { get; } = DateTime.UtcNow;

    public DateTime? DisconnectedTimestamp { get; set; }

    public uint ExpiryInterval => _connectPacket.SessionExpiryInterval;

    public bool HasSubscribedTopics => _subscribedTopics != null && _subscribedTopics.Count > 0;

    public string Id {get; init; }

    public string UserName {get; init; }

    public IDictionary Items { get; }

    public MqttConnectPacket LatestConnectPacket { get; set; }

    public MqttPacketIdentifierProvider PacketIdentifierProvider { get; } = new();

    public long PendingDataPacketsCount => _packetBus.PartitionItemsCount(MqttPacketBusPartition.Data);

    public bool WillMessageSent { get; set; }

    public void AcknowledgePublishPacket(ushort packetIdentifier)
    {
        lock (_unacknowledgedPublishPackets)
        {
            for (int i = 0; i < _unacknowledgedPublishPackets.Count; i++)
            {
                if (_unacknowledgedPublishPackets[i].PacketIdentifier == packetIdentifier)
                {
                    _unacknowledgedPublishPackets.RemoveAt(i);
                    return;
                }
            }
        }
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

    public bool TryDequeuePacket(Memory<byte> dest, out int bytesWritten) => _packetBus.TryDequeueItem(dest, out bytesWritten);

    public bool HasPendingPackets => !_packetBus.IsEmpty;

    public void SetReadyNotifier(Action onPacketReady)
    {
        Volatile.Write(ref _onPacketReady, onPacketReady);
    }

    public void SetAttachedAdapter(MqttChannelAdapter adapter)
    {
        Volatile.Write(ref _attachedAdapter, adapter);
    }

    void NotifyReady() => Volatile.Read(ref _onPacketReady)?.Invoke();

    bool TrySendInline(ref MqttPacketBuffer buffer)
    {
        var adapter = Volatile.Read(ref _attachedAdapter);
        if (adapter == null) return false;
        return adapter.TrySendInline(ref buffer, _packetBus);
    }

    public void Dispose()
    {
        _packetBus.Dispose();
        _subscriptionsManager.Dispose();
        _encoder.Dispose();
    }

    public void EnqueueControlPacket(ref MqttPacketBuffer packetBusItem)
    {
        if (TrySendInline(ref packetBusItem))
        {
            return;
        }

        _packetBus.EnqueueItem(ref packetBusItem, MqttPacketBusPartition.Control);
        NotifyReady();
    }

    public EnqueueDataPacketResult EnqueueDataPacket(ref MqttPacketBuffer packetBusItem)
    {
        if (PendingDataPacketsCount >= Constants.MaxPendingMessagesPerSession)
        {
            if (_serverOptions.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropNewMessage)
            {
                return EnqueueDataPacketResult.Dropped;
            }

            if (_serverOptions.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
            {
                // Only drop from the data partition. Dropping from control partition might break the connection
                // because the client does not receive PINGREQ packets etc. any longer.
                _packetBus.DropFirstItem(MqttPacketBusPartition.Data);
            }
        }

        // Fast path: if no one has queued and the channel send-lock is uncontended,
        // skip the bus and write directly to the socket. The adapter validates the
        // bus is empty *inside* its send lock, so FIFO is preserved with respect
        // to the worker.
        if (TrySendInline(ref packetBusItem))
        {
            return EnqueueDataPacketResult.Enqueued;
        }

        _packetBus.EnqueueItem(ref packetBusItem, MqttPacketBusPartition.Data);
        NotifyReady();
        return EnqueueDataPacketResult.Enqueued;
    }

    /// <summary>
    ///     Encodes a publish packet and enqueues it while allowing an explicit state
    ///     parameter for callback configuration to avoid closure allocations.
    /// </summary>
    public EnqueueDataPacketResult EnqueuePublishPacket(ref MqttPublishPacket publishPacket)
    {
        if (publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
        {
            publishPacket.PacketIdentifier = _packetIdentifierProvider.GetNextPacketIdentifier();
        }

        MqttPacketBuffer encoded;
        lock (_encoderLock)
        {
            encoded = _encoder.Encode(ref publishPacket);

            // Anything above QOS-0 is going to have allocations in hotpath atm
            if (publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
            {
                // Store a self-contained copy of the encoded bytes for retransmission.
                // This avoids keeping references to the publisher's body buffer.
                lock (_unacknowledgedPublishPackets)
                {
                    _unacknowledgedPublishPackets.Add(new UnacknowledgedPublishEntry(publishPacket.PacketIdentifier, encoded.ToArray()));
                }
            }

            return EnqueueDataPacket(ref encoded);
        }
    }

    public void EnqueueHealthPacket(ref MqttPacketBuffer packetBusItem)
    {
        if (TrySendInline(ref packetBusItem))
        {
            return;
        }

        _packetBus.EnqueueItem(ref packetBusItem, MqttPacketBusPartition.Health);
        NotifyReady();
    }

    public bool HasUnacknowledgedPublishPacket(ushort packetIdentifier)
    {
        lock (_unacknowledgedPublishPackets)
        {
            for (int i = 0; i < _unacknowledgedPublishPackets.Count; i++)
            {
                if (_unacknowledgedPublishPackets[i].PacketIdentifier == packetIdentifier)
                {
                    return true;
                }
            }
            return false;
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

        // Create a copy of all currently unacknowledged encoded packets and clear the storage.
        // We re-enqueue the already-encoded bytes directly — no re-encoding needed.
        List<UnacknowledgedPublishEntry> unacknowledgedEntries;
        lock (_unacknowledgedPublishPackets)
        {
            unacknowledgedEntries = new List<UnacknowledgedPublishEntry>(_unacknowledgedPublishPackets);
            _unacknowledgedPublishPackets.Clear();
        }

        _packetBus.Clear();

        foreach (var entry in unacknowledgedEntries)
        {
            var buffer = new MqttPacketBuffer(new ArraySegment<byte>(entry.EncodedBytes));
            EnqueueDataPacket(ref buffer);

            // Re-add to unacknowledged so they can be ACK'd or recovered again.
            lock (_unacknowledgedPublishPackets)
            {
                _unacknowledgedPublishPackets.Add(entry);
            }
        }
    }

    public void RemoveSubscribedTopic(string topic)
    {
        _subscribedTopics?.Remove(topic);
    }

    public SubscribeResult Subscribe(ref MqttSubscribePacket subscribePacket)
    {
        return _subscriptionsManager.Subscribe(ref subscribePacket);
    }

    public bool TryCheckSubscriptions(Span<byte> topic, ulong topicHash, MqttQualityOfServiceLevel qualityOfServiceLevel, string senderId, out CheckSubscriptionsResult result)
    {
        result = default;

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

    public UnsubscribeResult Unsubscribe(MqttUnsubscribePacket unsubscribePacket, CancellationToken cancellationToken)
    {
        return _subscriptionsManager.Unsubscribe(unsubscribePacket, cancellationToken);
    }

    readonly struct UnacknowledgedPublishEntry(ushort packetIdentifier, byte[] encodedBytes)
    {
        public ushort PacketIdentifier { get; } = packetIdentifier;
        public byte[] EncodedBytes { get; } = encodedBytes;
    }
}