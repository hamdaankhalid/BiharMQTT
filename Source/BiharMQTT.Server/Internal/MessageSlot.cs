// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Server.Internal;

/// <summary>
///     A lightweight value-type handle that identifies a region of payload bytes
///     inside a <see cref="MessageRingBuffer" />.  Passed by value through the
///     publish pipeline; all downstream consumers reference the same ring buffer
///     region without copying.
/// </summary>
public readonly struct MessageSlot
{
    /// <summary>
    ///     A sentinel value indicating "no slot" (e.g. empty payload or ring buffer not in use).
    /// </summary>
    public static readonly MessageSlot Empty;

    public MessageSlot(int offset, int length, long sequenceNumber)
    {
        Offset = offset;
        Length = length;
        SequenceNumber = sequenceNumber;
    }

    /// <summary>
    ///     Byte offset within the ring buffer where this message's payload starts.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    ///     Length in bytes of the payload region.
    /// </summary>
    public int Length { get; }

    /// <summary>
    ///     Monotonically increasing sequence number assigned at acquire time.
    ///     Used to index into the metadata array (modulo max slots) and to
    ///     detect stale slot references.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    ///     Returns <c>true</c> if this slot represents a valid ring buffer region.
    /// </summary>
    public bool IsValid => Length > 0;
}
