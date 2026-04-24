// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using BiharMQTT.Packets;

namespace BiharMQTT.Server.EnhancedAuthentication;

public sealed class ExchangeEnhancedAuthenticationOptionsFactory
{
    readonly ExchangeEnhancedAuthenticationOptions _options = new();

    public ExchangeEnhancedAuthenticationOptions Build()
    {
        return _options;
    }

    public ExchangeEnhancedAuthenticationOptionsFactory WithAuthenticationData(byte[] authenticationData)
    {
        _options.AuthenticationData = authenticationData;

        return this;
    }

    public ExchangeEnhancedAuthenticationOptionsFactory WithAuthenticationData(string authenticationData)
    {
        if (authenticationData == null)
        {
            _options.AuthenticationData = null;
        }
        else
        {
            _options.AuthenticationData = Encoding.UTF8.GetBytes(authenticationData);
        }

        return this;
    }

    public ExchangeEnhancedAuthenticationOptionsFactory WithReasonString(string reasonString)
    {
        _options.ReasonString = reasonString;

        return this;
    }

    public ExchangeEnhancedAuthenticationOptionsFactory WithUserProperties(List<MqttUserProperty> userProperties)
    {
        _options.UserProperties = userProperties;

        return this;
    }

    static ArraySegment<byte> ToSegment(string s) => s == null ? default : new ArraySegment<byte>(Encoding.UTF8.GetBytes(s));

    [Obsolete("Please use more performance `WithUserProperty` with ArraySegment<byte> or ReadOnlyMemory<byte> for the value.")]
    public ExchangeEnhancedAuthenticationOptionsFactory WithUserProperty(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_options.UserProperties == null)
        {
            _options.UserProperties = new List<MqttUserProperty>();
        }

        _options.UserProperties.Add(new MqttUserProperty(ToSegment(name), ToSegment(value)));

        return this;
    }

    public ExchangeEnhancedAuthenticationOptionsFactory WithUserProperty(string name, ReadOnlyMemory<byte> value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_options.UserProperties == null)
        {
            _options.UserProperties = new List<MqttUserProperty>();
        }

        _options.UserProperties.Add(new MqttUserProperty(ToSegment(name), new ArraySegment<byte>(value.ToArray())));

        return this;
    }

    public ExchangeEnhancedAuthenticationOptionsFactory WithUserProperty(string name, ArraySegment<byte> value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_options.UserProperties == null)
        {
            _options.UserProperties = new List<MqttUserProperty>();
        }

        _options.UserProperties.Add(new MqttUserProperty(ToSegment(name), value));

        return this;
    }
}