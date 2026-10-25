using System.Security.Cryptography.X509Certificates;

namespace PartnerBank.Security;

/// <summary>Where a Partner Bank deployable finds its TLS server certificate and the trusted root (outside the repository).</summary>
public sealed class TlsOptions
{
    public string ServerCertificatePath { get; init; } = string.Empty;

    public string ServerCertificatePasswordPath { get; init; } = string.Empty;

    /// <summary>The root that client certificates and the Partner's own servers must chain to (the PoC's neutral trust body).</summary>
    public string TrustRootPath { get; init; } = string.Empty;

    /// <summary>
    /// More roots that the deployable's OWN calls (to its authorization server, its API) may chain to - in a
    /// cluster, the public CA whose certificate the Gateway presents on the browser-facing ports. Never used
    /// for client certificates. Empty on the home PC.
    /// </summary>
    public string[] BackchannelTrustRootPaths { get; init; } = [];

    public X509Certificate2 LoadServerCertificate()
    {
        var path = Required(ServerCertificatePath, nameof(ServerCertificatePath));
        var password = File.ReadAllText(Required(ServerCertificatePasswordPath, nameof(ServerCertificatePasswordPath))).Trim();
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    public X509Certificate2 LoadTrustRoot() =>
        X509CertificateLoader.LoadCertificateFromFile(Required(TrustRootPath, nameof(TrustRootPath)));

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The roots the Partner's own calls accept: its trust root, plus the configured extra
    // │ ones. A separate set from the client-certificate check (which keeps the trust root
    // │ alone): a public CA may vouch for a server name, never for EWP's identity.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public X509Certificate2Collection LoadBackchannelTrustRoots(X509Certificate2 trustRoot)
    {
        var roots = new X509Certificate2Collection(trustRoot);
        foreach (var configured in BackchannelTrustRootPaths)
            roots.Add(X509CertificateLoader.LoadCertificateFromFile(Required(configured, nameof(BackchannelTrustRootPaths))));
        return roots;
    }

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
                $"Tls:{name} must name an existing file (got '{path}'). Create the certificates with EWP's ps\\pki\\New-EwpLocalPki.ps1 - see README.md.");
        return path;
    }
}