// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography.X509Certificates;

namespace BiharMQTT.Certificates;

public class BlobCertificateProvider : ICertificateProvider
{
    public BlobCertificateProvider(byte[] blob)
    {
        Blob = blob ?? throw new ArgumentNullException(nameof(blob));
        // Defer parse to first GetCertificate() call — Password is a settable property on
        // this type and is typically assigned through an object initializer after the ctor
        // runs. Caching after first parse still avoids the per-handshake re-parse that the
        // unmanaged key handle leak was tied to.
    }

    public byte[] Blob { get; }

    public string Password { get; set; }

    public X509Certificate2 GetCertificate()
    {
        // Single-init via lock-free double-checked pattern would require a volatile field;
        // a plain lock here is fine — GetCertificate is called once per provider lifetime
        // off the steady-state hot path (provider is built at server boot, the listener
        // calls this before the first handshake).
        var existing = _cached;
        if (existing != null)
        {
            return existing;
        }

        lock (_cacheLock)
        {
            if (_cached == null)
            {
                _cached = LoadFromBlob();
            }
            return _cached;
        }
    }

    readonly object _cacheLock = new();
    X509Certificate2 _cached;

    X509Certificate2 LoadFromBlob()
    {
#if NET10_0_OR_GREATER
        if (string.IsNullOrEmpty(Password))
        {
            return X509CertificateLoader.LoadCertificate(Blob);
        }

        return X509CertificateLoader.LoadPkcs12(Blob, Password);
#else
        if (string.IsNullOrEmpty(Password))
        {
            // Use a different overload when no password is specified. Otherwise, the constructor will fail.
            return new X509Certificate2(Blob);
        }

        return new X509Certificate2(Blob, Password);
#endif
    }
}
