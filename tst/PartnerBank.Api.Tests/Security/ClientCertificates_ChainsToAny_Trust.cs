using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using AwesomeAssertions;

using PartnerBank.Security;

namespace PartnerBank.Api.Tests.Security;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ In a cluster the Partner's own calls meet two kinds of server certificate: its PKI's
// │ (pod to pod) and a public CA's (the Gateway on the browser-facing ports). The calls
// │ may accept both; a client certificate must still chain to the PKI's root alone.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class ClientCertificates_ChainsToAny_Trust
{
    private X509Certificate2 pkiRoot = null!;
    private X509Certificate2 publicRoot = null!;
    private X509Certificate2 otherRoot = null!;

    [SetUp]
    public void SetUp()
    {
        pkiRoot = Root("CN=Test PKI Root");
        publicRoot = Root("CN=Test Public Root");
        otherRoot = Root("CN=Test Other Root");
    }

    [TearDown]
    public void TearDown()
    {
        pkiRoot.Dispose();
        publicRoot.Dispose();
        otherRoot.Dispose();
    }

    [Test]
    [Category("Happy")]
    public void Given_BothRoots_When_TheGatewaysPublicCertificateIsChecked_Then_ItIsTrusted()
    {
        // Given
        using var gateway = Leaf(publicRoot, "CN=partner-auth.partner.example");
        var roots = new X509Certificate2Collection { pkiRoot, publicRoot };

        // When
        var trusted = gateway.ChainsToAny(roots, intermediates: null);

        // Then
        trusted.Should().BeTrue();
    }

    [Test]
    [Category("Happy")]
    public void Given_BothRoots_When_APodsPkiCertificateIsChecked_Then_ItIsTrusted()
    {
        // Given
        using var pod = Leaf(pkiRoot, "CN=partner-api");
        var roots = new X509Certificate2Collection { pkiRoot, publicRoot };

        // When
        var trusted = pod.ChainsToAny(roots, intermediates: null);

        // Then
        trusted.Should().BeTrue();
    }

    [Test]
    [Category("Security")]
    public void Given_BothRoots_When_ACertificateFromAnotherRootIsChecked_Then_ItIsRefused()
    {
        // Given
        using var stranger = Leaf(otherRoot, "CN=partner-auth.partner.example");
        var roots = new X509Certificate2Collection { pkiRoot, publicRoot };

        // When
        var trusted = stranger.ChainsToAny(roots, intermediates: null);

        // Then
        trusted.Should().BeFalse();
    }

    [Test]
    [Category("Security")]
    public void Given_ThePkiRootAlone_When_APublicCaCertificateIsCheckedAsAClient_Then_ItIsRefused()
    {
        // Given - the client-certificate check keeps the PKI's root only
        using var publicClient = Leaf(publicRoot, "CN=EWP BSS Partner Products Client, O=Four Walls Inc.");

        // When
        var trusted = publicClient.ChainsTo(pkiRoot);

        // Then
        trusted.Should().BeFalse("a public CA may vouch for a server name, never for EWP's identity");
    }

    [Test]
    [Category("Happy")]
    public void Given_ExtraRootFiles_When_TheBackchannelRootsAreLoaded_Then_TheTrustRootComesFirstAndTheExtraOnesFollow()
    {
        // Given
        var folder = Directory.CreateTempSubdirectory("pb-roots-");
        try
        {
            var extra = Path.Combine(folder.FullName, "public-root.pem");
            File.WriteAllText(extra, publicRoot.ExportCertificatePem());
            var options = new TlsOptions { BackchannelTrustRootPaths = [extra] };

            // When
            var roots = options.LoadBackchannelTrustRoots(pkiRoot);

            // Then
            roots.Select(root => root.Thumbprint).Should().Equal(pkiRoot.Thumbprint, publicRoot.Thumbprint);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Test]
    [Category("Security")]
    public void Given_AMissingExtraRootFile_When_TheBackchannelRootsAreLoaded_Then_TheStartIsRefused()
    {
        // Given
        var options = new TlsOptions { BackchannelTrustRootPaths = [Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pem")] };

        // When
        var load = () => options.LoadBackchannelTrustRoots(pkiRoot);

        // Then
        load.Should().Throw<InvalidOperationException>().WithMessage("*BackchannelTrustRootPaths*");
    }

    private static X509Certificate2 Root(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 Leaf(X509Certificate2 issuer, string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        return request.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(8));
    }
}