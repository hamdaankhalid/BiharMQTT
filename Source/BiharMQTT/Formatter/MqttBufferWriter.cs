// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using BiharMQTT.Exceptions;
using BiharMQTT.Internal;
using BiharMQTT.Protocol;

namespace BiharMQTT.Formatter;

public sealed class MqttBufferWriter : IDisposable
{
    const int EncodedStringMaxLength = 80;

    byte[] _buffer;
    int _position;
    private bool disposedValue;


    public MqttBufferWriter(int bufferSize)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    public int Length { get; private set; }

    public static byte BuildFixedHeader(MqttControlPacketType packetType, byte flags = 0)
    {
        var fixedHeader = (int)packetType << 4;
        fixedHeader |= flags;
        return (byte)fixedHeader;
    }

    public void Cleanup()
    {
        Array.Clear(_buffer);
    }

    public byte[] GetBuffer()
    {
        return _buffer;
    }

    public static int GetVariableByteIntegerSize(uint value)
    {
        // From RFC: Table 2.4 Size of Remaining Length field

        // 0 (0x00) to 127 (0x7F)
        if (value <= 127)
        {
            return 1;
        }

        // 128 (0x80, 0x01) to 16 383 (0xFF, 0x7F)
        if (value <= 16383)
        {
            return 2;
        }

        // 16 384 (0x80, 0x80, 0x01) to 2 097 151 (0xFF, 0xFF, 0x7F)
        if (value <= 2097151)
        {
            return 3;
        }

        // 2 097 152 (0x80, 0x80, 0x80, 0x01) to 268 435 455 (0xFF, 0xFF, 0xFF, 0x7F)
        return 4;
    }

    public void Reset(int length)
    {
        _position = 0;
        Length = length;
    }

    public void Seek(int position)
    {
        _position = position;
    }

    public void Write(MqttBufferWriter propertyWriter)
    {
        ArgumentNullException.ThrowIfNull(propertyWriter);

        WriteBinary(propertyWriter._buffer, 0, propertyWriter.Length);
    }

    public void WriteBinary(ArraySegment<byte> value)
    {
        var length = value.Count;

        EnsureAdditionalCapacity(length + 2);

        _buffer[_position] = (byte)(length >> 8);
        _buffer[_position + 1] = (byte)length;

        if (length > 0)
        {
            MqttMemoryHelper.Copy(value.Array, value.Offset, _buffer, _position + 2, length);
        }

        IncreasePosition(length + 2);
    }

    public void WriteBinary(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (count == 0)
        {
            return;
        }

        EnsureAdditionalCapacity(count);

        MqttMemoryHelper.Copy(buffer, offset, _buffer, _position, count);
        IncreasePosition(count);
    }

    public void WriteByte(byte @byte)
    {
        EnsureAdditionalCapacity(1);

        _buffer[_position] = @byte;
        IncreasePosition(1);
    }

    public void WriteString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            EnsureAdditionalCapacity(2);

            _buffer[_position] = 0;
            _buffer[_position + 1] = 0;

            IncreasePosition(2);
        }
        else
        {
            // Do not use Encoding.UTF8.GetByteCount(value);
            // UTF8 chars can have a max length of 4 and the used buffer increase *2 every time.
            // So the buffer should always have much more capacity left so that a correct value
            // here is only waste of CPU cycles.
            var byteCount = value.Length * 4;

            EnsureAdditionalCapacity(byteCount + 2);

            var writtenBytes = Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _position + 2);

            // From RFC: 1.5.4 UTF-8 Encoded String
            // Unless stated otherwise all UTF-8 encoded strings can have any length in the range 0 to 65,535 bytes.
            if (writtenBytes > EncodedStringMaxLength)
            {
                throw new MqttProtocolViolationException($"The maximum string length is 65535. The current string has a length of {writtenBytes}.");
            }

            _buffer[_position] = (byte)(writtenBytes >> 8);
            _buffer[_position + 1] = (byte)writtenBytes;

            IncreasePosition(writtenBytes + 2);
        }
    }

    public void WriteString(ReadOnlyMemory<byte> value)
    {
        var span = value.Span;
        var length = span.Length;

        if (length > EncodedStringMaxLength)
        {
            throw new MqttProtocolViolationException($"The maximum string length is 65535. The current string has a length of {length}.");
        }

        EnsureAdditionalCapacity(length + 2);

        _buffer[_position] = (byte)(length >> 8);
        _buffer[_position + 1] = (byte)length;

        if (length > 0)
        {
            span.CopyTo(_buffer.AsSpan(_position + 2));
        }

        IncreasePosition(length + 2);
    }

    public void WriteString(ArraySegment<byte> utf8Value)
    {
        var length = utf8Value.Count;

        if (length > EncodedStringMaxLength)
        {
            throw new MqttProtocolViolationException($"The maximum string length is 65535. The current string has a length of {length}.");
        }

        EnsureAdditionalCapacity(length + 2);

        _buffer[_position] = (byte)(length >> 8);
        _buffer[_position + 1] = (byte)length;

        if (length > 0)
        {
            MqttMemoryHelper.Copy(utf8Value.Array, utf8Value.Offset, _buffer, _position + 2, length);
        }

        IncreasePosition(length + 2);
    }

    public void WriteTwoByteInteger(ushort value)
    {
        EnsureAdditionalCapacity(2);

        _buffer[_position] = (byte)(value >> 8);
        IncreasePosition(1);
        _buffer[_position] = (byte)value;
        IncreasePosition(1);
    }

    public void WriteVariableByteInteger(uint value)
    {
        if (value == 0)
        {
            _buffer[_position] = 0;
            IncreasePosition(1);

            return;
        }

        if (value <= 127)
        {
            _buffer[_position] = (byte)value;
            IncreasePosition(1);

            return;
        }

        MqttProtocolViolationException.ThrowIfVariableByteIntegerExceedsLimit(value);

        var size = 0;
        var x = value;
        do
        {
            var encodedByte = x % 128;
            x /= 128;
            if (x > 0)
            {
                encodedByte |= 128;
            }

            _buffer[_position + size] = (byte)encodedByte;
            size++;
        } while (x > 0);

        IncreasePosition(size);
    }

    void EnsureAdditionalCapacity(int additionalCapacity)
    {
        var bufferLength = _buffer.Length;

        var freeSpace = bufferLength - _position;
        if (freeSpace >= additionalCapacity)
        {
            return;
        }

        throw new InvalidDataException("Ran out of buffer writer capacity. Fix the capacity manually on the MqttBufferWriter");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IncreasePosition(int length)
    {
        _position += length;

        if (_position > Length)
        {
            // Also extend the position because we reached the end of the
            // pre allocated buffer.
            Length = _position;
        }
    }

    private void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
                ArrayPool<byte>.Shared.Return(_buffer);
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~MqttBufferWriter()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

}