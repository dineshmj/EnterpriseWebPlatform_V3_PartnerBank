using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using PartnerBank.Api.Configuration;

namespace PartnerBank.Api.Tests.Support;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A THROW-AWAY request-signing key per test run, in a temp folder - never EWP's real key
// │ from %USERPROFILE%\.ewp-pki. The Partner Bank gets only its certificate (the public
// │ part), exactly as it gets ewp-request-signing.cer in real life. Signing is done by
// │ hand (header.payload.signature) so a test can also build what EWP would never send:
// │ "alg": "none", another key, another type, a long life, no jti.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>Plays EWP: signs distributor instructions the way EWP's Partner Products API does.</summary>
internal sealed class InstructionSigner : IDisposable
{
    public const string EwpClientId = "FourWalls.Ewp.PartnerProducts";

    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "partner-bank-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string thumbprint;

    public InstructionSigner()
    {
        Directory.CreateDirectory(folder);
        using var certificate = new CertificateRequest("CN=EWP request signing (test)", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        thumbprint = certificate.Thumbprint;
        var path = Path.Combine(folder, "ewp-request-signing.cer");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        Settings = new ApiOptions { EwpClientId = EwpClientId, EwpRequestSigningCertificatePath = path };
    }

    /// <summary>The Partner Bank API's settings: EWP's client ID and the path to EWP's certificate.</summary>
    public ApiOptions Settings { get; }

    /// <summary>A compact JWS as EWP signs it; each argument can be changed to build a bad one.</summary>
    public string Sign(
        JsonObject instruction, DateTimeOffset issuedAt, string? jti = "generate", TimeSpan? lifetime = null,
        string issuer = EwpClientId, string audience = "partner-api", string type = "partner-instruction+jwt", ECDsa? signWith = null)
    {
        var header = new JsonObject { ["alg"] = "ES256", ["typ"] = type, ["kid"] = thumbprint };
        var iat = issuedAt.ToUnixTimeSeconds();
        var payload = new JsonObject
        {
            ["iss"] = issuer, ["aud"] = audience, ["iat"] = iat, ["exp"] = iat + (long)(lifetime ?? TimeSpan.FromMinutes(5)).TotalSeconds,
            ["instruction"] = instruction.DeepClone()
        };
        if (jti is not null)
            payload["jti"] = jti == "generate" ? Guid.NewGuid().ToString() : jti;

        var signingInput = $"{Base64Url(header)}.{Base64Url(payload)}";
        var signature = (signWith ?? key).SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>The same payload with "alg": "none" and no signature.</summary>
    public string Unsigned(string compact) =>
        $"{Base64Url(new JsonObject { ["alg"] = "none", ["typ"] = "partner-instruction+jwt" })}.{compact.Split('.')[1]}.";

    /// <summary>The same header and signature over a payload changed after signing.</summary>
    public static string Altered(string compact, string from, string to)
    {
        var parts = compact.Split('.');
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1]))).Replace(from, to);
        return $"{parts[0]}.{Base64Url(Encoding.UTF8.GetBytes(payload))}.{parts[2]}";
    }

    public static JsonObject StaffView(string goldAccount = "PB-DGA-7K3M9Q", string customer = "CUST-100001") => new()
    {
        ["type"] = "staff_account_view",
        ["goldAccountNumber"] = goldAccount,
        ["customer"] = new JsonObject { ["ewpCustomerNumber"] = customer },
        ["actor"] = new JsonObject { ["lanId"] = "sophie", ["branch"] = "SYD001" }
    };

    public void Dispose()
    {
        key.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private static string Base64Url(JsonObject value) => Base64Url(Encoding.UTF8.GetBytes(value.ToJsonString()));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Pad(string base64Url)
    {
        var text = base64Url.Replace('-', '+').Replace('_', '/');
        return text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
    }
}