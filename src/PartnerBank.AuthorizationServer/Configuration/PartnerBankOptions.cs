using System.Security.Cryptography.X509Certificates;

namespace PartnerBank.AuthorizationServer.Configuration;

/// <summary>The "PartnerBank" section of appsettings: who we are, our TLS material, and the one partner we serve.</summary>
public sealed class PartnerBankOptions
{
    public const string SectionName = "PartnerBank";

    /// <summary>The issuer every token names, e.g. https://partner-auth.ewp.home.arpa:47401 (fixed - never taken from the request).</summary>
    public string IssuerUri { get; init; } = string.Empty;

    public TlsOptions Tls { get; init; } = new();

    public EwpClientOptions EwpClient { get; init; } = new();

    public List<DemoCustomerOptions> DemoCustomers { get; init; } = [];
}

/// <summary>Where the TLS server certificate and the trusted root live (outside the repository).</summary>
public sealed class TlsOptions
{
    public string ServerCertificatePath { get; init; } = string.Empty;

    public string ServerCertificatePasswordPath { get; init; } = string.Empty;

    /// <summary>The root that client certificates must chain to (the PoC's neutral trust body).</summary>
    public string TrustRootPath { get; init; } = string.Empty;

    public X509Certificate2 LoadServerCertificate()
    {
        var path = Required(ServerCertificatePath, nameof(ServerCertificatePath));
        var password = File.ReadAllText(Required(ServerCertificatePasswordPath, nameof(ServerCertificatePasswordPath))).Trim();
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    public X509Certificate2 LoadTrustRoot() =>
        X509CertificateLoader.LoadCertificateFromFile(Required(TrustRootPath, nameof(TrustRootPath)));

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Fail closed at start-up: no certificate, no trust root - no server. A missing file is
    // │ named in the message (paths may use %USERPROFILE%), never silently replaced by a
    // │ development certificate.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static string Required(string configured, string name)
    {
        var path = Environment.ExpandEnvironmentVariables(configured ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(
                $"PartnerBank:Tls:{name} must name an existing file (got '{path}'). Create the certificates with EWP's ps\\pki\\New-EwpLocalPki.ps1 - see README.md.");
        return path;
    }
}

/// <summary>EWP (Four Walls Inc.), the one organisation allowed to show our products.</summary>
public sealed class EwpClientOptions
{
    public string ClientId { get; init; } = string.Empty;

    /// <summary>The exact subject of EWP's client certificate (RFC 8705 tls_client_auth).</summary>
    public string CertificateSubject { get; init; } = string.Empty;

    public List<string> RedirectUris { get; init; } = [];
}

/// <summary>A Partner Bank customer for the demo sign-in (Development only).</summary>
public sealed class DemoCustomerOptions
{
    public string SubjectId { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}