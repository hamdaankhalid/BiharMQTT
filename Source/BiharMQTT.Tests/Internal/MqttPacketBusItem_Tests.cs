// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Formatter;
using BiharMQTT.Internal;

namespace BiharMQTT.Tests.Internal;

// ReSharper disable InconsistentNaming
[TestClass]
public sealed class MqttPacketBusItem_Tests
{
    static MqttPacketBuffer DummyBuffer() => new(new ArraySegment<byte>(new byte[] { 0 }));

    [TestMethod]
    public void Fire_Completed_Event()
    {
        var eventFired = false;

        var item = new MqttPacketBusItem(DummyBuffer());
        item.Completed += (_, _) =>
        {
            eventFired = true;
        };

        item.Complete();

        Assert.IsTrue(eventFired);
    }

    [TestMethod]
    public Task Wait_Packet_Bus_Item_After_Already_Canceled()
    {
        return Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
        {
            var item = new MqttPacketBusItem(DummyBuffer());

            // Finish the item before the actual
            item.Cancel();

            await item.WaitAsync();
        });
    }
}