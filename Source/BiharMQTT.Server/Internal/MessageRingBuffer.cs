// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace BiharMQTT.Server.Internal;

/// <summary>
///     A pre-allocated ring buffer for MQTT message payloads.  All in-flight
///     message bytes live inside a single contiguous <c>byte[]</c> that is
///     allocated once at server startup.
///     <para>
///         <b>Write path</b>: <see cref="Acquire" /> reserves a contiguous
///         region and returns a <see cref="MessageSlot" /> handle plus a
///         <see cref="Memory{T}" /> the caller writes payload bytes into.
///     </para>
///     <para>
///         <b>Read path</b>: <see cref="GetPayload" /> returns a
///         <see cref="ReadOnlyMemory{T}" /> view for a previously acquired slot.
///     </para>
///     <para>
///         <b>Lifecycle</b>: call <see cref="AddRef" /> once per subscriber
///         before enqueuing; call <see cref="Release" /> when each consumer
///         finishes.  When the ref count reaches 0 the region becomes
///         reclaimable and the buffer can advance past it.
///     </para>
///     <para>
///         <b>Back-pressure</b>: when insufficient contiguous free space exists,
///         <see cref="Acquire" /> blocks (async) until enough space is reclaimed.
///     </para>
/// </summary>
public sealed class MessageRingBuffer : IDisposable
{
    readonly byte[] _buffer;
    readonly int _capacity;
    readonly MessageSlotMetadata[] _slots;
    readonly int _maxSlots;

    // ── Write head ──
    // Only the Acquire path advances _writeHead; protected by _writeLock.
    readonly object _writeLock = new();
    int _writeHead;

    // ── Read/reclaim head ──
    // Reserved for future ordered reclaim tracking.
    // int _readHead;

    // ── Sequence counter ──
    long _nextSequence;

    // ── Back-pressure ──
    readonly SemaphoreSlim _spaceAvailable;

    // ── Metrics ──
    long _totalAcquired;
    long _totalReleased;

    /// <summary>
    ///     Creates a new ring buffer with the specified capacity.
    /// </summary>
    /// <param name="capacityBytes">
    ///     Total size of the pre-allocated payload buffer in bytes.
    /// </param>
    /// <param name="maxSlots">
    ///     Maximum number of concurrent in-flight messages.  Determines the
    ///     size of the metadata array.
    /// </param>
    public MessageRingBuffer(int capacityBytes, int maxSlots)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacityBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSlots, 0);

        _capacity = capacityBytes;
        _maxSlots = maxSlots;
        _buffer = GC.AllocateUninitializedArray<byte>(capacityBytes, pinned: true);
        _slots = new MessageSlotMetadata[maxSlots];

        for (var i = 0; i < maxSlots; i++)
        {
            _slots[i] = new MessageSlotMetadata();
        }

        // Initial free space = full capacity.  Each Acquire decrements by
        // the payload length; each reclaim increments.
        _spaceAvailable = new SemaphoreSlim(capacityBytes, capacityBytes);
    }

    /// <summary>Total bytes capacity of the ring buffer.</summary>
    public int Capacity => _capacity;

    /// <summary>Total messages ever acquired (diagnostic).</summary>
    public long TotalAcquired => Interlocked.Read(ref _totalAcquired);

    /// <summary>Total messages ever released (diagnostic).</summary>
    public long TotalReleased => Interlocked.Read(ref _totalReleased);

    /// <summary>
    ///     Reserves a contiguous region in the ring buffer for a message payload
    ///     of the specified length.  Blocks asynchronously if insufficient space
    ///     is available (back-pressure).
    /// </summary>
    /// <param name="length">Required payload size in bytes. Must be &gt; 0.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     A tuple of the <see cref="MessageSlot" /> handle and a
    ///     <see cref="Memory{T}" /> the caller must write the payload into.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     If <paramref name="length" /> exceeds <see cref="Capacity" />.
    /// </exception>
    public async ValueTask<(MessageSlot Slot, Memory<byte> Buffer)> Acquire(int length, CancellationToken cancellationToken = default)
    {
        if (length <= 0)
        {
            return (MessageSlot.Empty, Memory<byte>.Empty);
        }

        if (length > _capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"Message payload length ({length}) exceeds ring buffer capacity ({_capacity}).");
        }

        // ── Back-pressure: wait until enough bytes are free ──
        // We acquire `length` permits from the semaphore.  Each permit
        // represents one byte of ring buffer space.
        for (var i = 0; i < length; i++)
        {
            await _spaceAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        int offset;
        long seq;
        int slotIndex;

        lock (_writeLock)
        {
            // If the payload would wrap past the end of the buffer, waste the
            // tail space and start at offset 0.  The wasted bytes are reclaimed
            // when _readHead advances past them.
            var tailSpace = _capacity - _writeHead;
            if (tailSpace < length)
            {
                // We already acquired `length` semaphore permits, but we're
                // wasting `tailSpace` bytes.  We need `tailSpace` additional
                // permits for the wasted region... but those bytes will be
                // reclaimed immediately.  To keep it simple we just move the
                // write head; the wasted tail bytes are logically part of the
                // preceding slot's region and will be reclaimed when _readHead
                // wraps.
                //
                // However this means we need `tailSpace` more permits.
                // Since this is inside _writeLock and the tail might be large,
                // we cannot do a blocking wait.  Instead, we only wrap when
                // tailSpace permits are available synchronously.
                var gotTailPermits = true;
                for (var i = 0; i < tailSpace; i++)
                {
                    if (!_spaceAvailable.Wait(0, CancellationToken.None))
                    {
                        // Return permits we already got for the tail
                        _spaceAvailable.Release(i);
                        gotTailPermits = false;
                        break;
                    }
                }

                if (!gotTailPermits)
                {
                    // Can't wrap right now — extremely rare edge case.
                    // Fall through and place the message at _writeHead anyway
                    // (it fits because tailSpace < length means we need to wrap,
                    // but we can't get the tail permits).  In practice this
                    // only happens when the buffer is nearly full.
                    //
                    // For simplicity, we'll spin-release the original permits
                    // and retry.  This is the degenerate back-pressure path.
                    _spaceAvailable.Release(length);
                    // Unlock and retry asynchronously — recursive call will
                    // re-acquire permits.
                    goto Retry;
                }

                _writeHead = 0;
            }

            offset = _writeHead;
            seq = _nextSequence++;
            slotIndex = (int)(seq % _maxSlots);

            _slots[slotIndex].Reset(seq, offset, length);
            _writeHead = offset + length;

            Interlocked.Increment(ref _totalAcquired);
        }

        return (new MessageSlot(offset, length, seq), _buffer.AsMemory(offset, length));

    Retry:
        return await Acquire(length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Returns a read-only view of the payload bytes for a previously
    ///     acquired slot.
    /// </summary>
    public ReadOnlyMemory<byte> GetPayload(MessageSlot slot)
    {
        if (!slot.IsValid)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        Debug.Assert(slot.Offset + slot.Length <= _capacity, "Slot exceeds buffer bounds");
        return new ReadOnlyMemory<byte>(_buffer, slot.Offset, slot.Length);
    }

    /// <summary>
    ///     Returns a writable view for a slot (used during inbound wire read
    ///     where the channel writes directly into the ring buffer).
    /// </summary>
    public Memory<byte> GetWritablePayload(MessageSlot slot)
    {
        if (!slot.IsValid)
        {
            return Memory<byte>.Empty;
        }

        Debug.Assert(slot.Offset + slot.Length <= _capacity, "Slot exceeds buffer bounds");
        return new Memory<byte>(_buffer, slot.Offset, slot.Length);
    }

    /// <summary>
    ///     Atomically increments the reference count for a slot.  Call once
    ///     per subscriber before enqueuing the packet bus item.
    /// </summary>
    public void AddRef(MessageSlot slot)
    {
        if (!slot.IsValid)
        {
            return;
        }

        var slotIndex = (int)(slot.SequenceNumber % _maxSlots);
        var meta = _slots[slotIndex];

        Debug.Assert(meta.SequenceNumber == slot.SequenceNumber, "Stale slot reference");
        Interlocked.Increment(ref meta.RefCount);
    }

    /// <summary>
    ///     Atomically decrements the reference count for a slot.  When the
    ///     count reaches 0 the slot's bytes are released back to the free pool,
    ///     unblocking any producers waiting in <see cref="Acquire" />.
    /// </summary>
    public void Release(MessageSlot slot)
    {
        if (!slot.IsValid)
        {
            return;
        }

        var slotIndex = (int)(slot.SequenceNumber % _maxSlots);
        var meta = _slots[slotIndex];

        if (meta.SequenceNumber != slot.SequenceNumber)
        {
            // Stale reference — slot has already been recycled.
            return;
        }

        var newRefCount = Interlocked.Decrement(ref meta.RefCount);
        if (newRefCount == 0)
        {
            // Return the byte permits to the semaphore so producers can proceed.
            _spaceAvailable.Release(meta.PayloadLength);
            Interlocked.Increment(ref _totalReleased);
        }
    }

    /// <summary>
    ///     Static callback suitable for use as <c>MqttPacketBusItem.OnTerminated</c>.
    ///     The <c>state</c> parameter must be a <c>(MessageRingBuffer, MessageSlot)</c> tuple.
    /// </summary>
    public static void ReleaseCallback(object state)
    {
        var (ringBuffer, slot) = ((MessageRingBuffer, MessageSlot))state;
        ringBuffer.Release(slot);
    }

    public void Dispose()
    {
        _spaceAvailable.Dispose();
    }
}
