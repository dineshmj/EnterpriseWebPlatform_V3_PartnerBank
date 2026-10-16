using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace PartnerBank.AuthorizationServer.Authorization;

/// <summary>EWP's request-signing key, registered with this server as a JSON Web Key (RFC 7517).</summary>
public static class RequestSigningKey
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The PUBLIC key only, from EWP's signing certificate: an EC P-256 JWK for ES256, marked
    // │ for signatures, with the certificate's thumbprint as its key ID. EWP names that key ID
    // │ in each request object's header, so the right key is used - and only it.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static string ToJwk(X509Certificate2 certificate)
    {
        using var key = certificate.GetECDsaPublicKey()
            ?? throw new InvalidOperationException("EWP's request-signing certificate must hold an ECDSA (P-256) key.");
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new InvalidOperationException("EWP's request-signing key must be on the P-256 curve (ES256).");

        return new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64Url(parameters.Q.X!),
            ["y"] = Base64Url(parameters.Q.Y!),
            ["kid"] = certificate.Thumbprint,
            ["use"] = "sig",
            ["alg"] = "ES256"
        }.ToJsonString();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}