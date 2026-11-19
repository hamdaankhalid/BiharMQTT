// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace BiharMQTT.Certificates;

public interface ICertificateProvider
{
    X509Certificate2 GetCertificate();

    /// <summary>
    /// Intermediate certificates the server should present alongside the leaf in the
    /// TLS ServerHello. Returning a non-null collection makes the listener build an
    /// <see cref="SslStreamCertificateContext"/> so OpenSSL/Schannel transmit the full
    /// chain — required when peers only trust the root CA.
    /// </summary>
    X509Certificate2Collection GetIntermediateCertificates() => null;

    /// <summary>
    /// Pre-built <see cref="SslStreamCertificateContext"/> the listener can reuse across
    /// connections. Building this object is expensive (chain validation, optional OCSP
    /// fetch when <paramref name="offline"/> is false) and it is explicitly designed by
    /// the framework to be constructed once per certificate and shared. Default returns
    /// null — the listener will fall back to wiring <see cref="SslServerAuthenticationOptions.ServerCertificate"/>
    /// directly, which is fine when no intermediate chain has to be transmitted.
    /// </summary>
    /// <param name="offline">
    /// When true, the runtime does not perform online OCSP/AIA fetches while building the
    /// context (no stapled OCSP response). When false, the runtime may attempt synchronous
    /// network I/O on first build. The listener selects this based on the TLS endpoint's
    /// <c>CheckCertificateRevocation</c> flag.
    /// </param>
    SslStreamCertificateContext GetServerCertificateContext(bool offline) => null;
}