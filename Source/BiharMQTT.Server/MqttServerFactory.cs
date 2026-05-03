// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Server.EnhancedAuthentication;

namespace BiharMQTT.Server;

[SuppressMessage("Performance", "CA1822:Mark members as static")]
public sealed class MqttServerFactory
{
    public MqttServerFactory() : this(new MqttNetNullLogger())
    {
    }

    public MqttServerFactory(IMqttNetLogger logger)
    {
        DefaultLogger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IMqttNetLogger DefaultLogger { get; }


    public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

    public ExchangeEnhancedAuthenticationOptionsFactory CreateExchangeExtendedAuthenticationOptionsBuilder()
    {
        return new ExchangeEnhancedAuthenticationOptionsFactory();
    }

    public MqttServer CreateMqttServer(MqttServerOptions options)
    {
        return CreateMqttServer(options, DefaultLogger);
    }

    public MqttServer CreateMqttServer(MqttServerOptions options, IMqttNetLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return new MqttServer(options, logger);
    }

    public MqttServerClientDisconnectOptionsBuilder CreateMqttServerClientDisconnectOptionsBuilder()
    {
        return new MqttServerClientDisconnectOptionsBuilder();
    }

    public MqttServerOptionsBuilder CreateServerOptionsBuilder()
    {
        return new MqttServerOptionsBuilder();
    }
}