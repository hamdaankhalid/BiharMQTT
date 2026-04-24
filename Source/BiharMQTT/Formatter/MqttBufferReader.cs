// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using System.Text;
using BiharMQTT.Exceptions;
using BiharMQTT.Internal;
using System.Buffers.Binary;


namespace BiharMQTT.Formatter;

public sealed class MqttBufferReader
{
    byte[] _buffer = EmptyBuffer.Array;
    int _maxPosition;
    int _offset;
    int _position;

    public int BytesLeft => _maxPosition - _position;

    public bool EndOfStream => BytesLeft == 0;

    public int Position => _position - _offset;

    public byte[] ReadBinaryData()
    {
        var length = ReadTwoByteInteger();

        if (length == 0)
        {
            return EmptyBuffer.Array;
        }

        ValidateReceiveBuffer(length);

        var result = GC.AllocateUninitializedArray<byte>(length);
        MqttMemoryHelper.Copy(_buffer, _position, result, 0, length);
        _position += length;

        return result;
    }

    public byte ReadByte()
    {
        ValidateReceiveBuffer(1);
        return _buffer[_position++];
    }

    public uint ReadFourByteInteger()
    {
        ValidateReceiveBuffer(4);

        var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_position));

        _position += 4;
        return value;
    }

    public byte[] ReadRemainingData()
    {
        var bufferLength = BytesLeft;
        if (bufferLength == 0)
        {
            return EmptyBuffer.Array;
        }

        var buffer = GC.AllocateUninitializedArray<byte>(bufferLength);
        MqttMemoryHelper.Copy(_buffer, _position, buffer, 0, bufferLength);
        _position += bufferLength;

        return buffer;
    }

    /// <summary>
    ///     Returns the remaining data as an <see cref="ArraySegment{T}" /> that
    ///     references the existing body buffer directly — no allocation, no copy.
    ///     The segment is only valid as long as the underlying body buffer is alive.
    /// </summary>
    public ArraySegment<byte> ReadRemainingDataSlice()
    {
        var bufferLength = BytesLeft;
        if (bufferLength == 0)
        {
            return EmptyBuffer.ArraySegment;
        }

        var segment = new ArraySegment<byte>(_buffer, _position, bufferLength);
        _position += bufferLength;

        return segment;
    }


    public bool PeekEqualsSequence(ReadOnlySpan<byte> str, out int bytesToSkip)
    {
        var stringLength = ReadTwoByteInteger();

        if (stringLength == 0)
        {
            bytesToSkip = 2;
            _position -= 2; // Reset to before the length prefix
            return str.Length == 0;
        }

        ValidateReceiveBuffer(stringLength);
        bool res = _buffer.AsSpan(_position, stringLength).SequenceEqual(str);

        _position -= 2; // Reset to before the length prefix
        bytesToSkip = 2 + stringLength; // Total bytes: length prefix + string data
        return res;
    }

    /// <summary>
    ///     Reads the next length-prefixed string and compares it against the expected
    ///     UTF-8 byte sequence without allocating a managed string. Advances position
    ///     past the entire string regardless of whether it matches.
    /// </summary>
    public bool ReadNextStringEquals(ReadOnlySpan<byte> expected)
    {
        var length = ReadTwoByteInteger();

        if (length == 0)
        {
            return expected.Length == 0;
        }

        ValidateReceiveBuffer(length);
        bool match = _buffer.AsSpan(_position, length).SequenceEqual(expected);
        _position += length;
        return match;
    }

    /// <summary>
    ///     Returns the next length-prefixed binary data as an <see cref="ArraySegment{T}" />
    ///     referencing the existing body buffer directly — no allocation, no copy.
    ///     The segment is only valid as long as the underlying body buffer is alive.
    /// </summary>
    public ArraySegment<byte> ReadBinaryDataSlice()
    {
        var length = ReadTwoByteInteger();

        if (length == 0)
        {
            return EmptyBuffer.ArraySegment;
        }

        ValidateReceiveBuffer(length);
        var segment = new ArraySegment<byte>(_buffer, _position, length);
        _position += length;
        return segment;
    }

    /// <summary>
    ///     Advances position past the next length-prefixed string without reading it.
    /// </summary>
    public void SkipString()
    {
        var length = ReadTwoByteInteger();
        if (length > 0)
        {
            ValidateReceiveBuffer(length);
            _position += length;
        }
    }

    /// <summary>
    ///     Advances position past the next length-prefixed binary data without reading it.
    /// </summary>
    public void SkipBinaryData()
    {
        SkipString(); // Same wire format: 2-byte length prefix + data
    }


    public string ReadString()
    {
        var length = ReadTwoByteInteger();

        if (length == 0)
        {
            return string.Empty;
        }

        ValidateReceiveBuffer(length);

        // AsSpan() version is slightly faster. Not much but at least a little bit.
        var result = Encoding.UTF8.GetString(_buffer.AsSpan(_position, length));

        _position += length;
        return result;
    }

    public ushort ReadTwoByteInteger()
    {
        ValidateReceiveBuffer(2);

        var value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_position));

        _position += 2;
        return value;
    }

    public uint ReadVariableByteInteger()
    {
        var multiplier = 1;
        var value = 0U;
        byte encodedByte;

        do
        {
            encodedByte = ReadByte();
            value += (uint)((encodedByte & 127) * multiplier);

            if (multiplier > 2097152)
            {
                throw new MqttProtocolViolationException("Variable length integer is invalid.");
            }

            multiplier *= 128;
        } while ((encodedByte & 128) != 0);

        return value;
    }

    public void Seek(int position)
    {
        _position = _offset + position;
    }

    public void SetBuffer(ArraySegment<byte> buffer)
    {
        SetBuffer(buffer.Array, buffer.Offset, buffer.Count);
    }

    public void SetBuffer(byte[] buffer, int offset, int length)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _offset = offset;
        _position = offset;
        _maxPosition = offset + length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ValidateReceiveBuffer(int length)
    {
        var newPosition = _position + length;
        if (_maxPosition < newPosition)
        {
            throw new MqttProtocolViolationException($"Expected at least {newPosition} bytes but there are only {_maxPosition} bytes");
        }
    }
}