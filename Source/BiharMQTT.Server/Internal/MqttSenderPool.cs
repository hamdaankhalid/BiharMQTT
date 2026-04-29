// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Collections.Concurrent;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Exceptions;
using BiharMQTT.Internal;

namespace BiharMQTT.Server.Internal;

/*
    Multiplexed sender. M dedicated background threads service N connected
    clients. Each client owns a per-session bus. When a packet is enqueued the
    session invokes its ready-notifier, which calls Schedule(client). At most
    one worker handles a given client at a time (gated by IsScheduledForSend),
    so per-client ordering is preserved without per-client locks beyond the
    channel adapter's send lock.
*/
public sealed class MqttSenderPool : IDisposable
{
    // Drain budget per worker visit. Bounds latency on other ready clients
    // so a single noisy publisher cannot starve the rest behind it.
    const int FairnessCap = 32;

    readonly CancellationTokenSource _cts = new();
    readonly MqttNetSourceLogger _logger;
    readonly ConcurrentQueue<MqttConnectedClient> _readyQueue = new();
    readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    readonly Thread[] _workers;

    int _disposed;

    public MqttSenderPool(int workerCount, IMqttNetLogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger.WithSource(nameof(MqttSenderPool));
        _workers = new Thread[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            var t = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"BiharMQTT-Sender-{i}"
            };
            _workers[i] = t;
            t.Start();
        }
    }

    public int WorkerCount => _workers.Length;

    public void Schedule(MqttConnectedClient client)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        _readyQueue.Enqueue(client);
        _signal.Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }

        // Wake every worker so they can observe cancellation and exit.
        try { _signal.Release(_workers.Length); } catch (ObjectDisposedException) { }

        foreach (var t in _workers)
        {
            // Background threads will be torn down by the runtime on shutdown,
            // but we join briefly so buffers get returned to the pool cleanly.
            t.Join(TimeSpan.FromSeconds(2));
        }

        _cts.Dispose();
        _signal.Dispose();
    }

    void WorkerLoop()
    {
        var ct = _cts.Token;
        // One send buffer per worker thread, sized to the broker's per-message cap.
        // Shared across every client this worker services — only one packet is in
        // flight per worker at a time.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Constants.MaxMemoryPerMessageBytes * 1024);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _signal.Wait(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!_readyQueue.TryDequeue(out var client))
                {
                    continue;
                }

                // Hold the scheduling slot for the entire drain: producers that
                // race with us see flag=1 and *don't* re-enqueue, so no other
                // worker can pop and concurrently drain the same client/session.
                // Concurrent drainers would corrupt MqttPacketBus._activePartition.
                DrainAndSend(client, buffer, ct);

                client.ResetScheduledFlag();

                // Producers that arrived during the drain (flag was 1) added to
                // the bus but did not re-enqueue. We re-claim if anything is
                // pending so those packets get drained on the next round.
                if (!ct.IsCancellationRequested && client.IsRunning && !client.IsTakenOver && client.Session.HasPendingPackets)
                {
                    if (client.TrySetScheduledFlag())
                    {
                        _readyQueue.Enqueue(client);
                        _signal.Release();
                    }
                    // CAS failed: a producer that arrived AFTER our reset has
                    // already claimed the slot and re-enqueued. Nothing to do.
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    void DrainAndSend(MqttConnectedClient client, byte[] buffer, CancellationToken ct)
    {
        for (int i = 0; i < FairnessCap; i++)
        {
            if (ct.IsCancellationRequested || !client.IsRunning || client.IsTakenOver)
            {
                return;
            }

            if (!client.Session.TryDequeuePacket(buffer.AsMemory(), out int bytesWritten) || bytesWritten == 0)
            {
                return;
            }

            try
            {
                client.SendPacket(buffer.AsMemory(0, bytesWritten), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (MqttCommunicationTimedOutException ex)
            {
                _logger.Warning(ex, "Client '{0}': send failed (timeout)", client.Id);
                client.RequestStop();
                return;
            }
            catch (MqttCommunicationException ex)
            {
                _logger.Warning(ex, "Client '{0}': send failed (communication)", client.Id);
                client.RequestStop();
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Client '{0}': send failed", client.Id);
                client.RequestStop();
                return;
            }
        }
    }
}
