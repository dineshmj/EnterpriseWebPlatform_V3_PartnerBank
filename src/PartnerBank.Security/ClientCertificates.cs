using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;

namespace PartnerBank.Security;

/// <summary>Mutual TLS for every Partner Bank deployable: the TLS defaults and the client-certificate check.</summary>
public static class ClientCertificates
{
    /// <summary>The authentication scheme that validates client certificates.</summary>
    public const string Scheme = "Certificate";

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ TLS 1.2 / 1.3 only, with the Partner Bank's certificate. Whether a port asks for a
    // │ client certificate is set per endpoint (Kestrel:Endpoints - ClientCertificateMode):
    // │ public ports never ask, mutual-TLS ports require one. Kestrel accepts ANY certificate at
    // │ the TLS layer only so that the application decides (and answers with a proper error) -
    // │ a certificate is never trusted there.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static void UsePartnerTls(this KestrelServerOptions kestrel, X509Certificate2 serverCertificate) =>
        kestrel.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificate = serverCertificate;
            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            https.AllowAnyClientCertificate();
        });

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The client-certificate check: it must chain to the PoC's trust root ONLY (not to any
    // │ root Windows trusts), be within its validity period, and carry the client-
    // │ authentication purpose. Revocation is not checked: the PoC's root publishes no
    // │ revocation list (a production trust body does - see README).
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static AuthenticationBuilder AddPartnerClientCertificates(this AuthenticationBuilder authentication, X509Certificate2 trustRoot) =>
        authentication.AddCertificate(Scheme, options =>
        {
            options.AllowedCertificateTypes = CertificateTypes.Chained;
            options.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
            options.CustomTrustStore.Add(trustRoot);
            options.RevocationMode = X509RevocationMode.NoCheck;
            options.ValidateCertificateUse = true;
            options.ValidateValidityPeriod = true;
        });

    /// <summary>True when the certificate chains to the PoC's trust root (and to nothing else).</summary>
    public static bool ChainsTo(this X509Certificate2 certificate, X509Certificate2 trustRoot) =>
        certificate.ChainsToAny(new X509Certificate2Collection(trustRoot), intermediates: null);

    /// <summary>
    /// True when the certificate chains to one of the given roots (and to nothing else). The server's own
    /// intermediates (from the TLS handshake) may complete the chain; they are never trusted as roots.
    /// </summary>
    public static bool ChainsToAny(this X509Certificate2 certificate, X509Certificate2Collection roots, X509Chain? intermediates)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        foreach (var element in intermediates?.ChainElements.Skip(1) ?? [])
            chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        return chain.Build(certificate);
    }

    /// <summary>The RFC 8705 certificate thumbprint (base64url SHA-256 of the DER encoding) - the value of cnf.x5t#S256.</summary>
    public static string X5tS256(this X509Certificate2 certificate) =>
        Convert.ToBase64String(SHA256.HashData(certificate.RawData)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}