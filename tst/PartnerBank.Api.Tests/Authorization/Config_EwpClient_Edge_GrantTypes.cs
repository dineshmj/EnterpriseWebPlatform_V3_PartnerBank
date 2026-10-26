using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using AwesomeAssertions;
using Duende.IdentityServer.Configuration;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Validation;

using PartnerBank.AuthorizationServer.Configuration;

namespace PartnerBank.Api.Tests.Authorization;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ EWP's client must pass IdentityServer's own configuration check in every deployment:
// │ a code-flow client without a redirect URI is refused whole, and then even EWP's
// │ client-credentials calls fail with invalid_client (EWP's cloud demo, 26-OCT-2026).
// │ The code flow is registered only where EWP's callback is configured.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class Config_EwpClient_Edge_GrantTypes
{
    private X509Certificate2 requestSigning = null!;

    [SetUp]
    public void SetUp()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        requestSigning = new CertificateRequest("CN=EWP BSS Request Signing", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [TearDown]
    public void TearDown() => requestSigning.Dispose();

    [Test]
    [Category("Edge")]
    public async Task Given_NoCallbackConfigured_When_EwpsClientIsLoaded_Then_ItIsValidWithClientCredentialsOnly()
    {
        // Given
        var ewp = EwpClient(redirectUris: []);

        // When
        var (client, result) = await LoadAsync(ewp);

        // Then
        result.IsValid.Should().BeTrue(result.ErrorMessage);
        client.AllowedGrantTypes.Should().BeEquivalentTo([GrantType.ClientCredentials]);
    }

    [Test]
    [Category("Happy")]
    public async Task Given_ACallbackConfigured_When_EwpsClientIsLoaded_Then_ItIsValidWithTheCodeFlowToo()
    {
        // Given
        var ewp = EwpClient(redirectUris: ["https://ewp-partner.example/partner/callback"]);

        // When
        var (client, result) = await LoadAsync(ewp);

        // Then
        result.IsValid.Should().BeTrue(result.ErrorMessage);
        client.AllowedGrantTypes.Should().BeEquivalentTo([GrantType.AuthorizationCode, GrantType.ClientCredentials]);
        client.RedirectUris.Should().ContainSingle("https://ewp-partner.example/partner/callback");
    }

    private static EwpClientOptions EwpClient(List<string> redirectUris) => new()
    {
        ClientId = "FourWalls.Ewp.PartnerProducts",
        CertificateSubject = "CN=EWP BSS Partner Products Client, O=Four Walls Inc.",
        RedirectUris = redirectUris
    };

    /// <summary>EWP's client as the server builds it, checked by IdentityServer's own validator (what its client store runs).</summary>
    private async Task<(Client Client, ClientConfigurationValidationContext Result)> LoadAsync(EwpClientOptions ewp)
    {
        var window = new ProductsWindowClientOptions
        {
            ClientId = "PartnerBank.Products",
            Secret = "test-secret",
            RedirectUris = ["https://partner-products.example:47402/signin-oidc"]
        };
        var client = Config.Clients(ewp, requestSigning, window).Single(c => c.ClientId == ewp.ClientId);
        var context = new ClientConfigurationValidationContext(client);
        await new DefaultClientConfigurationValidator(new IdentityServerOptions()).ValidateAsync(context, CancellationToken.None);
        return (client, context);
    }
}