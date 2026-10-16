using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;

using PartnerBank.Security;

namespace PartnerBank.Api.Security;

/// <summary>RFC 8705 certificate-bound access tokens, checked on every API call.</summary>
public static class BoundTokens
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A valid token is not enough: it must be BOUND to the client certificate of THIS
    // │ connection (cnf.x5t#S256 = the certificate's SHA-256 thumbprint), and that certificate
    // │ must chain to the PoC's trust root. The TLS handshake has already proved the caller
    // │ holds the certificate's private key - so a token copied from logs, a proxy or another
    // │ system is useless on its own.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static JwtBearerEvents Events(X509Certificate2 trustRoot) => new()
    {
        OnTokenValidated = async context =>
        {
            var certificate = await context.HttpContext.Connection.GetClientCertificateAsync(context.HttpContext.RequestAborted);
            var bound = BoundThumbprint(context.Principal?.FindFirst("cnf")?.Value);

            if (certificate is null || bound is null)
                context.Fail("The access token must be bound to the client certificate of the connection (cnf.x5t#S256).");
            else if (!certificate.ChainsTo(trustRoot))
                context.Fail("The client certificate does not chain to the trusted root.");
            else if (!string.Equals(bound, certificate.X5tS256(), StringComparison.Ordinal))
                context.Fail("The access token is bound to another certificate.");
        }
    };

    private static string? BoundThumbprint(string? cnf)
    {
        if (string.IsNullOrWhiteSpace(cnf))
            return null;
        try
        {
            using var document = JsonDocument.Parse(cnf);
            return document.RootElement.TryGetProperty("x5t#S256", out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}