using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using PartnerBank.Api.Configuration;

namespace PartnerBank.Api.Distribution;

/// <summary>
/// Checks a distributor instruction: a JSON Web Token EWP signed with its request-signing key
/// (ES256), sent as the body of the call (application/jwt).
/// </summary>
public sealed class SignedInstructions
{
    public const string MediaType = "application/jwt";
    public const string TokenType = "partner-instruction+jwt";
    public const string Audience = "partner-api";

    private readonly ApiOptions settings;
    private readonly TimeProvider clock;
    private readonly JsonWebTokenHandler handler = new();
    private readonly TokenValidationParameters parameters;

    public sealed record Checked(JsonObject Instruction, string InstructionId);

    public SignedInstructions(ApiOptions settings, TimeProvider clock)
    {
        this.settings = settings;
        this.clock = clock;

        var path = Environment.ExpandEnvironmentVariables(settings.EwpRequestSigningCertificatePath);
        if (!File.Exists(path))
            throw new InvalidOperationException($"EWP's request-signing certificate was not found at {path} (copy ewp-request-signing.cer from EWP's ps\\pki output).");
        var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
        var key = certificate.GetECDsaPublicKey()
            ?? throw new InvalidOperationException("EWP's request-signing certificate must hold an ECDSA (P-256) key.");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Only EWP's signing key (the PUBLIC part, from its certificate), only ES256, only this
        // │ token type and audience, issued by EWP's client ID, within its lifetime. An unsigned
        // │ ("alg": "none"), altered, expired or otherwise-signed instruction is refused - so the
        // │ Partner Bank keeps signed evidence of exactly what EWP instructed (non-repudiation).
        // └──────────────────────────────────────────────────────────────────────────────────────
        parameters = new TokenValidationParameters
        {
            IssuerSigningKey = new ECDsaSecurityKey(key) { KeyId = certificate.Thumbprint },
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidTypes = [TokenType],
            ValidIssuer = settings.EwpClientId,
            ValidAudience = Audience,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>The checked instruction and its ID (jti), or why it was refused.</summary>
    public async Task<(Checked? Result, string? Error)> CheckAsync(string compact, string callerClientId)
    {
        var result = await handler.ValidateTokenAsync(compact, parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
            return (null, "The instruction is not validly signed by EWP (signature, type, issuer, audience or lifetime).");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The signer must be the caller (the token's client): EWP's signature never vouches
        // │ for a call made by someone else. A short life only - signed at most
        // │ InstructionLifetimeSeconds before it expires, never "valid for a year" - and an ID,
        // │ which makes the instruction once-only.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (token.Issuer != callerClientId)
            return (null, "The instruction was signed for another caller.");
        if (string.IsNullOrWhiteSpace(token.Id) || token.Id.Length > 64)
            return (null, "The instruction needs an ID (jti, at most 64 characters).");
        if (token.IssuedAt == DateTime.MinValue || token.ValidTo - token.IssuedAt > TimeSpan.FromSeconds(settings.Distribution.InstructionLifetimeSeconds) ||
            token.IssuedAt > clock.GetUtcNow().UtcDateTime.AddSeconds(30))
        {
            return (null, $"The instruction must carry iat and expire at most {settings.Distribution.InstructionLifetimeSeconds} seconds after it.");
        }

        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(token.EncodedPayload))) as JsonObject;
        return payload?["instruction"] is JsonObject instruction
            ? (new Checked((JsonObject)instruction.DeepClone(), token.Id), null)
            : (null, "The signed token carries no instruction.");
    }

    /// <summary>The body of the call: a compact JWS of reasonable size, nothing else.</summary>
    public static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), MediaType, StringComparison.OrdinalIgnoreCase))
            return null;
        using var reader = new StreamReader(request.Body, Encoding.ASCII);
        var buffer = new char[16 * 1024 + 1];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);
        return read is 0 or > 16 * 1024 ? null : new string(buffer, 0, read).Trim();
    }
}