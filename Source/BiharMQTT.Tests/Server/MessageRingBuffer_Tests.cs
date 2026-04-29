// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Server.Internal;

namespace BiharMQTT.Tests.Server;

[TestClass]
public sealed class MessageRingBuffer_Tests
{
    [TestMethod]
    public async Task Acquire_Returns_Valid_Slot()
    {
        using var rb = new MessageRingBuffer(1024, 16);

        var (slot, memory) = await rb.Acquire(64);

        Assert.IsTrue(slot.IsValid);
        Assert.AreEqual(64, slot.Length);
        Assert.AreEqual(64, memory.Length);
    }

    [TestMethod]
    public async Task Write_And_Read_Payload()
    {
        using var rb = new MessageRingBuffer(1024, 16);

        var payload = "hello world"u8.ToArray();
        var (slot, memory) = await rb.Acquire(payload.Length);
        payload.CopyTo(memory);

        var readBack = rb.GetPayload(slot);
        Assert.IsTrue(readBack.Span.SequenceEqual(payload));
    }

    [TestMethod]
    public async Task Multiple_Acquires_Use_Different_Regions()
    {
        using var rb = new MessageRingBuffer(1024, 16);

        var (slot1, mem1) = await rb.Acquire(100);
        var (slot2, mem2) = await rb.Acquire(100);

        Assert.AreNotEqual(slot1.Offset, slot2.Offset);
        Assert.AreEqual(0, slot1.Offset);
        Assert.AreEqual(100, slot2.Offset);
    }

    [TestMethod]
    public async Task AddRef_And_Release_Lifecycle()
    {
        using var rb = new MessageRingBuffer(256, 16);

        var (slot, memory) = await rb.Acquire(64);

        // Sentinel refcount = 1 from Acquire.
        // Simulate 3 subscribers.
        rb.AddRef(slot);
        rb.AddRef(slot);
        rb.AddRef(slot);

        // Release sentinel
        rb.Release(slot);

        // Release 2 subscribers — slot should still be alive
        rb.Release(slot);
        rb.Release(slot);

        // Release last subscriber — slot should be reclaimed
        rb.Release(slot);

        Assert.AreEqual(1L, rb.TotalAcquired);
        Assert.AreEqual(1L, rb.TotalReleased);
    }

    [TestMethod]
    public async Task Empty_Payload_Returns_Empty_Slot()
    {
        using var rb = new MessageRingBuffer(1024, 16);

        var (slot, memory) = await rb.Acquire(0);

        Assert.IsFalse(slot.IsValid);
        Assert.IsTrue(memory.IsEmpty);
    }

    [TestMethod]
    public async Task GetPayload_On_Empty_Slot_Returns_Empty()
    {
        using var rb = new MessageRingBuffer(1024, 16);

        var payload = rb.GetPayload(MessageSlot.Empty);

        Assert.IsTrue(payload.IsEmpty);
    }

    [TestMethod]
    public void Acquire_Payload_Exceeding_Capacity_Throws()
    {
        using var rb = new MessageRingBuffer(64, 16);

        Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => rb.Acquire(128).AsTask());
    }

    [TestMethod]
    public async Task Back_Pressure_Blocks_Until_Space_Available()
    {
        // Small buffer: 128 bytes, so two 64-byte messages fill it.
        using var rb = new MessageRingBuffer(128, 16);

        var (slot1, _) = await rb.Acquire(64);
        var (slot2, _) = await rb.Acquire(64);

        // Buffer is now full. A third acquire should block.
        var acquireTask = rb.Acquire(64).AsTask();

        // Give it a moment — should NOT complete.
        await Task.Delay(100);
        Assert.IsFalse(acquireTask.IsCompleted, "Acquire should block when buffer is full");

        // Release slot1 — frees 64 bytes.
        rb.Release(slot1);

        // Now the third acquire should complete.
        var (slot3, _) = await acquireTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(slot3.IsValid);
    }

    [TestMethod]
    public async Task Wraparound_When_Tail_Space_Insufficient()
    {
        // Buffer of 100 bytes. Acquire 60, release it, then acquire 60 again.
        // First 60 is at offset 0. Write head at 60. 40 bytes of tail space.
        // Next 60 can't fit in tail → should wrap to offset 0.
        using var rb = new MessageRingBuffer(100, 16);

        var (slot1, _) = await rb.Acquire(60);
        rb.Release(slot1); // free immediately

        // Need to wait briefly for semaphore release to propagate
        await Task.Delay(50);

        var (slot2, _) = await rb.Acquire(60);

        // slot2 should have wrapped to offset 0
        Assert.AreEqual(0, slot2.Offset);
        Assert.AreEqual(60, slot2.Length);
    }

    [TestMethod]
    public async Task Concurrent_Producers_Dont_Overlap()
    {
        using var rb = new MessageRingBuffer(4096, 256);
        var slots = new List<(MessageSlot Slot, int Value)>();
        var slotLock = new object();

        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            var (slot, memory) = await rb.Acquire(16);
            // Write a pattern to detect overlap
            memory.Span.Fill((byte)(i & 0xFF));

            lock (slotLock)
            {
                slots.Add((slot, i & 0xFF));
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        // Verify each slot has its expected pattern
        foreach (var (slot, value) in slots)
        {
            var payload = rb.GetPayload(slot);
            for (var j = 0; j < payload.Length; j++)
            {
                Assert.AreEqual((byte)value, payload.Span[j],
                    $"Corruption at slot seq={slot.SequenceNumber}, byte {j}");
            }
        }
    }

    [TestMethod]
    public async Task Release_Stale_Slot_Is_NoOp()
    {
        using var rb = new MessageRingBuffer(1024, 4);

        // Acquire and release slot at index 0
        var (slot1, _) = await rb.Acquire(32);
        rb.Release(slot1);

        // Acquire 4 more slots to wrap the metadata array and reuse index 0
        for (var i = 0; i < 4; i++)
        {
            var (s, _) = await rb.Acquire(32);
            rb.Release(s);
            await Task.Delay(10);
        }

        // Now releasing slot1 again should be a no-op (stale sequence number)
        rb.Release(slot1); // should not throw or corrupt state
    }

    [TestMethod]
    public async Task ReleaseCallback_Works_With_PacketBusItem()
    {
        var rb = new MessageRingBuffer(1024, 16);

        var (slot, memory) = await rb.Acquire(32);
        "test payload"u8.CopyTo(memory.Span);

        // Simulate what the dispatch loop does
        rb.AddRef(slot); // subscriber ref
        rb.Release(slot); // sentinel release

        // Simulate what MqttPacketBusItem.OnTerminated does
        MessageRingBuffer.ReleaseCallback(ref rb, ref slot);

        Assert.AreEqual(1L, rb.TotalReleased);
        rb.Dispose();
    }

    [TestMethod]
    public async Task Cancellation_Aborts_Blocked_Acquire()
    {
        using var rb = new MessageRingBuffer(64, 16);
        using var cts = new CancellationTokenSource();

        // Fill the buffer
        var (slot, _) = await rb.Acquire(64);

        // Start a blocked acquire
        var acquireTask = rb.Acquire(32, cts.Token).AsTask();

        // Cancel it
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => acquireTask);
    }

    [TestMethod]
    public async Task Diagnostic_Counters()
    {
        using var rb = new MessageRingBuffer(1024, 16);

        Assert.AreEqual(0L, rb.TotalAcquired);
        Assert.AreEqual(0L, rb.TotalReleased);

        var (slot1, _) = await rb.Acquire(32);
        var (slot2, _) = await rb.Acquire(32);

        Assert.AreEqual(2L, rb.TotalAcquired);

        rb.Release(slot1);
        Assert.AreEqual(1L, rb.TotalReleased);

        rb.Release(slot2);
        Assert.AreEqual(2L, rb.TotalReleased);
    }

    [TestMethod]
    public async Task GetWritablePayload_Returns_Writable_Memory()
    {
        using var rb = new MessageRingBuffer(1024, 16);

        var (slot, _) = await rb.Acquire(16);

        var writable = rb.GetWritablePayload(slot);
        "direct write"u8.CopyTo(writable.Span);

        var readBack = rb.GetPayload(slot);
        Assert.IsTrue(readBack.Span.StartsWith("direct write"u8));
    }
}
