// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server;

public sealed class MqttServerClientDisconnectOptionsBuilder
{
    readonly MqttServerClientDisconnectOptions _options = new MqttServerClientDisconnectOptions();

    public MqttServerClientDisconnectOptions Build()
    {
        return _options;
    }

    public MqttServerClientDisconnectOptionsBuilder WithReasonCode(MqttDisconnectReasonCode value)
    {
        _options.ReasonCode = value;
        return this;
    }

    public MqttServerClientDisconnectOptionsBuilder WithReasonString(string value)
    {
        _options.ReasonString = value;
        return this;
    }

    public MqttServerClientDisconnectOptionsBuilder WithServerReference(string value)
    {
        _options.ServerReference = value;
        return this;
    }

    public MqttServerClientDisconnectOptionsBuilder WithUserProperties(List<MqttUserProperty> value)
    {
        _options.UserProperties = value;
        return this;
    }

    static ArraySegment<byte> ToSegment(string s) => s == null ? default : new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(s));

    [Obsolete("Please use more performance `WithUserProperty` with ArraySegment<byte> or ReadOnlyMemory<byte> for the value.")]
    public MqttServerClientDisconnectOptionsBuilder WithUserProperty(string name, string value)
    {
        if (_options.UserProperties == null)
        {
            _options.UserProperties = new List<MqttUserProperty>();
        }

        _options.UserProperties.Add(new MqttUserProperty(ToSegment(name), ToSegment(value)));
        return this;
    }

    public MqttServerClientDisconnectOptionsBuilder WithUserProperty(string name, ReadOnlyMemory<byte> value)
    {
        if (_options.UserProperties == null)
        {
            _options.UserProperties = new List<MqttUserProperty>();
        }

        _options.UserProperties.Add(new MqttUserProperty(ToSegment(name), new ArraySegment<byte>(value.ToArray())));
        return this;
    }

    public MqttServerClientDisconnectOptionsBuilder WithUserProperty(string name, ArraySegment<byte> value)
    {
        if (_options.UserProperties == null)
        {
            _options.UserProperties = new List<MqttUserProperty>();
        }

        _options.UserProperties.Add(new MqttUserProperty(ToSegment(name), value));
        return this;
    }
}