// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Exceptions;
using BiharMQTT.Formatter;
using BiharMQTT;
using BiharMQTT.Internal;

namespace BiharMQTT.Adapter;

/// <summary>
/// Invoked when BeginReceivePacket completes. Exactly one of (packet, error)
/// is meaningful: error is non-null on failure. <see cref="ReceivedMqttPacket.Empty"/>
/// signals a clean half-close.
/// </summary>
public delegate void ReceivePacketCompletionHandler(ReceivedMqttPacket packet, Exception error);

public sealed class MqttChannelAdapter : Disposable
{
    const uint ErrorOperationAborted = 0x800703E3;
    const int ReadBufferSize = 4096;

    // Loopback / pre-buffered data can complete every Socket.ReceiveAsync
    // synchronously — chained recursion would blow the stack on large packets.
    // Past this depth we hand the next receive to the thread pool to break the chain.
    const int MaxSyncCompletionChain = 16;

    [ThreadStatic] static int _syncCompletionDepth;

    readonly MqttTcpChannel _channel;
    readonly byte[] _fixedHeaderBuffer = new byte[2];
    readonly MqttNetSourceLogger _logger;
    readonly object _syncRoot = new();

    // Per-connection reusable body buffer rented from ArrayPool. Grows to the
    // high-water mark of incoming packet sizes and is reused for every subsequent
    // packet on this connection. Safe because the receive loop is sequential per
    // connection. Returned to the pool on Dispose; on growth the prior rental
    // is returned before re-renting.
    byte[] _reusableBodyBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

    Statistics _statistics; // mutable struct, don't make readonly!

    // ----- Receive state machine fields. Single in-flight receive at a time, so
    // these are not synchronized. The hot path mutates them inline and re-arms
    // the channel; cancellation aborts via channel disposal. -----

    ReceivePacketCompletionHandler _packetCallback;
    // Cached delegates point into instance methods so the per-receive BeginReceive
    // call passes them without a closure allocation.
    readonly ReceiveCompletionHandler _onFixedHeaderRead;
    readonly ReceiveCompletionHandler _onRemainingLengthRead;
    readonly ReceiveCompletionHandler _onBodyRead;
    readonly Action _beginReadFixedHeader;
    readonly Action _beginReadRemainingLength;
    readonly Action _beginReadBody;

    int _fhTotalRead;
    int _rlMultiplier;
    int _rlValue;
    int _rlOffset;
    byte _flags;
    int _bodyLength;
    int _bodyOffset;
    int _totalReceivedHeaderBytes;

    public MqttChannelAdapter(MqttTcpChannel channel, MqttPacketFormatterAdapter packetFormatterAdapter, IMqttNetLogger logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));

        PacketFormatterAdapter = packetFormatterAdapter ?? throw new ArgumentNullException(nameof(packetFormatterAdapter));

        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger.WithSource(nameof(MqttChannelAdapter));

        _onFixedHeaderRead = OnFixedHeaderRead;
        _onRemainingLengthRead = OnRemainingLengthRead;
        _onBodyRead = OnBodyRead;
        _beginReadFixedHeader = BeginReadFixedHeader;
        _beginReadRemainingLength = BeginReadRemainingLength;
        _beginReadBody = BeginReadBody;
    }

    public bool AllowPacketFragmentation { get; set; } = true;

    public long BytesReceived => Volatile.Read(ref _statistics._bytesReceived);

    public long BytesSent => Volatile.Read(ref _statistics._bytesSent);

    public X509Certificate2 ClientCertificate => _channel.ClientCertificate;

    public EndPoint RemoteEndPoint => _channel.RemoteEndPoint;

    public EndPoint LocalEndPoint => _channel.LocalEndPoint;

    public bool IsSecureConnection => _channel.IsSecureConnection;

    public MqttPacketFormatterAdapter PacketFormatterAdapter { get; }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Begin receiving the next MQTT packet from the channel. The callback is
    /// invoked exactly once when a full packet has been read, the peer closed
    /// the connection (delivered as <see cref="ReceivedMqttPacket.Empty"/>),
    /// or an error occurred. Caller must not issue another BeginReceivePacket
    /// until the callback fires.
    /// </summary>
    public void BeginReceivePacket(ReceivePacketCompletionHandler callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ThrowIfDisposed();

        _packetCallback = callback;

        ResetReceiveState();
        BeginReadFixedHeader();
    }

    /// <summary>
    /// Task-shaped wrapper around <see cref="BeginReceivePacket"/>. Used for
    /// the connect handshake where we read exactly one packet and want
    /// CancellationToken-driven timeout. Allocates a TCS per call — fine for
    /// the cold path, hot loop should use BeginReceivePacket directly.
    /// </summary>
    public Task<ReceivedMqttPacket> ReceivePacketAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var tcs = new TaskCompletionSource<ReceivedMqttPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Cancellation aborts by closing the channel — pending Receive completes
        // with OperationAborted, which we surface as Empty.
        CancellationTokenRegistration ctr = default;
        if (cancellationToken.CanBeCanceled)
        {
            ctr = cancellationToken.Register(static state =>
            {
                var self = (MqttChannelAdapter)state;
                try { self.Dispose(); } catch (ObjectDisposedException) { }
            }, this);
        }

        try
        {
            BeginReceivePacket((packet, error) =>
            {
                ctr.Dispose();

                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetResult(ReceivedMqttPacket.Empty);
                    return;
                }

                if (error != null)
                {
                    if (TryClassifyAsClean(error))
                    {
                        tcs.TrySetResult(ReceivedMqttPacket.Empty);
                    }
                    else
                    {
                        tcs.TrySetException(WrapException(error));
                    }
                    return;
                }

                tcs.TrySetResult(packet);
            });
        }
        catch (Exception ex)
        {
            ctr.Dispose();
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }

    public void ResetStatistics()
    {
        _statistics.Reset();
    }


    // Cold-path send for packets that come out of the encoder as
    // (header, payload) pairs (CONNACK during handshake, DISCONNECT on shutdown).
    // The hot path uses the inlined-buffer overload below.
    public void SendPacket(Memory<byte> header, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // Serialize writes against the hot-path SendPacketsLoop so a disconnect
        // sent from another thread cannot interleave bytes with an in-flight publish.
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _channel.Write(header);
                foreach (ReadOnlyMemory<byte> segment in payload)
                {
                    _channel.Write(segment);
                }
                Interlocked.Add(ref _statistics._bytesReceived, header.Length + payload.Length);
            }
            catch (Exception exception)
            {
                if (!WrapAndThrowException(exception))
                {
                    throw;
                }
            }
            finally
            {
                PacketFormatterAdapter.Cleanup();
            }
        }
    }

    // Inline-send fast path. Called by producers (Session enqueue) before they
    // hand a packet to the bus + sender pool. Succeeds only when:
    //   1) the channel send-lock is uncontended (TryEnter),
    //   2) the bus is empty — otherwise we'd skip past queued packets and break FIFO,
    //   3) the kernel send buffer has room (Socket.Poll SelectWrite),
    //   4) the packet fits in InlineMaxBytes — bigger goes through the queue so a
    //      large slow write doesn't pin the producer's thread.
    // On success, bytes are on the wire before this returns and the bus is bypassed.
    // Any failure (lock contention, non-empty bus, full kernel buffer, oversized
    // packet, write exception) returns false and the caller queues normally.
    const int InlineMaxBytes = 16 * 1024;

    public bool TrySendInline(ref MqttPacketBuffer buffer, MqttPacketBus bus)
    {
        if (IsDisposed) return false;
        if (buffer.Length > InlineMaxBytes) return false;
        if (!_channel.IsWritable()) return false;

        if (!Monitor.TryEnter(_syncRoot)) return false;
        try
        {
            // Re-check inside the lock: while we were entering, the worker may have
            // started/finished a drain or another producer queued.
            if (!bus.IsEmpty) return false;
            if (!_channel.IsWritable()) return false;

            try
            {
                _channel.Write(buffer.Packet);
                foreach (ReadOnlyMemory<byte> segment in buffer.Payload)
                {
                    _channel.Write(segment);
                }
                Interlocked.Add(ref _statistics._bytesReceived, buffer.Length);
                return true;
            }
            catch (Exception ex)
            {
                // Any send exception — fall back to queue path so the worker's
                // standard error handling logs and tears down the connection.
                _logger.Debug("TrySendInline write failed, falling back to queue (Remote={0}, Bytes={1}, Ex={2})",
                    RemoteEndPoint, buffer.Length, ex.GetType().Name);
                return false;
            }
        }
        finally
        {
            Monitor.Exit(_syncRoot);
        }
    }

    public void SendPacket(Memory<byte> fullPacketInlined, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // Serialize writes against the cold-path overload above.
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // All our packet data is inlined in our Memory<byte> buffer so we can write it in a single call
                _channel.Write(fullPacketInlined);
                Interlocked.Add(ref _statistics._bytesReceived, fullPacketInlined.Length);
            }
            catch (Exception exception)
            {
                if (!WrapAndThrowException(exception))
                {
                    throw;
                }
            }
            finally
            {
                PacketFormatterAdapter.Cleanup();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _channel.Dispose();
            ArrayPool<byte>.Shared.Return(_reusableBodyBuffer);
            PacketFormatterAdapter.Dispose();
        }

        base.Dispose(disposing);
    }

    void ResetReceiveState()
    {
        _fhTotalRead = 0;
        _rlMultiplier = 128;
        _rlValue = 0;
        _rlOffset = 0;
        _flags = 0;
        _bodyLength = 0;
        _bodyOffset = 0;
        _totalReceivedHeaderBytes = 0;
    }

    void BeginReadFixedHeader()
    {
        try
        {
            _channel.BeginReceive(_fixedHeaderBuffer.AsMemory(_fhTotalRead, 2 - _fhTotalRead), _onFixedHeaderRead);
        }
        catch (Exception ex)
        {
            DeliverError(ex);
        }
    }

    void OnFixedHeaderRead(int bytesRead, Exception error)
    {
        if (error != null) { DeliverError(error); return; }
        if (bytesRead == 0)
        {
            if (_fhTotalRead > 0)
            {
                // Anomaly: peer half-closed mid-packet. We had byte 0 of the fixed
                // header but never got byte 1 — that's not a clean disconnect.
                _logger.Debug("Peer closed mid-fixed-header (Remote={0}, FhBytes={1})", RemoteEndPoint, _fhTotalRead);
            }
            DeliverEmpty();
            return;
        }

        _fhTotalRead += bytesRead;
        if (_fhTotalRead < 2)
        {
            ContinueOrPunt(_beginReadFixedHeader);
            return;
        }

        _flags = _fixedHeaderBuffer[0];
        _totalReceivedHeaderBytes = 2;

        // Fast path: top bit of the second byte clear means the remaining-length
        // is a single byte and we already have it.
        if ((_fixedHeaderBuffer[1] & 0x80) == 0)
        {
            _bodyLength = _fixedHeaderBuffer[1];
            ProceedToBodyOrFinish();
            return;
        }

        _rlValue = _fixedHeaderBuffer[1] & 0x7F;
        ContinueOrPunt(_beginReadRemainingLength);
    }

    void BeginReadRemainingLength()
    {
        try
        {
            // Reuse byte 1 of the fixed-header buffer as a single-byte scratch
            // slot — safe because we've already extracted what we needed from it.
            _channel.BeginReceive(_fixedHeaderBuffer.AsMemory(1, 1), _onRemainingLengthRead);
        }
        catch (Exception ex)
        {
            DeliverError(ex);
        }
    }

    void OnRemainingLengthRead(int bytesRead, Exception error)
    {
        if (error != null) { DeliverError(error); return; }
        if (bytesRead == 0) { DeliverEmpty(); return; }

        _rlOffset++;
        if (_rlOffset > 3)
        {
            _logger.Debug("Invalid remaining-length encoding (Remote={0}, Flags=0x{1:X2}, Bytes>4)", RemoteEndPoint, _flags);
            DeliverError(new MqttProtocolViolationException("Remaining length is invalid."));
            return;
        }

        var encoded = _fixedHeaderBuffer[1];
        _rlValue += (encoded & 0x7F) * _rlMultiplier;
        _rlMultiplier *= 128;
        _totalReceivedHeaderBytes++;

        if ((encoded & 0x80) != 0)
        {
            ContinueOrPunt(_beginReadRemainingLength);
            return;
        }

        _bodyLength = _rlValue;
        ProceedToBodyOrFinish();
    }

    void ProceedToBodyOrFinish()
    {
        if (_bodyLength == 0)
        {
            DeliverPacket(new ReceivedMqttPacket(_flags, EmptyBuffer.ArraySegment, _totalReceivedHeaderBytes));
            return;
        }

        // Grow the reusable body buffer to fit; on growth the prior rental is
        // returned to the pool before we rent the larger one.
        if (_reusableBodyBuffer.Length < _bodyLength)
        {
            ArrayPool<byte>.Shared.Return(_reusableBodyBuffer);
            _reusableBodyBuffer = ArrayPool<byte>.Shared.Rent(_bodyLength);
        }

        ContinueOrPunt(_beginReadBody);
    }

    void BeginReadBody()
    {
        try
        {
            // Bound by *this packet's* remaining bytes, not buffer capacity:
            // the reused buffer can be larger after a previous big packet, and
            // over-reading would eat bytes from the next packet and desync.
            var chunkSize = Math.Min(ReadBufferSize, _bodyLength - _bodyOffset);
            _channel.BeginReceive(_reusableBodyBuffer.AsMemory(_bodyOffset, chunkSize), _onBodyRead);
        }
        catch (Exception ex)
        {
            DeliverError(ex);
        }
    }

    void OnBodyRead(int bytesRead, Exception error)
    {
        if (error != null) { DeliverError(error); return; }
        if (bytesRead == 0)
        {
            // Anomaly: had a fixed header (so peer committed to a packet) but
            // never finished sending the body. Track how much we got vs expected.
            _logger.Debug("Peer closed mid-body (Remote={0}, Flags=0x{1:X2}, BodyOffset={2}, BodyLength={3})",
                RemoteEndPoint, _flags, _bodyOffset, _bodyLength);
            DeliverEmpty();
            return;
        }

        _bodyOffset += bytesRead;
        if (_bodyOffset < _bodyLength)
        {
            ContinueOrPunt(_beginReadBody);
            return;
        }

        var bodySegment = new ArraySegment<byte>(_reusableBodyBuffer, 0, _bodyLength);
        DeliverPacket(new ReceivedMqttPacket(_flags, bodySegment, _totalReceivedHeaderBytes + _bodyLength));
    }

    // Recurse for sync completions but break the stack chain past the threshold.
    // Keeps the hot path on the calling thread (cache-friendly) while still
    // bounded against pathological loopback streams.
    static void ContinueOrPunt(Action next)
    {
        if (_syncCompletionDepth < MaxSyncCompletionChain)
        {
            _syncCompletionDepth++;
            try { next(); }
            finally { _syncCompletionDepth--; }
        }
        else
        {
            // Hand off to the thread pool so we unwind the current stack.
            ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state)(), next);
        }
    }

    void DeliverPacket(ReceivedMqttPacket packet)
    {
        Interlocked.Add(ref _statistics._bytesSent, packet.TotalLength);

        if (PacketFormatterAdapter.ProtocolVersion == MqttProtocolVersion.Unknown)
        {
            PacketFormatterAdapter.DetectProtocolVersion(ref packet);
        }

        _logger.Debug("DeliverPacket (Remote={0}, Flags=0x{1:X2}, Body={2}, Total={3})",
            RemoteEndPoint, packet.FixedHeader, packet.Body.Count, packet.TotalLength);

        var cb = _packetCallback;
        _packetCallback = null;
        cb?.Invoke(packet, null);
    }

    void DeliverEmpty()
    {
        var cb = _packetCallback;
        _packetCallback = null;
        cb?.Invoke(ReceivedMqttPacket.Empty, null);
    }

    void DeliverError(Exception ex)
    {
        // Central funnel for every receive/decode anomaly. Logging once here
        // beats instrumenting every throw site in the decoder.
        _logger.Debug("DeliverError (Remote={0}, Ex={1}, FhBytes={2}, BodyOffset={3}, BodyLength={4}, Flags=0x{5:X2})",
            RemoteEndPoint, ex.GetType().Name, _fhTotalRead, _bodyOffset, _bodyLength, _flags);

        var cb = _packetCallback;
        _packetCallback = null;
        cb?.Invoke(ReceivedMqttPacket.Empty, ex);
    }

    static bool TryClassifyAsClean(Exception ex)
    {
        // Treat clean shutdowns and aborts as empty rather than exceptions so
        // the caller's normal "TotalLength == 0" path runs.
        if (ex is OperationCanceledException || ex is ObjectDisposedException) return true;

        if (ex is SocketException se)
        {
            return se.SocketErrorCode == SocketError.OperationAborted
                || se.SocketErrorCode == SocketError.ConnectionAborted
                || se.SocketErrorCode == SocketError.ConnectionReset;
        }

        return false;
    }

    static Exception WrapException(Exception exception)
    {
        if (exception is MqttCommunicationException
            || exception is MqttProtocolViolationException
            || exception is OperationCanceledException
            || exception is MqttCommunicationTimedOutException)
        {
            return exception;
        }

        if (exception is IOException && exception.InnerException is SocketException innerException)
        {
            exception = innerException;
        }

        if (exception is SocketException socketException)
        {
            if (socketException.SocketErrorCode == SocketError.OperationAborted)
            {
                return new OperationCanceledException();
            }

            if (socketException.SocketErrorCode == SocketError.ConnectionAborted)
            {
                return new MqttCommunicationException(socketException);
            }
        }

        if (exception is COMException comException && (uint)comException.HResult == ErrorOperationAborted)
        {
            return new OperationCanceledException();
        }

        return new MqttCommunicationException(exception);
    }

    static bool WrapAndThrowException(Exception exception)
    {
        if (exception is OperationCanceledException || exception is MqttCommunicationTimedOutException || exception is MqttCommunicationException ||
            exception is MqttProtocolViolationException)
        {
            return false;
        }

        if (exception is IOException && exception.InnerException is SocketException innerException)
        {
            exception = innerException;
        }

        if (exception is SocketException socketException)
        {
            if (socketException.SocketErrorCode == SocketError.OperationAborted)
            {
                throw new OperationCanceledException();
            }

            if (socketException.SocketErrorCode == SocketError.ConnectionAborted)
            {
                throw new MqttCommunicationException(socketException);
            }
        }

        if (exception is COMException comException)
        {
            if ((uint)comException.HResult == ErrorOperationAborted)
            {
                throw new OperationCanceledException();
            }
        }

        throw new MqttCommunicationException(exception);
    }

    struct Statistics
    {
        public long _bytesReceived;
        public long _bytesSent;

        public void Reset()
        {
            Volatile.Write(ref _bytesReceived, 0);
            Volatile.Write(ref _bytesSent, 0);
        }
    }
}
