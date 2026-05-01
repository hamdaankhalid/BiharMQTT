// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography.X509Certificates;

namespace BiharMQTT.Certificates;

public interface ICertificateProvider
{
    X509Certificate2 GetCertificate();

    /// <summary>
    /// Intermediate certificates the server should present alongside the leaf in the
    /// TLS ServerHello. Returning a non-null collection makes the listener build an
    /// <see cref="System.Net.Security.SslStreamCertificateContext"/> so OpenSSL/Schannel
    /// transmit the full chain — required when peers only trust the root CA.
    /// </summary>
    X509Certificate2Collection GetIntermediateCertificates() => null;
}