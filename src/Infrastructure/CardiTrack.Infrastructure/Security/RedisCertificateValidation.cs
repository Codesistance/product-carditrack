using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CardiTrack.Infrastructure.Security;

/// <summary>
/// Server authentication for Memorystore for Redis, which presents a certificate issued by a
/// per-instance certificate authority that no machine trust store knows about.
/// </summary>
public static class RedisCertificateValidation
{
    /// <summary>
    /// Builds a validation callback that accepts the server only when its certificate chains to
    /// one of the certificate authorities in <paramref name="caCertificatePem"/>.
    /// </summary>
    /// <param name="caCertificatePem">
    /// PEM bundle of the instance's CAs. Memorystore issues a second CA five years in for
    /// rotation, so more than one may be present and any of them is acceptable.
    /// </param>
    /// <exception cref="CryptographicException">
    /// The bundle holds no PEM-encoded certificate, or one that cannot be parsed. Thrown while
    /// the container is starting rather than on the first cache write, so a malformed secret
    /// fails the deployment instead of resurfacing later as a connection timeout.
    /// </exception>
    public static RemoteCertificateValidationCallback Create(string caCertificatePem)
    {
        var authorities = new X509Certificate2Collection();
        authorities.ImportFromPem(caCertificatePem);

        if (authorities.Count == 0)
        {
            throw new CryptographicException(
                "No PEM-encoded certificate found in the configured Redis CA bundle.");
        }

        return (_, certificate, certificateChain, sslPolicyErrors) =>
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            if (certificate is null)
            {
                return false;
            }

            // SslStream hands over an X509Certificate2 in practice, but the delegate is typed
            // as the base class — so promote rather than reject. Rejecting a base instance
            // would fail every handshake with nothing to show for it.
            X509Certificate2? promoted = null;
            if (certificate is not X509Certificate2 leaf)
            {
                leaf = promoted =
                    X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            }

            // Only a certificate created here is ours to dispose.
            using var promotedOwner = promoted;
            using var chain = new X509Chain();

            // The instance is addressed by its private IP, which the leaf certificate does not
            // carry, so the built-in name check always fails and StackExchange.Redis's own
            // TrustIssuer helper — which tolerates chain errors only — cannot be used here.
            // Pinning the per-instance CA is what authenticates the server instead: that CA
            // signs the certificate of this one instance and nothing else, and the endpoint is
            // an address on our own VPC that only this instance answers on.
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(authorities);

            // Memorystore's CA publishes neither a CRL nor an OCSP responder, so leaving
            // revocation on would fail every handshake with an offline-revocation error.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            // Any intermediates the server sent, so a chain longer than CA → leaf still builds.
            if (certificateChain is not null)
            {
                foreach (var element in certificateChain.ChainElements)
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            return chain.Build(leaf);
        };
    }
}
