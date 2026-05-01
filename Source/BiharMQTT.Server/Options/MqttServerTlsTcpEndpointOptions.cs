// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Authentication;
using BiharMQTT.Certificates;

namespace BiharMQTT.Server;

public sealed class MqttServerTlsTcpEndpointOptions : MqttServerTcpEndpointBaseOptions
{
    public MqttServerTlsTcpEndpointOptions()
    {
        Port = 8883;
    }

    public System.Net.Security.RemoteCertificateValidationCallback RemoteCertificateValidationCallback { get; set; }

    public ICertificateProvider CertificateProvider { get; set; }

    public bool ClientCertificateRequired { get; set; }

    /// <summary>
    /// Drives two TLS revocation behaviors:
    ///  - peer certificate validation mode (<c>X509RevocationMode.Online</c> when true, <c>NoCheck</c> when false), and
    ///  - the <c>offline</c> flag passed to <c>SslStreamCertificateContext.Create</c> when an
    ///    intermediate chain is configured. False (default) means the stack will not fetch OCSP
    ///    responses for stapling and the context build is fully offline; true allows a synchronous
    ///    network round-trip on the first context build per provider.
    /// Single flag for both knobs by design; deployments that need to split the two should
    /// open an issue.
    /// </summary>
    public bool CheckCertificateRevocation { get; set; }

    /// <summary>
    /// The default value is SslProtocols.None, which allows the operating system to choose the best protocol to use, and to block protocols that are not secure.
    /// </summary>
    /// <seealso href="https://learn.microsoft.com/en-us/dotnet/api/system.security.authentication.sslprotocols">SslProtocols</seealso>
    public SslProtocols SslProtocol { get; set; } = SslProtocols.None;

    public System.Net.Security.CipherSuitesPolicy CipherSuitesPolicy { get; set; }
}