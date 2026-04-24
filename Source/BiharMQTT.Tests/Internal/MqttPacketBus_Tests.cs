// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using BiharMQTT.Formatter;
using BiharMQTT.Internal;

namespace BiharMQTT.Tests.Internal;

[SuppressMessage("ReSharper", "InconsistentNaming")]
[TestClass]
public sealed class MqttPacketBus_Tests
{
    static MqttPacketBuffer Buf(byte tag) => new(new ArraySegment<byte>(new[] { tag }));

    const byte TagData = 1;
    const byte TagControl = 2;
    const byte TagHealth = 3;

    [TestMethod]
    public void Alternate_Priorities()
    {
        var bus = new MqttPacketBus();

        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagData)), MqttPacketBusPartition.Data);
        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagData)), MqttPacketBusPartition.Data);
        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagData)), MqttPacketBusPartition.Data);

        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagControl)), MqttPacketBusPartition.Control);
        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagControl)), MqttPacketBusPartition.Control);
        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagControl)), MqttPacketBusPartition.Control);

        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagHealth)), MqttPacketBusPartition.Health);
        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagHealth)), MqttPacketBusPartition.Health);
        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagHealth)), MqttPacketBusPartition.Health);

        Assert.AreEqual(9, bus.TotalItemsCount);

        // Items are dequeued round-robin across partitions.
        Assert.AreEqual(TagData, bus.DequeueItemAsync(CancellationToken.None).Result.PacketBuffer.Packet[0]);
        Assert.AreEqual(TagControl, bus.DequeueItemAsync(CancellationToken.None).Result.PacketBuffer.Packet[0]);
        Assert.AreEqual(TagHealth, bus.DequeueItemAsync(CancellationToken.None).Result.PacketBuffer.Packet[0]);
        Assert.AreEqual(TagData, bus.DequeueItemAsync(CancellationToken.None).Result.PacketBuffer.Packet[0]);
        Assert.AreEqual(TagControl, bus.DequeueItemAsync(CancellationToken.None).Result.PacketBuffer.Packet[0]);
        Assert.AreEqual(TagHealth, bus.DequeueItemAsync(CancellationToken.None).Result.PacketBuffer.Packet[0]);
        Assert.AreEqual(TagData, bus.DequeueItemAsync(CancellationToken.None).Result.PacketBuffer.Packet[0]);
        Assert.AreEqual(TagControl, bus.DequeueItemAsync(CancellationToken.None).Result.PacketBuffer.Packet[0]);
        Assert.AreEqual(TagHealth, bus.DequeueItemAsync(CancellationToken.None).Result.PacketBuffer.Packet[0]);

        Assert.AreEqual(0, bus.TotalItemsCount);
    }

    [TestMethod]
    public void Await_Single_Packet()
    {
        var bus = new MqttPacketBus();

        var delivered = false;

        var item1 = new MqttPacketBusItem(Buf(TagData));
        var item2 = new MqttPacketBusItem(Buf(TagData));

        var item3 = new MqttPacketBusItem(Buf(TagData));
        item3.Completed += (_, _) =>
        {
            delivered = true;
        };

        bus.EnqueueItem(item1, MqttPacketBusPartition.Data);
        bus.EnqueueItem(item2, MqttPacketBusPartition.Data);
        bus.EnqueueItem(item3, MqttPacketBusPartition.Data);

        Assert.IsFalse(delivered);

        bus.DequeueItemAsync(CancellationToken.None).Result.Complete();

        Assert.IsFalse(delivered);

        bus.DequeueItemAsync(CancellationToken.None).Result.Complete();

        Assert.IsFalse(delivered);

        bus.DequeueItemAsync(CancellationToken.None).Result.Complete();

        // The third packet has the event attached.
        Assert.IsTrue(delivered);
    }

    [TestMethod]
    public void Export_Packets_Without_Dequeue()
    {
        var bus = new MqttPacketBus();

        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagData)), MqttPacketBusPartition.Data);
        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagData)), MqttPacketBusPartition.Data);
        bus.EnqueueItem(new MqttPacketBusItem(Buf(TagData)), MqttPacketBusPartition.Data);

        Assert.AreEqual(3, bus.TotalItemsCount);

        var exportedPackets = bus.ExportPackets(MqttPacketBusPartition.Control);
        Assert.HasCount(0, exportedPackets);

        exportedPackets = bus.ExportPackets(MqttPacketBusPartition.Health);
        Assert.HasCount(0, exportedPackets);

        exportedPackets = bus.ExportPackets(MqttPacketBusPartition.Data);
        Assert.HasCount(3, exportedPackets);

        Assert.AreEqual(3, bus.TotalItemsCount);
    }

    [TestMethod]
    public async Task Fill_From_Different_Task()
    {
        const int messageCount = 500;

        var delayRandom = new Random();

        var bus = new MqttPacketBus();

        _ = Task.Run(
            () =>
            {
                for (var i = 0; i < messageCount; i++)
                {
                    bus.EnqueueItem(new MqttPacketBusItem(Buf(TagHealth)), MqttPacketBusPartition.Health);

                    Thread.Sleep(delayRandom.Next(0, 10));
                }
            });

        for (var i = 0; i < messageCount; i++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await bus.DequeueItemAsync(timeout.Token);
        }

        Assert.AreEqual(0, bus.TotalItemsCount);
    }

    [TestMethod]
    public Task Wait_With_Empty_Bus()
    {
        return Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
        {
            var bus = new MqttPacketBus();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await bus.DequeueItemAsync(timeout.Token);
        });

    }
}