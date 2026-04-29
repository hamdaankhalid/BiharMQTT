// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Internal;
using System.Buffers;

namespace BiharMQTT.Formatter;

public readonly struct MqttPacketBuffer : ICopyable<MqttPacketBuffer>
{
    public MqttPacketBuffer(ArraySegment<byte> packet, ReadOnlySequence<byte> payload)
    {
        Packet = packet;
        Payload = payload;
        Length = Packet.Count + (int)Payload.Length;
    }

    public MqttPacketBuffer(ArraySegment<byte> packet)
    {
        Packet = packet;
        Payload = EmptyBuffer.ReadOnlySequence;
        Length = Packet.Count;
    }

    public int Length { get; }

    public ArraySegment<byte> Packet { get; }

    public ReadOnlySequence<byte> Payload { get; }

    public void CopyHeaderTo(Span<byte> destination)
    {
        if (destination.Length < Packet.Count) throw new ArgumentException("Destination span is too small for header", nameof(destination));
        Packet.AsSpan().CopyTo(destination);
    }

    public void CopyPayloadTo(Span<byte> destination)
    {
        if (Payload.Length == 0) return;
        if (destination.Length < (int)Payload.Length) throw new ArgumentException("Destination span is too small for payload", nameof(destination));
        Payload.CopyTo(destination);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length) throw new ArgumentException("Destination span is too small for packet", nameof(destination));
        CopyHeaderTo(destination);
        CopyPayloadTo(destination.Slice(Packet.Count));
    }

    public byte[] ToArray()
    {
        if (Payload.Length == 0)
        {
            return Packet.ToArray();
        }

        var buffer = GC.AllocateUninitializedArray<byte>(Length);
        MqttMemoryHelper.Copy(Packet.Array, Packet.Offset, buffer, 0, Packet.Count);
        MqttMemoryHelper.Copy(Payload, 0, buffer, Packet.Count, (int)Payload.Length);

        return buffer;
    }

    public ArraySegment<byte> Join()
    {
        if (Payload.Length == 0)
        {
            return Packet;
        }

        return new ArraySegment<byte>(ToArray());
    }
}

public interface ICopyable<T> where T : ICopyable<T>
{
    int Length { get; }
    void CopyTo(Span<byte> destination);
    void CopyHeaderTo(Span<byte> destination);
    void CopyPayloadTo(Span<byte> destination);
}