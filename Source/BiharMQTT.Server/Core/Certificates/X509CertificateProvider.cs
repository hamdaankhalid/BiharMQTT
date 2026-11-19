// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace BiharMQTT.Certificates;

public class X509CertificateProvider : ICertificateProvider
{
    readonly X509Certificate2 _certificate;
    readonly X509Certificate2Collection _intermediates;

    // SslStreamCertificateContext is documented as expensive-to-build and safe-to-share
    // (https://learn.microsoft.com/dotnet/api/system.net.security.sslstreamcertificatecontext).
    // Build at most once per (offline) mode for the lifetime of this provider so handshakes
    // never pay chain-validation or OCSP-fetch cost again. Lazy<T> with the default
    // ExecutionAndPublication mode gives us thread-safe single-init.
    readonly Lazy<SslStreamCertificateContext> _contextOffline;
    readonly Lazy<SslStreamCertificateContext> _contextOnline;

    public X509CertificateProvider(X509Certificate2 certificate)
        : this(certificate, intermediates: null) { }

    public X509CertificateProvider(X509Certificate2 certificate, X509Certificate2Collection intermediates)
    {
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));

        if (!_certificate.HasPrivateKey)
        {
            throw new ArgumentException("The certificate for TLS encryption must contain the private key.", nameof(certificate));
        }

        _intermediates = intermediates is { Count: > 0 } ? intermediates : null;

        if (_intermediates != null)
        {
            _contextOffline = new Lazy<SslStreamCertificateContext>(
                () => SslStreamCertificateContext.Create(_certificate, _intermediates, offline: true));
            _contextOnline = new Lazy<SslStreamCertificateContext>(
                () => SslStreamCertificateContext.Create(_certificate, _intermediates, offline: false));
        }
    }

    public X509Certificate2 GetCertificate()
    {
        return _certificate;
    }

    public X509Certificate2Collection GetIntermediateCertificates()
    {
        return _intermediates;
    }

    public SslStreamCertificateContext GetServerCertificateContext(bool offline)
    {
        if (_intermediates == null)
        {
            // No chain to ship — caller will wire ServerCertificate directly. Building a
            // single-leaf SslStreamCertificateContext just to cache it adds no value.
            return null;
        }

        return offline ? _contextOffline.Value : _contextOnline.Value;
    }
}
