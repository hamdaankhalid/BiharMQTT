// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using BiharMQTT.Adapter;
using BiharMQTT.Exceptions;
using BiharMQTT.Formatter.V5;

namespace BiharMQTT.Formatter;

public sealed class MqttPacketFormatterAdapter
{

    public static ReadOnlySpan<byte> MqttPrefix => "MQTT"u8;

    readonly MqttBufferReader _bufferReader = new();
    readonly MqttBufferWriter _bufferWriter;

    MqttV5PacketFormatter _formatter;

    public MqttPacketFormatterAdapter(MqttBufferWriter mqttBufferWriter)
    {
        _bufferWriter = mqttBufferWriter ?? throw new ArgumentNullException(nameof(mqttBufferWriter));
    }

    public MqttPacketFormatterAdapter(MqttProtocolVersion protocolVersion, MqttBufferWriter bufferWriter)
        : this(bufferWriter)
    {
        UseProtocolVersion(protocolVersion);
    }

    public MqttProtocolVersion ProtocolVersion { get; private set; } = MqttProtocolVersion.Unknown;

    public MqttV5PacketDecoder Decoder => _formatter.Decoder;

    public MqttV5PacketEncoder Encoder => _formatter.Encoder;

    public void Cleanup()
    {
        _bufferWriter.Cleanup();
    }

    public void DetectProtocolVersion(ReceivedMqttPacket receivedMqttPacket)
    {
        var protocolVersion = ParseProtocolVersion(receivedMqttPacket);
        UseProtocolVersion(protocolVersion);
    }

    public static MqttV5PacketFormatter GetMqttPacketFormatter(MqttProtocolVersion protocolVersion, MqttBufferWriter bufferWriter)
    {
        if (protocolVersion == MqttProtocolVersion.Unknown)
        {
            throw new InvalidOperationException("MQTT protocol version is invalid.");
        }

        return protocolVersion switch
        {
            MqttProtocolVersion.V500 => new MqttV5PacketFormatter(bufferWriter),
            _ => throw new NotSupportedException()
        };
    }

    MqttProtocolVersion ParseProtocolVersion(ReceivedMqttPacket receivedMqttPacket)
    {
        if (receivedMqttPacket.Body.Count < 7)
        {
            // 2 byte protocol name length
            // at least 4 byte protocol name
            // 1 byte protocol level
            throw new MqttProtocolViolationException("CONNECT packet must have at least 7 bytes.");
        }

        _bufferReader.SetBuffer(receivedMqttPacket.Body.Array, receivedMqttPacket.Body.Offset, receivedMqttPacket.Body.Count);

        if (_bufferReader.AdvanceIfMatch(MqttPrefix))
        {
            var protocolLevel = _bufferReader.ReadByte();

            // Remove the mosquitto try_private flag (MQTT 3.1.1 Bridge).
            protocolLevel &= 0x7F;
            if (protocolLevel == 5)
            {
                return MqttProtocolVersion.V500;
            }

            throw new MqttProtocolViolationException($"Protocol level '{protocolLevel}' not supported. Only MQTT v5 is supported.");
        }

        throw new MqttProtocolViolationException("Protocol not supported. Only MQTT v5 is supported.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ThrowIfFormatterNotSet()
    {
        if (_formatter == null)
        {
            throw new InvalidOperationException("Protocol version not set or detected.");
        }
    }

    void UseProtocolVersion(MqttProtocolVersion protocolVersion)
    {
        if (protocolVersion == MqttProtocolVersion.Unknown)
        {
            throw new InvalidOperationException("MQTT protocol version is invalid.");
        }

        ProtocolVersion = protocolVersion;
        _formatter = GetMqttPacketFormatter(protocolVersion, _bufferWriter);
    }
}