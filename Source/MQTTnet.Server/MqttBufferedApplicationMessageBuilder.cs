// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Exceptions;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server;

/// <summary>
///     Fluent builder for <see cref="MqttBufferedApplicationMessage" />.
///     The builder is a class and may be reused across calls — only the
///     resulting struct is allocated on the stack.
/// </summary>
public sealed class MqttBufferedApplicationMessageBuilder
{
    string _contentType;
    byte[] _correlationData;
    uint _messageExpiryInterval;
    ReadOnlyMemory<byte> _payload;
    MqttPayloadFormatIndicator _payloadFormatIndicator;
    MqttQualityOfServiceLevel _qualityOfServiceLevel;
    string _responseTopic;
    bool _retain;
    List<uint> _subscriptionIdentifiers;
    string _topic;
    List<MqttUserProperty> _userProperties;

    public MqttBufferedApplicationMessage Build()
    {
        if (string.IsNullOrEmpty(_topic))
        {
            throw new MqttProtocolViolationException("Topic is not set.");
        }

        return new MqttBufferedApplicationMessage
        {
            Topic = _topic,
            Payload = _payload,
            QualityOfServiceLevel = _qualityOfServiceLevel,
            Retain = _retain,
            ContentType = _contentType,
            ResponseTopic = _responseTopic,
            CorrelationData = _correlationData,
            MessageExpiryInterval = _messageExpiryInterval,
            PayloadFormatIndicator = _payloadFormatIndicator,
            UserProperties = _userProperties,
            SubscriptionIdentifiers = _subscriptionIdentifiers
        };
    }

    public MqttBufferedApplicationMessageBuilder WithContentType(string contentType)
    {
        _contentType = contentType;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithCorrelationData(byte[] correlationData)
    {
        _correlationData = correlationData;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithMessageExpiryInterval(uint messageExpiryInterval)
    {
        _messageExpiryInterval = messageExpiryInterval;
        return this;
    }

    /// <summary>
    ///     Sets the payload from a <see cref="ReadOnlyMemory{T}" />.
    ///     This is the preferred overload for pooled / rented buffers.
    /// </summary>
    public MqttBufferedApplicationMessageBuilder WithPayload(ReadOnlyMemory<byte> payload)
    {
        _payload = payload;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithPayload(byte[] payload)
    {
        _payload = payload ?? ReadOnlyMemory<byte>.Empty;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithPayload(ArraySegment<byte> payload)
    {
        _payload = payload;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithPayloadFormatIndicator(MqttPayloadFormatIndicator payloadFormatIndicator)
    {
        _payloadFormatIndicator = payloadFormatIndicator;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithQualityOfServiceLevel(MqttQualityOfServiceLevel qualityOfServiceLevel)
    {
        _qualityOfServiceLevel = qualityOfServiceLevel;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithResponseTopic(string responseTopic)
    {
        _responseTopic = responseTopic;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithRetainFlag(bool value = true)
    {
        _retain = value;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithSubscriptionIdentifier(uint subscriptionIdentifier)
    {
        MqttProtocolViolationException.ThrowIfVariableByteIntegerExceedsLimit(subscriptionIdentifier);

        _subscriptionIdentifiers ??= [];
        _subscriptionIdentifiers.Add(subscriptionIdentifier);
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithTopic(string topic)
    {
        _topic = topic;
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithUserProperty(string name, ReadOnlyMemory<byte> value)
    {
        _userProperties ??= [];
        _userProperties.Add(new MqttUserProperty(name, value));
        return this;
    }

    public MqttBufferedApplicationMessageBuilder WithUserProperty(string name, ArraySegment<byte> value)
    {
        _userProperties ??= [];
        _userProperties.Add(new MqttUserProperty(name, value));
        return this;
    }
}
