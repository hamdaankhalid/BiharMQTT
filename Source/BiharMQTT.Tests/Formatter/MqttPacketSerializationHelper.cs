// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using BiharMQTT.Formatter;
using BiharMQTT.Formatter.V5;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Tests.Formatter;

/// <summary>
///     Test helper that round-trips MQTT packets through the V5 encoder and decoder.
/// </summary>
internal static class MqttPacketSerializationHelper
{
    public static ArraySegment<byte> EncodePacket<T>(T packet, MqttProtocolVersion version) where T : struct
    {
        var encoder = CreateEncoder();
        var buffer = EncodeWithEncoder(encoder, packet);
        return buffer.Join();
    }

    public static object DecodePacket(in ArraySegment<byte> raw, MqttProtocolVersion version)
    {
        ParseRaw(raw, out var header, out var body);
        var packetType = (MqttControlPacketType)(header >> 4);
        var decoder = new MqttV5PacketDecoder();

        return packetType switch
        {
            MqttControlPacketType.Auth => (object)decoder.DecodeAuthPacket(body),
            MqttControlPacketType.ConnAck => decoder.DecodeConnAckPacket(body),
            MqttControlPacketType.Connect => decoder.DecodeConnectPacket(body),
            MqttControlPacketType.Disconnect => decoder.DecodeDisconnectPacket(body),
            MqttControlPacketType.PubAck => decoder.DecodePubAckPacket(body),
            MqttControlPacketType.PubComp => decoder.DecodePubCompPacket(body),
            MqttControlPacketType.Publish => decoder.DecodePublishPacket(header, body),
            MqttControlPacketType.PubRec => decoder.DecodePubRecPacket(body),
            MqttControlPacketType.PubRel => decoder.DecodePubRelPacket(body),
            MqttControlPacketType.SubAck => decoder.DecodeSubAckPacket(body),
            MqttControlPacketType.Subscribe => decoder.DecodeSubscribePacket(body),
            MqttControlPacketType.UnsubAck => decoder.DecodeUnsubAckPacket(body),
            MqttControlPacketType.Unsubscribe => decoder.DecodeUnsubscribePacket(body),
            MqttControlPacketType.PingReq => new MqttPingReqPacket(),
            MqttControlPacketType.PingResp => new MqttPingRespPacket(),
            _ => throw new NotSupportedException($"Packet type {packetType} is not supported.")
        };
    }

    public static T EncodeAndDecodePacket<T>(T packet, MqttProtocolVersion version) where T : struct
    {
        var raw = EncodePacket(packet, version);
        var decoded = DecodePacket(raw, version);
        return (T)decoded;
    }

    // Only used in tests
    static MqttV5PacketEncoder CreateEncoder()
    {
        return new MqttV5PacketEncoder(new MqttBufferWriter(4096));
    }

    static MqttPacketBuffer EncodeWithEncoder<T>(MqttV5PacketEncoder encoder, T packet) where T : struct
    {
        // We need to call the appropriate Encode method based on the packet type.
        // Using pattern matching since we can't use ref with generic boxing.
        if (packet is MqttAuthPacket authPacket)
        {
            return encoder.Encode(ref authPacket);
        }
        if (packet is MqttConnAckPacket connAckPacket)
        {
            return encoder.Encode(ref connAckPacket);
        }
        if (packet is MqttConnectPacket connectPacket)
        {
            return encoder.Encode(ref connectPacket);
        }
        if (packet is MqttDisconnectPacket disconnectPacket)
        {
            return encoder.Encode(ref disconnectPacket);
        }
        if (packet is MqttPublishPacket publishPacket)
        {
            return encoder.Encode(ref publishPacket);
        }
        if (packet is MqttPubAckPacket pubAckPacket)
        {
            return encoder.Encode(ref pubAckPacket);
        }
        if (packet is MqttPubRecPacket pubRecPacket)
        {
            return encoder.Encode(ref pubRecPacket);
        }
        if (packet is MqttPubRelPacket pubRelPacket)
        {
            return encoder.Encode(ref pubRelPacket);
        }
        if (packet is MqttPubCompPacket pubCompPacket)
        {
            return encoder.Encode(ref pubCompPacket);
        }
        if (packet is MqttSubscribePacket subscribePacket)
        {
            return encoder.Encode(ref subscribePacket);
        }
        if (packet is MqttSubAckPacket subAckPacket)
        {
            return encoder.Encode(ref subAckPacket);
        }
        if (packet is MqttUnsubscribePacket unsubscribePacket)
        {
            return encoder.Encode(ref unsubscribePacket);
        }
        if (packet is MqttUnsubAckPacket unsubAckPacket)
        {
            return encoder.Encode(ref unsubAckPacket);
        }
        if (packet is MqttPingReqPacket)
        {
            return encoder.EncodePingReq();
        }
        if (packet is MqttPingRespPacket)
        {
            return encoder.EncodePingResp();
        }

        throw new NotSupportedException($"Packet type {typeof(T).Name} is not supported.");
    }

    static void ParseRaw(in ArraySegment<byte> raw, out byte header, out ArraySegment<byte> body)
    {
        var array = raw.Array!;
        var offset = raw.Offset;
        var end = raw.Offset + raw.Count;

        header = array[offset++];

        // Read variable-length remaining length
        uint remainingLength = 0;
        var multiplier = 1;
        for (var i = 0; i < 4; i++)
        {
            var encodedByte = array[offset++];
            remainingLength += (uint)((encodedByte & 0x7F) * multiplier);
            if ((encodedByte & 0x80) == 0)
            {
                break;
            }
            multiplier *= 128;
        }

        body = new ArraySegment<byte>(array, offset, (int)remainingLength);
    }
}
