using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CardiTrack.Infrastructure.Security;

namespace CardiTrack.UnitTests.Security;

public class RedisCertificateValidationTests
{
    // Memorystore always trips this pair: the certificate is issued by a CA no trust store
    // knows (chain error) and is presented on a bare private IP (name mismatch).
    private const SslPolicyErrors MemorystoreErrors =
        SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;

    // ── CA bundle validation ────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a certificate")]
    [InlineData("-----BEGIN CERTIFICATE-----\nnot base64\n-----END CERTIFICATE-----")]
    public void Create_RejectsBundleWithoutAUsableCertificate(string pem)
    {
        Assert.Throws<CryptographicException>(() => RedisCertificateValidation.Create(pem));
    }

    // ── Trust decisions ─────────────────────────────────────────────────────────

    [Fact]
    public void Callback_AcceptsCertificateIssuedByThePinnedCa()
    {
        using var ca = CreateCa("CN=memorystore-test-ca");
        using var leaf = IssueLeaf(ca, "CN=redis-instance");

        var validate = RedisCertificateValidation.Create(ca.ExportCertificatePem());

        Assert.True(validate(new object(), leaf, null, MemorystoreErrors));
    }

    [Fact]
    public void Callback_RejectsCertificateFromAnotherCa()
    {
        using var pinnedCa = CreateCa("CN=memorystore-test-ca");
        using var impostorCa = CreateCa("CN=impostor-ca");
        using var leaf = IssueLeaf(impostorCa, "CN=redis-instance");

        var validate = RedisCertificateValidation.Create(pinnedCa.ExportCertificatePem());

        Assert.False(validate(new object(), leaf, null, MemorystoreErrors));
    }

    [Fact]
    public void Callback_RejectsSelfSignedCertificate()
    {
        using var pinnedCa = CreateCa("CN=memorystore-test-ca");
        using var selfSigned = CreateCa("CN=redis-instance");

        var validate = RedisCertificateValidation.Create(pinnedCa.ExportCertificatePem());

        Assert.False(validate(new object(), selfSigned, null, MemorystoreErrors));
    }

    [Fact]
    public void Callback_RejectsExpiredCertificateFromThePinnedCa()
    {
        using var ca = CreateCa("CN=memorystore-test-ca");
        // Inside the CA's own validity window — an issuer cannot sign a certificate that
        // predates it — but already past its notAfter.
        using var expired = IssueLeaf(
            ca,
            "CN=redis-instance",
            notBefore: DateTimeOffset.UtcNow.AddHours(-20),
            notAfter: DateTimeOffset.UtcNow.AddHours(-1));

        var validate = RedisCertificateValidation.Create(ca.ExportCertificatePem());

        // Pinning the issuer must not amount to accepting anything it ever signed.
        Assert.False(validate(new object(), expired, null, MemorystoreErrors));
    }

    [Fact]
    public void Callback_AcceptsEitherCaInABundle()
    {
        // Memorystore publishes a second CA five years in; both must work over the overlap.
        using var currentCa = CreateCa("CN=memorystore-test-ca-1");
        using var rotatedCa = CreateCa("CN=memorystore-test-ca-2");
        var bundle = currentCa.ExportCertificatePem() + "\n" + rotatedCa.ExportCertificatePem();

        var validate = RedisCertificateValidation.Create(bundle);

        using var fromCurrent = IssueLeaf(currentCa, "CN=redis-instance");
        using var fromRotated = IssueLeaf(rotatedCa, "CN=redis-instance");

        Assert.True(validate(new object(), fromCurrent, null, MemorystoreErrors));
        Assert.True(validate(new object(), fromRotated, null, MemorystoreErrors));
    }

    [Fact]
    public void Callback_AcceptsWhenTheStackFoundNoProblem()
    {
        using var ca = CreateCa("CN=memorystore-test-ca");
        using var leaf = IssueLeaf(ca, "CN=redis-instance");

        var validate = RedisCertificateValidation.Create(ca.ExportCertificatePem());

        Assert.True(validate(new object(), leaf, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Callback_AcceptsACertificatePresentedAsTheBaseType()
    {
        using var ca = CreateCa("CN=memorystore-test-ca");
        using var leaf = IssueLeaf(ca, "CN=redis-instance");

        // RemoteCertificateValidationCallback is typed as X509Certificate. SslStream passes the
        // derived type today, but a base instance must not be silently rejected — that would
        // fail every handshake.
#pragma warning disable SYSLIB0057 // obsolete ctor is the only way to build a base instance
        using var asBaseType = new X509Certificate(leaf.RawData);
#pragma warning restore SYSLIB0057

        var validate = RedisCertificateValidation.Create(ca.ExportCertificatePem());

        Assert.True(validate(new object(), asBaseType, null, MemorystoreErrors));
    }

    [Fact]
    public void Callback_RejectsWhenNoCertificateWasPresented()
    {
        using var ca = CreateCa("CN=memorystore-test-ca");

        var validate = RedisCertificateValidation.Create(ca.ExportCertificatePem());

        Assert.False(validate(
            new object(), null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static X509Certificate2 CreateCa(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 IssueLeaf(
        X509Certificate2 issuer,
        string subject,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // id-kp-serverAuth
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var serialNumber = new byte[8];
        RandomNumberGenerator.Fill(serialNumber);

        return request.Create(
            issuer,
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(29),
            serialNumber);
    }
}
