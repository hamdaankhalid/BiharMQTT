// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography.X509Certificates;

namespace BiharMQTT.Certificates;

public class X509CertificateProvider : ICertificateProvider
{
    readonly X509Certificate2 _certificate;
    readonly X509Certificate2Collection _intermediates;

    public X509CertificateProvider(X509Certificate2 certificate)
        : this(certificate, intermediates: null) { }

    public X509CertificateProvider(X509Certificate2 certificate, X509Certificate2Collection intermediates)
    {
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _intermediates = intermediates is { Count: > 0 } ? intermediates : null;
    }

    public X509Certificate2 GetCertificate()
    {
        return _certificate;
    }

    public X509Certificate2Collection GetIntermediateCertificates()
    {
        return _intermediates;
    }
}