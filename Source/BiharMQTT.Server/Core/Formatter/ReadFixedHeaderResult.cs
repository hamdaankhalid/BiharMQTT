// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Formatter;

public struct ReadFixedHeaderResult
{
    public static ReadFixedHeaderResult Canceled { get; } = new()
    {
        IsCanceled = true
    };

    public static ReadFixedHeaderResult ConnectionClosed { get; } = new()
    {
        IsConnectionClosed = true
    };

    private const byte CanceledFlag = 0x40;
    private const byte ConnectionClosedFlag = 0x80;

    // byte 31 is set if is ConnectionClosed, byte 30 is set if is Canceled, the rest of the bits are reserved for future use
    private byte _data;

    public bool IsCanceled
    {
        readonly get => (_data & CanceledFlag) != 0;
        init => _data = (byte)((_data & ~CanceledFlag) | (value ? CanceledFlag : 0));
    }

    public bool IsConnectionClosed
    {
        readonly get => (_data & ConnectionClosedFlag) != 0;
        init => _data = (byte)((_data & ~ConnectionClosedFlag) | (value ? ConnectionClosedFlag : 0));
    }

    public MqttFixedHeader FixedHeader { get; init; }
}