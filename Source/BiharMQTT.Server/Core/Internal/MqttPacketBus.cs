// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Formatter;

namespace BiharMQTT.Internal;

public sealed class MqttPacketBus : IDisposable
{
    readonly HugeNativeMemoryPool _memoryPool;
    readonly PreAllocatedQ<MqttPacketBuffer>[] _partitions;
    // lock per partition
    readonly object[] _locks;

    int _activePartition = (int)MqttPacketBusPartition.Health;

    public int TotalItemsCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _partitions.Length; i++)
            {
                lock (_locks[i])
                {
                    count += _partitions[i].Count;
                }
            }
            return count;
        }
    }

    public bool IsEmpty => TotalItemsCount == 0;

    public MqttPacketBus(HugeNativeMemoryPool memoryPool)
    {
        _memoryPool = memoryPool;
        _partitions = new PreAllocatedQ<MqttPacketBuffer>[3];
        _locks = new object[3];
        for (int i = 0; i < _partitions.Length; i++)
        {
            _partitions[i] = new PreAllocatedQ<MqttPacketBuffer>(Constants.PreAllocatedQMemoryKb * 1024, _memoryPool);
            _locks[i] = new();
        }
    }

    public void Clear()
    {
        for (var i = 0; i < _partitions.Length; i++)
        {
            lock (_locks[i]) { _partitions[i].Clear(); }
        }
    }

    // Non-blocking dequeue. Round-robins partitions for fairness across
    // Health/Control/Data so a flood of data packets cannot starve health.
    // Caller is the multiplexed sender pool, which signals readiness via a
    // separate per-client gate, so this method does not park anywhere.
    public bool TryDequeueItem(Memory<byte> dest, out int bytesWritten)
    {
        bytesWritten = 0;

        for (var i = 0; i < _partitions.Length; i++)
        {
            MoveActivePartition();

            PreAllocatedQ<MqttPacketBuffer> activePartition = _partitions[_activePartition];
            lock (_locks[_activePartition])
            {
                if (activePartition.TryRemoveFirst(dest.Span, out bytesWritten))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var partition in _partitions)
        {
            partition.Clear();
            partition.Dispose();
        }
    }

    public void DropFirstItem(MqttPacketBusPartition partition)
    {
        lock (_locks[(int)partition])
        {
            _partitions[(int)partition].DropFirstItem();
        }
    }

    public void EnqueueItem(ref MqttPacketBuffer item, MqttPacketBusPartition partition)
    {
        // here I want to provide a single Span down into AddLast to avoid allocations
        lock (_locks[(int)partition])
        {
            _partitions[(int)partition].AddLast(ref item);
        }
    }

    public int PartitionItemsCount(MqttPacketBusPartition partition)
    {
        lock (_locks[(int)partition])
        {
            return _partitions[(int)partition].Count;
        }
    }

    void MoveActivePartition()
    {
        if (_activePartition >= _partitions.Length - 1)
        {
            _activePartition = 0;
        }
        else
        {
            _activePartition++;
        }
    }
}
