// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Formatter.V5;

public sealed class MqttV5PropertiesWriter : IDisposable
{
    readonly MqttBufferWriter _bufferWriter;
    private bool disposedValue;


    public MqttV5PropertiesWriter(MqttBufferWriter bufferWriter)
    {
        _bufferWriter = bufferWriter ?? throw new ArgumentNullException(nameof(bufferWriter));
    }

    public int Length => _bufferWriter.Length;

    public void Reset()
    {
        _bufferWriter.Reset(0);
        _bufferWriter.Cleanup();
    }

    public void WriteAssignedClientIdentifier(ArraySegment<byte> value)
    {
        Write(MqttPropertyId.AssignedClientIdentifier, value);
    }

    public void WriteAuthenticationData(ArraySegment<byte> value)
    {
        WriteBinaryProperty(MqttPropertyId.AuthenticationData, value);
    }

    public void WriteAuthenticationMethod(ArraySegment<byte> value)
    {
        Write(MqttPropertyId.AuthenticationMethod, value);
    }

    public void WriteContentType(ArraySegment<byte> value)
    {
        Write(MqttPropertyId.ContentType, value);
    }

    public void WriteCorrelationData(ArraySegment<byte> value)
    {
        WriteBinaryProperty(MqttPropertyId.CorrelationData, value);
    }

    public void WriteMaximumPacketSize(uint value)
    {
        // It is a Protocol Error to include the Maximum Packet Size more than once, or for the value to be set to zero.
        if (value == 0)
        {
            return;
        }

        WriteAsFourByteInteger(MqttPropertyId.MaximumPacketSize, value);
    }

    public void WriteMaximumQoS(MqttQualityOfServiceLevel value)
    {
        // It is a Protocol Error to include Maximum QoS more than once, or to have a value other than 0 or 1. If the Maximum QoS is absent, the Client uses a Maximum QoS of 2.
        if (value == MqttQualityOfServiceLevel.ExactlyOnce)
        {
            return;
        }

        Write(MqttPropertyId.MaximumQoS, value == MqttQualityOfServiceLevel.AtLeastOnce ? (byte)0x1 : (byte)0x0);
    }

    public void WriteMessageExpiryInterval(uint value)
    {
        // If absent, the Application Message does not expire.
        // This library uses 0 to indicate no expiration.
        if (value == 0)
        {
            return;
        }

        WriteAsFourByteInteger(MqttPropertyId.MessageExpiryInterval, value);
    }

    public void WritePayloadFormatIndicator(MqttPayloadFormatIndicator value)
    {
        // 0 (0x00) Byte Indicates that the Payload is unspecified bytes, which is equivalent to not sending a Payload Format Indicator.
        if (value == MqttPayloadFormatIndicator.Unspecified)
        {
            return;
        }

        Write(MqttPropertyId.PayloadFormatIndicator, (byte)value);
    }

    public void WriteReasonString(ArraySegment<byte> value)
    {
        Write(MqttPropertyId.ReasonString, value);
    }

    public void WriteReceiveMaximum(ushort value)
    {
        // It is a Protocol Error to include the Receive Maximum value more than once or for it to have the value 0.
        if (value == 0)
        {
            return;
        }

        Write(MqttPropertyId.ReceiveMaximum, value);
    }

    public void WriteRequestProblemInformation(bool value)
    {
        // If the Request Problem Information is absent, the value of 1 is used.
        if (value)
        {
            return;
        }

        Write(MqttPropertyId.RequestProblemInformation, false);
    }

    public void WriteRequestResponseInformation(bool value)
    {
        // If the Request Response Information is absent, the value of 0 is used.
        if (!value)
        {
            return;
        }

        Write(MqttPropertyId.RequestResponseInformation, true);
    }

    public void WriteResponseInformation(ArraySegment<byte> value)
    {
        Write(MqttPropertyId.ResponseInformation, value);
    }

    public void WriteResponseTopic(ArraySegment<byte> value)
    {
        Write(MqttPropertyId.ResponseTopic, value);
    }

    public void WriteRetainAvailable(bool value)
    {
        if (value)
        {
            // Absence of the flag means it is supported!
            return;
        }

        Write(MqttPropertyId.RetainAvailable, false);
    }

    public void WriteServerKeepAlive(ushort value)
    {
        if (value == 0)
        {
            return;
        }

        Write(MqttPropertyId.ServerKeepAlive, value);
    }

    public void WriteServerReference(ArraySegment<byte> value)
    {
        Write(MqttPropertyId.ServerReference, value);
    }

    public void WriteSessionExpiryInterval(uint value)
    {
        // If the Session Expiry Interval is absent the value 0 is used.
        if (value == 0)
        {
            return;
        }

        WriteAsFourByteInteger(MqttPropertyId.SessionExpiryInterval, value);
    }

    public void WriteSharedSubscriptionAvailable(bool value)
    {
        if (value)
        {
            // Absence of the flag means it is supported!
            return;
        }

        Write(MqttPropertyId.SharedSubscriptionAvailable, false);
    }

    public void WriteSubscriptionIdentifier(uint value)
    {
        WriteAsVariableByteInteger(MqttPropertyId.SubscriptionIdentifier, value);
    }

    public void WriteSubscriptionIdentifiers(ICollection<uint> value)
    {
        if (value == null)
        {
            return;
        }

        foreach (var subscriptionIdentifier in value)
        {
            WriteAsVariableByteInteger(MqttPropertyId.SubscriptionIdentifier, subscriptionIdentifier);
        }
    }

    public void WriteSubscriptionIdentifiersAvailable(bool value)
    {
        if (value)
        {
            // Absence of the flag means it is supported!
            return;
        }

        Write(MqttPropertyId.SubscriptionIdentifiersAvailable, false);
    }

    public void WriteTo(MqttBufferWriter target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.WriteVariableByteInteger((uint)_bufferWriter.Length);
        target.Write(_bufferWriter);
    }

    public void WriteTopicAlias(ushort value)
    {
        // A Topic Alias of 0 is not permitted. A sender MUST NOT send a PUBLISH packet containing a Topic Alias which has the value 0.
        if (value == 0)
        {
            return;
        }

        Write(MqttPropertyId.TopicAlias, value);
    }

    public void WriteTopicAliasMaximum(ushort value)
    {
        // If the Topic Alias Maximum property is absent, the default value is 0.
        if (value == 0)
        {
            return;
        }

        Write(MqttPropertyId.TopicAliasMaximum, value);
    }

    public void WriteUserProperties(List<MqttUserProperty> userProperties)
    {
        if (userProperties == null || userProperties.Count == 0)
        {
            return;
        }

        foreach (var property in userProperties)
        {
            _bufferWriter.WriteByte((byte)MqttPropertyId.UserProperty);
            _bufferWriter.WriteString(property.Name);
            _bufferWriter.WriteString(property.Value);
        }
    }

    public void WriteWildcardSubscriptionAvailable(bool value)
    {
        // If not present, then Wildcard Subscriptions are supported.
        if (value)
        {
            return;
        }

        Write(MqttPropertyId.WildcardSubscriptionAvailable, false);
    }

    public void WriteWillDelayInterval(uint value)
    {
        // If the Will Delay Interval is absent, the default value is 0 and there is no delay before the Will Message is published.
        if (value == 0)
        {
            return;
        }

        WriteAsFourByteInteger(MqttPropertyId.WillDelayInterval, value);
    }

    void Write(MqttPropertyId id, bool value)
    {
        _bufferWriter.WriteByte((byte)id);
        _bufferWriter.WriteByte(value ? (byte)0x1 : (byte)0x0);
    }

    void Write(MqttPropertyId id, byte value)
    {
        _bufferWriter.WriteByte((byte)id);
        _bufferWriter.WriteByte(value);
    }

    void Write(MqttPropertyId id, ushort value)
    {
        _bufferWriter.WriteByte((byte)id);
        _bufferWriter.WriteTwoByteInteger(value);
    }

    void Write(MqttPropertyId id, ArraySegment<byte> value)
    {
        if (value.Count == 0)
        {
            return;
        }

        _bufferWriter.WriteByte((byte)id);
        _bufferWriter.WriteString(value);
    }

    void WriteBinaryProperty(MqttPropertyId id, ArraySegment<byte> value)
    {
        if (value.Count == 0)
        {
            return;
        }

        _bufferWriter.WriteByte((byte)id);
        _bufferWriter.WriteBinary(value);
    }

    void WriteAsFourByteInteger(MqttPropertyId id, uint value)
    {
        _bufferWriter.WriteByte((byte)id);
        _bufferWriter.WriteByte((byte)(value >> 24));
        _bufferWriter.WriteByte((byte)(value >> 16));
        _bufferWriter.WriteByte((byte)(value >> 8));
        _bufferWriter.WriteByte((byte)value);
    }

    void WriteAsVariableByteInteger(MqttPropertyId id, uint value)
    {
        _bufferWriter.WriteByte((byte)id);
        _bufferWriter.WriteVariableByteInteger(value);
    }

    private void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
                _bufferWriter.Dispose();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~MqttV5PropertiesWriter()
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