// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Collections.Concurrent;

namespace MQTTnet.Server.Internal;

/// <summary>
///     A reference-counted wrapper around a <c>byte[]</c> rented from
///     <see cref="ArrayPool{T}" />. The wrapper itself is pooled via a
///     <see cref="ConcurrentQueue{T}" /> so that, after warm-up, neither the
///     payload buffer nor the tracking object cause heap allocations.
///     <para>
///         Typical usage inside <see cref="MqttClientSessionsManager" />:
///         <list type="number">
///             <item>Call <see cref="RentAndCopy" /> — rents a buffer and memcopies the payload.</item>
///             <item>For each matching subscriber, call <see cref="AddRef" /> before enqueuing
///                   the <c>MqttPacketBusItem</c>.</item>
///             <item>Set <c>busItem.OnTerminated = ReleaseCallback</c> and
///                   <c>busItem.TerminationState = sharedBuffer</c>.</item>
///             <item>After the loop, call <see cref="Release" /> to drop the sentinel reference.</item>
///         </list>
///     </para>
/// </summary>
sealed class SharedPooledPayloadBuffer
{
    static readonly ConcurrentQueue<SharedPooledPayloadBuffer> ObjectPool = new();

    /// <summary>
    ///     Pre-cached delegate for <see cref="Release" />. Because this points at a
    ///     static method the runtime caches the delegate instance — zero allocation.
    /// </summary>
    public static readonly Action<object> ReleaseCallback = Release;

    byte[] _buffer;
    int _length;
    int _refCount;

    /// <summary>
    ///     A <see cref="ReadOnlySequence{T}" /> view over the rented buffer
    ///     trimmed to the actual payload length.
    /// </summary>
    public ReadOnlySequence<byte> Payload => new(_buffer, 0, _length);

    /// <summary>
    ///     Obtains a <see cref="SharedPooledPayloadBuffer" /> (from the object pool when
    ///     possible), rents a <c>byte[]</c> from <see cref="ArrayPool{T}.Shared" />, and
    ///     copies the source payload into it.  The initial reference count is set to 1
    ///     (sentinel) so the buffer is not released before the caller drops the sentinel
    ///     via <see cref="Release" />.
    /// </summary>
    public static SharedPooledPayloadBuffer RentAndCopy(ReadOnlySpan<byte> source)
    {
        if (!ObjectPool.TryDequeue(out var instance))
        {
            instance = new SharedPooledPayloadBuffer();
        }

        instance._buffer = ArrayPool<byte>.Shared.Rent(source.Length);
        instance._length = source.Length;
        source.CopyTo(instance._buffer);
        instance._refCount = 1; // sentinel
        return instance;
    }

    /// <summary>
    ///     Atomically increments the reference count. Call once per subscriber
    ///     before enqueueing the packet bus item.
    /// </summary>
    public void AddRef()
    {
        Interlocked.Increment(ref _refCount);
    }

    /// <summary>
    ///     Atomically decrements the reference count. When the count reaches zero the
    ///     rented <c>byte[]</c> is returned to <see cref="ArrayPool{T}.Shared" /> and
    ///     this instance is returned to the object pool.
    ///     <para>
    ///         This method is designed to be used as the
    ///         <c>MqttPacketBusItem.OnTerminated</c> callback via <see cref="ReleaseCallback" />.
    ///     </para>
    /// </summary>
    public static void Release(object state)
    {
        var self = (SharedPooledPayloadBuffer)state;

        if (Interlocked.Decrement(ref self._refCount) == 0)
        {
            var buffer = self._buffer;
            if (buffer != null)
            {
                self._buffer = null;
                self._length = 0;
                ArrayPool<byte>.Shared.Return(buffer);
            }

            ObjectPool.Enqueue(self);
        }
    }
}
