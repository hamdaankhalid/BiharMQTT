using BiharMQTT.Adapter;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Formatter;
using BiharMQTT.Packets;
using BiharMQTT.Tests.Mockups;

namespace BiharMQTT.Tests;

public sealed class MqttPacketSerializationHelper
{
    readonly IMqttPacketFormatter _packetFormatter;
    readonly MqttProtocolVersion _protocolVersion;

    public MqttPacketSerializationHelper(MqttProtocolVersion protocolVersion = MqttProtocolVersion.V500, MqttBufferWriter bufferWriter = null)
    {
        _protocolVersion = protocolVersion;

        if (bufferWriter == null)
        {
            bufferWriter = new MqttBufferWriter(4096, 65535);
        }

        _packetFormatter = MqttPacketFormatterAdapter.GetMqttPacketFormatter(_protocolVersion, bufferWriter);
    }

    public MqttPacket Decode(MqttPacketBuffer buffer)
    {
        // HK TODO: TF is this ToArray crap?
        using var channel = new MemoryMqttChannel(buffer.ToArray());
        var formatterAdapter = new MqttPacketFormatterAdapter(_protocolVersion, new MqttBufferWriter(4096, 65535));

        var adapter = new MqttChannelAdapter(channel, formatterAdapter, MqttNetNullLogger.Instance);
        return adapter.ReceivePacketAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}