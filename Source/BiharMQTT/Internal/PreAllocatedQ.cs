using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BiharMQTT.Formatter;

namespace BiharMQTT.Internal;

// Bounded Queue that works by allowing memcopies when enqueuing, and dequeuing
// this makes the MqttPacket
// Enqueue at tail, dequeue at head
// Smallest amount of data we allow allocation for is 8 bytes for alignment purpose
// Think of this as a mixture of an allocator and a bounded Queue class such that the queue manages it's own allocations.
// Also the size of the backing memory of this queue will decide the largest record that can be worked with at the MQTT Layer.
// *******
public sealed unsafe class PreAllocatedQ<T> : IDisposable where T : ICopyable<T>
{
  private const int HeaderSize = sizeof(int);

  private readonly HugeNativeMemoryPool _memoryPool;
  private readonly byte* _backingMemory;
  private readonly int _allocatedBytes;
  private long _tailLogicalAddr;
  private long _headLogicalAddr;
  private int _itemCount;

  private int PhysicalHead => (int)(_headLogicalAddr % _allocatedBytes);
  private int PhysicalTail => (int)(_tailLogicalAddr % _allocatedBytes);

  public bool IsEmpty => _tailLogicalAddr == _headLogicalAddr;
  public int Count => _itemCount;

  private bool disposedValue;

  public PreAllocatedQ(int allocatedMemoryBytes, HugeNativeMemoryPool memoryPool)
  {
    _memoryPool = memoryPool;
    int requestedBytes = RoundUptoDivisibleBy8(allocatedMemoryBytes);
    if (!memoryPool.TryRent(requestedBytes, out IntPtr ptr, out int allocatedSize) || ptr == IntPtr.Zero)
    {
      throw new InvalidOperationException("Unable to rent memory from pool for queue backing storage.");
    }

    if (allocatedSize < requestedBytes)
    {
      throw new InvalidOperationException("Memory pool returned less memory than requested.");
    }

    _allocatedBytes = allocatedSize;
    _backingMemory = (byte*)ptr.ToPointer();
    _tailLogicalAddr = 0;
    _headLogicalAddr = 0;
    _itemCount = 0;
  }

  public void Clear()
  {
    _tailLogicalAddr = 0;
    _headLogicalAddr = 0;
    _itemCount = 0;
  }

  public bool AddLast(ref T data)
  {
    if (data.Length > int.MaxValue - HeaderSize)
    {
      return false;
    }

    // Header stores payload length as a signed int.
    int neededMemory = RoundUptoDivisibleBy8(data.Length + HeaderSize);

    // check if we have enough memory without overwriting the tail
    long bytesUsed = _tailLogicalAddr - _headLogicalAddr;
    long totalBytesAvailableInQ = _allocatedBytes - bytesUsed;
    // needed memory is greater than available memory 
    if (neededMemory > totalBytesAvailableInQ) return false;

    int physicalTail = PhysicalTail;
    long contiguousBytesAvailable = _allocatedBytes - physicalTail;

    // If the tail is near the end and payload does not fit contiguously, write a negative skip marker and wrap.
    if (neededMemory > contiguousBytesAvailable)
    {
      if (neededMemory > (totalBytesAvailableInQ - contiguousBytesAvailable))
      {
        return false;
      }

      if (contiguousBytesAvailable > 0)
      {
        *(int*)(_backingMemory + physicalTail) = (int)(-contiguousBytesAvailable);
        _tailLogicalAddr += contiguousBytesAvailable;
      }

      physicalTail = PhysicalTail;
    }

    // take the tail address and allocate neededMemory from it
    // write data length header
    *(int*)(_backingMemory + physicalTail) = data.Length;
    // write data
    data.CopyTo(new Span<byte>(_backingMemory + physicalTail + HeaderSize, data.Length));
    // move data by total bytes written
    _tailLogicalAddr += neededMemory;
    _itemCount++;
    return true;
  }

  // Reads the first entry into the dest span and frees it
  public bool TryRemoveFirst(Span<byte> dest, out int bytesWritten)
  {
    bytesWritten = 0;

    if (IsEmpty) return false;

    byte* headAddr = _backingMemory + PhysicalHead;
    int dataLen;
    // check if _headLogicalAddr is at a fragmented end.
    // we know an end is fragmented if we have a negative number. we store the amount of bytes that were fragmented here
    dataLen = *(int*)headAddr;
    while (dataLen < 0)
    {
      int fragmentedBytes = -dataLen;
      if (fragmentedBytes == 0)
      {
        return false;
      }

      // Lazily move the logical addr and recalculate head addr after accounting for fragmented bits
      _headLogicalAddr += fragmentedBytes;

      if (IsEmpty)
      {
        return false;
      }

      headAddr = _backingMemory + PhysicalHead;
      dataLen = *(int*)headAddr;
    }

    if (dataLen < 0 || dest.Length < dataLen)
    {
      return false;
    }

    new Span<byte>(headAddr + HeaderSize, dataLen).CopyTo(dest);

    bytesWritten = dataLen;
    // physical memory consumed here does not include the fragmented bytes.
    // we already accounted for moving _headLogicalAddr forward
    int physicalMemoryConsumed = RoundUptoDivisibleBy8(dataLen + HeaderSize);
    _headLogicalAddr += physicalMemoryConsumed;
    _itemCount--;
    return true;
  }

  public void DropFirstItem()
  {
    if (IsEmpty) return;

    // cool just move the head forward by the size of the first item, while accomodating fragmented bits
    byte* headAddr = _backingMemory + PhysicalHead;
    int dataLen = *(int*)headAddr;
    while (dataLen < 0)
    {
      int fragmentedBytes = -dataLen;
      if (fragmentedBytes == 0)
      {
        return;
      }

      _headLogicalAddr += fragmentedBytes;

      if (IsEmpty)
      {
        return;
      }

      headAddr = _backingMemory + PhysicalHead;
      dataLen = *(int*)headAddr;
    }

    int physicalMemoryConsumed = RoundUptoDivisibleBy8(dataLen + HeaderSize);
    _headLogicalAddr += physicalMemoryConsumed;
    _itemCount--;
  }

  private static int RoundUptoDivisibleBy8(int num) => (num + 7) & ~7;

  private void Dispose(bool disposing)
  {
    if (!disposedValue)
    {
      if (_backingMemory != null)
      {
        if (disposing)
        {
          _memoryPool.Return((nint)_backingMemory, _allocatedBytes);
        }
        else
        {
          NativeMemory.Free(_backingMemory);
        }
      }

      // TODO: set large fields to null
      disposedValue = true;
    }
  }

  ~PreAllocatedQ()
  {
    // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    Dispose(disposing: false);
  }

  public void Dispose()
  {
    // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    Dispose(disposing: true);
    GC.SuppressFinalize(this);
  }

}