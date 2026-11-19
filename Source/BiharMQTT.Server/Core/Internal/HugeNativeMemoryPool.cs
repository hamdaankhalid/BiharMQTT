using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BiharMQTT.Internal;

// we can use pinned buffers to preallocate mqtt packet buses
public unsafe class HugeNativeMemoryPool : IDisposable
{
  private readonly (ConcurrentBag<IntPtr> arrs, int bucketSize)[] _buckets;
  private bool disposedValue;

  public HugeNativeMemoryPool((uint bucketSize, int minNumBuckets)[] buckets)
  {
    Array.Sort(buckets, (a, b) => a.bucketSize.CompareTo(b.bucketSize));
    _buckets = new (ConcurrentBag<IntPtr>, int)[buckets.Length];
    for (int i = 0; i < buckets.Length; i++)
    {
      (uint bucketSize, int minNumBuckets) = buckets[i];
      ConcurrentBag<IntPtr> nints = new ConcurrentBag<nint>();
      for (int j = 0; j < minNumBuckets; j++)
      {
        void* data = NativeMemory.Alloc(bucketSize);
        nints.Add(new IntPtr(data));
      }
      _buckets[i] = (nints, (int)bucketSize);
    }
  }

  public bool TryRent(int size, out IntPtr puttar, out int allocatedSize)
  {
    allocatedSize = 0;
    puttar = default;
    for (int i = 0; i < _buckets.Length; i++)
    {
      (ConcurrentBag<nint> arrs, int bucketSize) = _buckets[i];
      if (bucketSize >= size)
      {
        if (arrs.TryTake(out puttar))
        {
          allocatedSize = bucketSize;
          return true;
        }
        // On purpose we are not allowing fallback allocation to larger bucket
        return false;
      }
    }
    return false;
  }

  public void Return(IntPtr puttar, int allocatedSize)
  {
    for (int i = 0; i < _buckets.Length; i++)
    {
      (ConcurrentBag<nint> arrs, int bucketSize) = _buckets[i];
      if (bucketSize == allocatedSize)
      {
        arrs.Add(puttar);
      }
    }
  }

  protected virtual void Dispose(bool disposing)
  {
    if (!disposedValue)
    {
      if (disposing)
      {
        // TODO: dispose managed state (managed objects)
      }

      // Free the allocated memory
      foreach (var buck in _buckets)
      {
        ConcurrentBag<nint> bag = buck.arrs;
        while (bag.TryTake(out nint ptr))
        {
          NativeMemory.Free((void*)ptr);
        }
      }

      // TODO: free unmanaged resources (unmanaged objects) and override finalizer
      // TODO: set large fields to null
      disposedValue = true;
    }
  }

  ~HugeNativeMemoryPool()
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