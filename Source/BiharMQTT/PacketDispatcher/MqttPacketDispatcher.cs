// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Adapter;
using BiharMQTT.Protocol;

namespace BiharMQTT.PacketDispatcher;

public sealed class MqttPacketDispatcher : IDisposable
{
    readonly List<IMqttPacketAwaitable> _waiters = [];

    bool _isDisposed;

    public MqttPacketAwaitable AddAwaitable(MqttControlPacketType packetType, ushort packetIdentifier = 0)
    {
        var awaitable = new MqttPacketAwaitable(packetType, packetIdentifier, this);

        lock (_waiters)
        {
            _waiters.Add(awaitable);
        }

        return awaitable;
    }

    public void CancelAll()
    {
        lock (_waiters)
        {
            foreach (var awaitable in _waiters)
            {
                awaitable.Cancel();
            }

            _waiters.Clear();
        }
    }

    public void Dispose()
    {
        Dispose(new ObjectDisposedException(nameof(MqttPacketDispatcher)));
    }

    public void Dispose(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_waiters)
        {
            FailAll(exception);

            // Make sure that no task can start waiting after this instance is already disposed.
            // This will prevent unexpected freezes.
            _isDisposed = true;
        }
    }

    public void FailAll(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_waiters)
        {
            foreach (var awaitable in _waiters)
            {
                awaitable.Fail(exception);
            }

            _waiters.Clear();
        }
    }

    public void RemoveAwaitable(IMqttPacketAwaitable awaitable)
    {
        ArgumentNullException.ThrowIfNull(awaitable);

        lock (_waiters)
        {
            _waiters.Remove(awaitable);
        }
    }

    /// <summary>
    ///     Dispatches a received packet to any matching waiters based on packet type and identifier.
    ///     The packet type is extracted from the upper 4 bits of the fixed header byte.
    /// </summary>
    public bool TryDispatch(MqttControlPacketType packetType, ushort packetIdentifier, ReceivedMqttPacket packet)
    {
        var waiters = new List<IMqttPacketAwaitable>();

        lock (_waiters)
        {
            ThrowIfDisposed();

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var entry = _waiters[i];

                // Note: The PingRespPacket will also arrive here and has NO identifier but there
                // is code which waits for it. So the code must be able to deal with filters which
                // are referring to the type only (identifier is 0)!
                if (entry.Filter.PacketType != packetType || entry.Filter.Identifier != packetIdentifier)
                {
                    continue;
                }

                waiters.Add(entry);
                _waiters.RemoveAt(i);
            }
        }

        foreach (var matchingEntry in waiters)
        {
            matchingEntry.Complete(packet);
        }

        return waiters.Count > 0;
    }

    void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}