// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Server.Internal;

/// <summary>
///     Per-slot metadata stored in a parallel array alongside the
///     <see cref="MessageRingBuffer" /> payload data.  Each entry corresponds
///     to one in-flight message and is recycled when the slot's reference
///     count reaches zero.
/// </summary>
sealed class MessageSlotMetadata
{
    /// <summary>
    ///     Atomic reference count.  Starts at 1 (sentinel for the dispatch caller);
    ///     incremented once per subscriber; decremented when each consumer finishes.
    ///     When it reaches 0 the slot is reclaimable.
    /// </summary>
    public int RefCount;

    /// <summary>
    ///     The sequence number this metadata entry was last used for.
    ///     Used to detect stale slot references (when a slot has been recycled
    ///     since the reference was created).
    /// </summary>
    public long SequenceNumber;

    /// <summary>
    ///     Byte offset in the ring buffer (redundant with <see cref="MessageSlot.Offset" />
    ///     but needed for the reclaim scan).
    /// </summary>
    public int Offset;

    /// <summary>
    ///     Payload byte length (redundant with <see cref="MessageSlot.Length" />
    ///     but needed for the reclaim scan).
    /// </summary>
    public int PayloadLength;

    public void Reset(long sequenceNumber, int offset, int payloadLength)
    {
        SequenceNumber = sequenceNumber;
        Offset = offset;
        PayloadLength = payloadLength;
        RefCount = 1; // sentinel
    }
}
