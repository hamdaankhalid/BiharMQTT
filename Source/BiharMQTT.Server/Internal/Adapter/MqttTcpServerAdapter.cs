// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Sockets;
using BiharMQTT.Adapter;
using BiharMQTT.Diagnostics.Logger;

namespace BiharMQTT.Server.Internal.Adapter;

public sealed class MqttTcpServerAdapter : IDisposable
{
    readonly List<MqttTcpServerListener> _listeners = new List<MqttTcpServerListener>();

    CancellationTokenSource _cancellationTokenSource;

    MqttServerOptions _serverOptions;

    public bool TreatSocketOpeningErrorAsWarning { get; set; }

    public void Dispose()
    {
        Cleanup();
    }

    public void Start(MqttServerOptions options, IMqttNetLogger logger, Func<MqttChannelAdapter, Task> clientHandler)
    {
        if (_cancellationTokenSource != null)
        {
            throw new InvalidOperationException("Server is already started.");
        }

        ArgumentNullException.ThrowIfNull(clientHandler);

        _serverOptions = options;

        _cancellationTokenSource = new CancellationTokenSource();

        if (options.DefaultEndpointOptions.IsEnabled)
        {
            RegisterListeners(options.DefaultEndpointOptions, logger, clientHandler, _cancellationTokenSource.Token);
        }

        if (options.TlsEndpointOptions?.IsEnabled == true)
        {
            if (options.TlsEndpointOptions.CertificateProvider == null)
            {
                throw new ArgumentException("TLS certificate is not set.");
            }

            // Resolve the certificate once at boot so misconfiguration (null cert, bad blob,
            // wrong PFX password) surfaces here instead of as a baffling first-handshake
            // failure later. Hot-rotating providers that legitimately produce null until a
            // later moment should override this — but those are not the common shape.
            if (options.TlsEndpointOptions.CertificateProvider.GetCertificate() == null)
            {
                throw new ArgumentException("TLS certificate provider returned a null certificate.");
            }

            RegisterListeners(options.TlsEndpointOptions, logger, clientHandler, _cancellationTokenSource.Token);
        }
    }

    public Task StopAsync()
    {
        Cleanup();
        return Task.CompletedTask;
    }

    void Cleanup()
    {
        try
        {
            _cancellationTokenSource?.Cancel(false);
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            foreach (var listener in _listeners)
            {
                listener.Dispose();
            }

            _listeners.Clear();
        }
    }

    void RegisterListeners(MqttServerTcpEndpointBaseOptions tcpEndpointOptions, IMqttNetLogger logger, Func<MqttChannelAdapter, Task> clientHandler, CancellationToken cancellationToken)
    {
        if (!tcpEndpointOptions.BoundInterNetworkAddress.Equals(IPAddress.None))
        {
            var listenerV4 = new MqttTcpServerListener(AddressFamily.InterNetwork, _serverOptions, tcpEndpointOptions, clientHandler, logger);

            if (listenerV4.Start(TreatSocketOpeningErrorAsWarning, cancellationToken))
            {
                _listeners.Add(listenerV4);
            }
        }

        if (!tcpEndpointOptions.BoundInterNetworkV6Address.Equals(IPAddress.None))
        {
            var listenerV6 = new MqttTcpServerListener(AddressFamily.InterNetworkV6, _serverOptions, tcpEndpointOptions, clientHandler, logger);

            if (listenerV6.Start(TreatSocketOpeningErrorAsWarning, cancellationToken))
            {
                _listeners.Add(listenerV6);
            }
        }
    }
}