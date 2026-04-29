// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Security.Cryptography;
using BiharMQTT.Exceptions;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Formatter.V5;

public sealed class MqttV5PacketEncoder : IDisposable
{
    const int FixedHeaderSize = 1;

    readonly MqttBufferWriter _bufferWriter;
    readonly MqttV5PropertiesWriter _propertiesWriter = new(new MqttBufferWriter(Constants.PerBufferWriterMemoryAllocatedbytes));
    private bool disposedValue;


    public MqttV5PacketEncoder(MqttBufferWriter bufferWriter)
    {
        _bufferWriter = bufferWriter;
    }


    public MqttPacketBuffer Encode(ref MqttConnectPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodeConnectPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttConnAckPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodeConnAckPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttDisconnectPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodeDisconnectPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttPublishPacket packet)
    {
        BeginEncode();
        byte fixedHeader = EncodePublishPacket(ref packet);
        return FinalizePacket(fixedHeader, packet.Payload);
    }

    public MqttPacketBuffer Encode(ref MqttPubAckPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodePubAckPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttPubRecPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodePubRecPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttPubRelPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodePubRelPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttPubCompPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodePubCompPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttSubscribePacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodeSubscribePacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttSubAckPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodeSubAckPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttUnsubscribePacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodeUnsubscribePacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttUnsubAckPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodeUnsubAckPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer Encode(ref MqttAuthPacket packet)
    {
        BeginEncode();
        var fixedHeader = EncodeAuthPacket(ref packet);
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer EncodePingReq()
    {
        BeginEncode();
        var fixedHeader = EncodePingReqPacket();
        return FinalizePacket(fixedHeader);
    }

    public MqttPacketBuffer EncodePingResp()
    {
        BeginEncode();
        var fixedHeader = EncodePingRespPacket();
        return FinalizePacket(fixedHeader);
    }

    void BeginEncode()
    {
        const int reservedHeaderSize = 5;
        _bufferWriter.Reset(reservedHeaderSize);
        _bufferWriter.Seek(reservedHeaderSize);
    }

    MqttPacketBuffer FinalizePacket(byte fixedHeader, ReadOnlySequence<byte> payload = default)
    {
        const int reservedHeaderSize = 5;
        var remainingLength = (uint)_bufferWriter.Length - reservedHeaderSize;
        remainingLength += (uint)payload.Length;

        var remainingLengthSize = MqttBufferWriter.GetVariableByteIntegerSize(remainingLength);
        var headerSize = FixedHeaderSize + remainingLengthSize;
        var headerOffset = reservedHeaderSize - headerSize;

        _bufferWriter.Seek(headerOffset);
        _bufferWriter.WriteByte(fixedHeader);
        _bufferWriter.WriteVariableByteInteger(remainingLength);

        var buffer = _bufferWriter.GetBuffer();
        var firstSegment = new ArraySegment<byte>(buffer, headerOffset, _bufferWriter.Length - headerOffset);
        return payload.Length > 0 ? new MqttPacketBuffer(firstSegment, payload) : new MqttPacketBuffer(firstSegment);
    }

    byte EncodeAuthPacket(ref MqttAuthPacket packet)
    {
        _propertiesWriter.WriteAuthenticationMethod(packet.AuthenticationMethod);
        _propertiesWriter.WriteAuthenticationData(packet.AuthenticationData);
        _propertiesWriter.WriteReasonString(packet.ReasonString);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        // MQTT spec: The Reason Code and Property Length can be omitted if the Reason Code is 0x00 (Success) and there are no Properties.
        // In this case the AUTH has a Remaining Length of 0.
        if (packet.ReasonCode == MqttAuthenticateReasonCode.Success && _propertiesWriter.Length == 0)
        {
            return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.Auth);
        }

        _bufferWriter.WriteByte((byte)packet.ReasonCode);

        _propertiesWriter.WriteTo(_bufferWriter);
        _propertiesWriter.Reset();

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.Auth);
    }

    byte EncodeConnAckPacket(ref MqttConnAckPacket packet)
    {
        byte connectAcknowledgeFlags = 0x0;
        if (packet.IsSessionPresent)
        {
            connectAcknowledgeFlags |= 0x1;
        }

        _bufferWriter.WriteByte(connectAcknowledgeFlags);
        _bufferWriter.WriteByte((byte)packet.ReasonCode);

        _propertiesWriter.WriteSessionExpiryInterval(packet.SessionExpiryInterval);
        _propertiesWriter.WriteAuthenticationMethod(packet.AuthenticationMethod);
        _propertiesWriter.WriteAuthenticationData(packet.AuthenticationData);
        _propertiesWriter.WriteRetainAvailable(packet.RetainAvailable);
        _propertiesWriter.WriteReceiveMaximum(packet.ReceiveMaximum);
        _propertiesWriter.WriteMaximumQoS(packet.MaximumQoS);
        _propertiesWriter.WriteAssignedClientIdentifier(packet.AssignedClientIdentifier);
        _propertiesWriter.WriteTopicAliasMaximum(packet.TopicAliasMaximum);
        _propertiesWriter.WriteReasonString(packet.ReasonString);
        _propertiesWriter.WriteMaximumPacketSize(packet.MaximumPacketSize);
        _propertiesWriter.WriteWildcardSubscriptionAvailable(packet.WildcardSubscriptionAvailable);
        _propertiesWriter.WriteSubscriptionIdentifiersAvailable(packet.SubscriptionIdentifiersAvailable);
        _propertiesWriter.WriteSharedSubscriptionAvailable(packet.SharedSubscriptionAvailable);
        _propertiesWriter.WriteServerKeepAlive(packet.ServerKeepAlive);
        _propertiesWriter.WriteResponseInformation(packet.ResponseInformation);
        _propertiesWriter.WriteServerReference(packet.ServerReference);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        _propertiesWriter.WriteTo(_bufferWriter);
        _propertiesWriter.Reset();

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.ConnAck);
    }

    byte EncodeConnectPacket(ref MqttConnectPacket packet)
    {
        if (packet.ClientId.Count == 0 && !packet.CleanSession)
        {
            throw new MqttProtocolViolationException("CleanSession must be set if ClientId is empty [MQTT-3.1.3-7].");
        }

        _bufferWriter.WriteString("MQTT");
        _bufferWriter.WriteByte(5); // [3.1.2.2 Protocol Version]

        byte connectFlags = 0x0;
        if (packet.CleanSession)
        {
            connectFlags |= 0x2;
        }

        if (packet.WillFlag)
        {
            connectFlags |= 0x4;
            connectFlags |= (byte)((byte)packet.WillQoS << 3);

            if (packet.WillRetain)
            {
                connectFlags |= 0x20;
            }
        }

        if (packet.Password.Count > 0 && packet.Username.Count == 0)
        {
            throw new MqttProtocolViolationException("If the User Name Flag is set to 0, the Password Flag MUST be set to 0 [MQTT-3.1.2-22].");
        }

        if (packet.Password.Count > 0)
        {
            connectFlags |= 0x40;
        }

        if (packet.Username.Count > 0)
        {
            connectFlags |= 0x80;
        }

        _bufferWriter.WriteByte(connectFlags);
        _bufferWriter.WriteTwoByteInteger(packet.KeepAlivePeriod);

        _propertiesWriter.WriteSessionExpiryInterval(packet.SessionExpiryInterval);
        _propertiesWriter.WriteAuthenticationMethod(packet.AuthenticationMethod);
        _propertiesWriter.WriteAuthenticationData(packet.AuthenticationData);
        _propertiesWriter.WriteRequestProblemInformation(packet.RequestProblemInformation);
        _propertiesWriter.WriteRequestResponseInformation(packet.RequestResponseInformation);
        _propertiesWriter.WriteReceiveMaximum(packet.ReceiveMaximum);
        _propertiesWriter.WriteTopicAliasMaximum(packet.TopicAliasMaximum);
        _propertiesWriter.WriteMaximumPacketSize(packet.MaximumPacketSize);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        _propertiesWriter.WriteTo(_bufferWriter);
        _propertiesWriter.Reset();

        _bufferWriter.WriteString(packet.ClientId);

        if (packet.WillFlag)
        {
            _propertiesWriter.WritePayloadFormatIndicator(packet.WillPayloadFormatIndicator);
            _propertiesWriter.WriteMessageExpiryInterval(packet.WillMessageExpiryInterval);
            _propertiesWriter.WriteResponseTopic(packet.WillResponseTopic);
            _propertiesWriter.WriteCorrelationData(packet.WillCorrelationData);
            _propertiesWriter.WriteContentType(packet.WillContentType);
            _propertiesWriter.WriteUserProperties(packet.WillUserProperties);
            _propertiesWriter.WriteWillDelayInterval(packet.WillDelayInterval);

            _propertiesWriter.WriteTo(_bufferWriter);
            _propertiesWriter.Reset();

            _bufferWriter.WriteString(packet.WillTopic);
            _bufferWriter.WriteBinary(packet.WillMessage);
        }

        if (packet.Username.Count > 0)
        {
            _bufferWriter.WriteString(packet.Username);
        }

        if (packet.Password.Count > 0)
        {
            _bufferWriter.WriteBinary(packet.Password);
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.Connect);
    }

    byte EncodeDisconnectPacket(ref MqttDisconnectPacket packet)
    {
        _bufferWriter.WriteByte((byte)packet.ReasonCode);

        _propertiesWriter.WriteServerReference(packet.ServerReference);
        _propertiesWriter.WriteReasonString(packet.ReasonString);
        _propertiesWriter.WriteSessionExpiryInterval(packet.SessionExpiryInterval);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        _propertiesWriter.WriteTo(_bufferWriter);
        _propertiesWriter.Reset();

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.Disconnect);
    }

    static byte EncodePingReqPacket()
    {
        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.PingReq);
    }

    static byte EncodePingRespPacket()
    {
        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.PingResp);
    }

    byte EncodePubAckPacket(ref MqttPubAckPacket packet)
    {
        if (packet.PacketIdentifier == 0)
        {
            throw new MqttProtocolViolationException("PubAck packet has no packet identifier.");
        }

        _bufferWriter.WriteTwoByteInteger(packet.PacketIdentifier);

        _propertiesWriter.WriteReasonString(packet.ReasonString);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        if (_propertiesWriter.Length > 0 || packet.ReasonCode != MqttPubAckReasonCode.Success)
        {
            _bufferWriter.WriteByte((byte)packet.ReasonCode);
            _propertiesWriter.WriteTo(_bufferWriter);
            _propertiesWriter.Reset();
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.PubAck);
    }

    byte EncodePubCompPacket(ref MqttPubCompPacket packet)
    {
        ThrowIfPacketIdentifierIsInvalid(packet.PacketIdentifier, nameof(MqttPubCompPacket));

        _bufferWriter.WriteTwoByteInteger(packet.PacketIdentifier);

        _propertiesWriter.WriteReasonString(packet.ReasonString);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        if (_propertiesWriter.Length > 0 || packet.ReasonCode != MqttPubCompReasonCode.Success)
        {
            _bufferWriter.WriteByte((byte)packet.ReasonCode);
            _propertiesWriter.WriteTo(_bufferWriter);
            _propertiesWriter.Reset();
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.PubComp);
    }

    byte EncodePublishPacket(ref MqttPublishPacket packet)
    {
        if (packet.QualityOfServiceLevel == 0 && packet.Dup)
        {
            throw new MqttProtocolViolationException("Dup flag must be false for QoS 0 packets [MQTT-3.3.1-2].");
        }

        _bufferWriter.WriteString(packet.Topic);

        if (packet.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
        {
            if (packet.PacketIdentifier == 0)
            {
                throw new MqttProtocolViolationException("Publish packet has no packet identifier.");
            }

            _bufferWriter.WriteTwoByteInteger(packet.PacketIdentifier);
        }
        else
        {
            if (packet.PacketIdentifier > 0)
            {
                throw new MqttProtocolViolationException("Packet identifier must be 0 if QoS == 0 [MQTT-2.3.1-5].");
            }
        }

        _propertiesWriter.WriteContentType(packet.ContentType);
        _propertiesWriter.WriteCorrelationData(packet.CorrelationData);
        _propertiesWriter.WriteMessageExpiryInterval(packet.MessageExpiryInterval);
        _propertiesWriter.WritePayloadFormatIndicator(packet.PayloadFormatIndicator);
        _propertiesWriter.WriteResponseTopic(packet.ResponseTopic);
        _propertiesWriter.WriteSubscriptionIdentifiers(packet.SubscriptionIdentifiers);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);
        _propertiesWriter.WriteTopicAlias(packet.TopicAlias);

        _propertiesWriter.WriteTo(_bufferWriter);
        _propertiesWriter.Reset();

        // The payload is the past part of the packet. But it is not added here in order to keep
        // memory allocation low.

        byte fixedHeader = 0;

        if (packet.Retain)
        {
            fixedHeader |= 0x01;
        }

        fixedHeader |= (byte)((byte)packet.QualityOfServiceLevel << 1);

        if (packet.Dup)
        {
            fixedHeader |= 0x08;
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.Publish, fixedHeader);
    }

    byte EncodePubRecPacket(ref MqttPubRecPacket packet)
    {
        ThrowIfPacketIdentifierIsInvalid(packet.PacketIdentifier, nameof(MqttPubRecPacket));

        _propertiesWriter.WriteReasonString(packet.ReasonString);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        _bufferWriter.WriteTwoByteInteger(packet.PacketIdentifier);

        if (_propertiesWriter.Length > 0 || packet.ReasonCode != MqttPubRecReasonCode.Success)
        {
            _bufferWriter.WriteByte((byte)packet.ReasonCode);
            _propertiesWriter.WriteTo(_bufferWriter);
            _propertiesWriter.Reset();
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.PubRec);
    }

    byte EncodePubRelPacket(ref MqttPubRelPacket packet)
    {
        ThrowIfPacketIdentifierIsInvalid(packet.PacketIdentifier, nameof(MqttPubRelPacket));

        _propertiesWriter.WriteReasonString(packet.ReasonString);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        _bufferWriter.WriteTwoByteInteger(packet.PacketIdentifier);

        if (_propertiesWriter.Length > 0 || packet.ReasonCode != MqttPubRelReasonCode.Success)
        {
            _bufferWriter.WriteByte((byte)packet.ReasonCode);
            _propertiesWriter.WriteTo(_bufferWriter);
            _propertiesWriter.Reset();
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.PubRel, 0x02);
    }

    byte EncodeSubAckPacket(ref MqttSubAckPacket packet)
    {
        if (packet.ReasonCodes?.Count == 0)
        {
            throw new MqttProtocolViolationException("At least one reason code must be set[MQTT - 3.8.3 - 3].");
        }

        ThrowIfPacketIdentifierIsInvalid(packet.PacketIdentifier, nameof(MqttSubAckPacket));

        _bufferWriter.WriteTwoByteInteger(packet.PacketIdentifier);

        _propertiesWriter.WriteReasonString(packet.ReasonString);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        _propertiesWriter.WriteTo(_bufferWriter);
        _propertiesWriter.Reset();

        foreach (var reasonCode in packet.ReasonCodes)
        {
            _bufferWriter.WriteByte((byte)reasonCode);
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.SubAck);
    }

    byte EncodeSubscribePacket(ref MqttSubscribePacket packet)
    {
        if (packet.TopicFilters?.Count == 0)
        {
            throw new MqttProtocolViolationException("At least one topic filter must be set [MQTT-3.8.3-3].");
        }

        ThrowIfPacketIdentifierIsInvalid(packet.PacketIdentifier, nameof(MqttSubscribePacket));

        _bufferWriter.WriteTwoByteInteger(packet.PacketIdentifier);

        if (packet.SubscriptionIdentifier > 0)
        {
            _propertiesWriter.WriteSubscriptionIdentifier(packet.SubscriptionIdentifier);
        }

        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        _propertiesWriter.WriteTo(_bufferWriter);
        _propertiesWriter.Reset();

        if (packet.TopicFilters?.Count > 0)
        {
            foreach (var topicFilter in packet.TopicFilters)
            {
                _bufferWriter.WriteString(topicFilter.Topic);

                var options = (byte)topicFilter.QualityOfServiceLevel;

                if (topicFilter.NoLocal)
                {
                    options |= 1 << 2;
                }

                if (topicFilter.RetainAsPublished)
                {
                    options |= 1 << 3;
                }

                if (topicFilter.RetainHandling != MqttRetainHandling.SendAtSubscribe)
                {
                    options |= (byte)((byte)topicFilter.RetainHandling << 4);
                }

                _bufferWriter.WriteByte(options);
            }
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.Subscribe, 0x02);
    }

    byte EncodeUnsubAckPacket(ref MqttUnsubAckPacket packet)
    {
        ThrowIfPacketIdentifierIsInvalid(packet.PacketIdentifier, nameof(MqttUnsubAckPacket));

        _bufferWriter.WriteTwoByteInteger(packet.PacketIdentifier);

        _propertiesWriter.WriteReasonString(packet.ReasonString);
        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        _propertiesWriter.WriteTo(_bufferWriter);
        _propertiesWriter.Reset();

        foreach (var reasonCode in packet.ReasonCodes)
        {
            _bufferWriter.WriteByte((byte)reasonCode);
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.UnsubAck);
    }

    byte EncodeUnsubscribePacket(ref MqttUnsubscribePacket packet)
    {
        if (packet.TopicFilters?.Count == 0)
        {
            throw new MqttProtocolViolationException("At least one topic filter must be set [MQTT-3.10.3-2].");
        }

        ThrowIfPacketIdentifierIsInvalid(packet.PacketIdentifier, nameof(MqttUnsubscribePacket));

        _bufferWriter.WriteTwoByteInteger(packet.PacketIdentifier);

        _propertiesWriter.WriteUserProperties(packet.UserProperties);

        _propertiesWriter.WriteTo(_bufferWriter);
        _propertiesWriter.Reset();

        foreach (var topicFilter in packet.TopicFilters)
        {
            _bufferWriter.WriteString(topicFilter);
        }

        return MqttBufferWriter.BuildFixedHeader(MqttControlPacketType.Unsubscribe, 0x02);
    }

    static void ThrowIfPacketIdentifierIsInvalid(ushort packetIdentifier, string packetTypeName)
    {
        // SUBSCRIBE, UNSUBSCRIBE, and PUBLISH(in cases where QoS > 0) Control Packets MUST contain a non-zero 16 - bit Packet Identifier[MQTT - 2.3.1 - 1].

        if (packetIdentifier == 0)
        {
            throw new MqttProtocolViolationException($"Packet identifier is not set for {packetTypeName}.");
        }
    }

    private void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
                _bufferWriter.Dispose();
                _propertiesWriter.Dispose();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~MqttV5PacketEncoder()
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