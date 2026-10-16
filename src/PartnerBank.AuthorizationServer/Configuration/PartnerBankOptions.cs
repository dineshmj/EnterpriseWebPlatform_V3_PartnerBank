using System.Security.Cryptography.X509Certificates;

using PartnerBank.Security;

namespace PartnerBank.AuthorizationServer.Configuration;

/// <summary>The "PartnerBank" section of appsettings: who we are, our TLS material, and the clients we serve.</summary>
public sealed class PartnerBankOptions
{
    public const string SectionName = "PartnerBank";

    /// <summary>The issuer every token names, e.g. https://partner-auth.ewp.home.arpa:47401 (fixed - never taken from the request).</summary>
    public string IssuerUri { get; init; } = string.Empty;

    /// <summary>Where EWP's server reaches the mutual-TLS endpoints (PAR, token), e.g. https://partner-auth.ewp.home.arpa:47411 - the Mtls port of Kestrel:Endpoints.</summary>
    public string MtlsBaseUrl { get; init; } = string.Empty;

    public TlsOptions Tls { get; init; } = new();

    public EwpClientOptions EwpClient { get; init; } = new();

    public ProductsWindowClientOptions ProductsWindowClient { get; init; } = new();

    public List<DemoCustomerOptions> DemoCustomers { get; init; } = [];
}

/// <summary>EWP (Four Walls Inc.), the one organisation allowed to show our products.</summary>
public sealed class EwpClientOptions
{
    public string ClientId { get; init; } = string.Empty;

    /// <summary>The exact subject of EWP's client certificate (RFC 8705 tls_client_auth).</summary>
    public string CertificateSubject { get; init; } = string.Empty;

    /// <summary>EWP's request-signing certificate (public part only): its key verifies EWP's signed authorization requests (JAR).</summary>
    public string RequestSigningCertificatePath { get; init; } = string.Empty;

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Fail closed: without EWP's signing certificate the server does not start - it never
    // │ falls back to accepting unsigned requests.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public X509Certificate2 LoadRequestSigningCertificate()
    {
        var path = Environment.ExpandEnvironmentVariables(RequestSigningCertificatePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(
                $"PartnerBank:EwpClient:RequestSigningCertificatePath must name an existing file (got '{path}'). Copy ewp-request-signing.cer from EWP's %USERPROFILE%\\.ewp-pki - see README.md.");
        return X509CertificateLoader.LoadCertificateFromFile(path);
    }

    public List<string> RedirectUris { get; init; } = [];
}

/// <summary>The Partner Bank's own products window (PartnerBank.Products) - a client of this server like any other.</summary>
public sealed class ProductsWindowClientOptions
{
    public string ClientId { get; init; } = string.Empty;

    public string Secret { get; init; } = string.Empty;

    public List<string> RedirectUris { get; init; } = [];

    public List<string> PostLogoutRedirectUris { get; init; } = [];
}

/// <summary>A Partner Bank customer for the demo sign-in (Development only).</summary>
public sealed class DemoCustomerOptions
{
    public string SubjectId { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}