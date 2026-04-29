// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Formatter.V5;

public sealed class MqttV5PacketFormatter : IDisposable
{
    readonly MqttBufferWriter _bufferWriter;

    public MqttV5PacketDecoder Decoder { get; }
    public MqttV5PacketEncoder Encoder { get; }

    public MqttV5PacketFormatter(MqttBufferWriter bufferWriter)
    {
        _bufferWriter = bufferWriter ?? throw new ArgumentNullException(nameof(bufferWriter));
        Decoder = new MqttV5PacketDecoder();
        Encoder = new MqttV5PacketEncoder(bufferWriter);
    }

    public void Cleanup()
    {
        _bufferWriter.Cleanup();
    }

    public void Dispose()
    {
        Encoder.Dispose();
    }
}