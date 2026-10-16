using PartnerBank.Security;

namespace PartnerBank.Web.Configuration;

/// <summary>The "PartnerBank" section of the window's appsettings.</summary>
public sealed class WebOptions
{
    public const string SectionName = "PartnerBank";

    /// <summary>The Partner Bank's authorization server (its public port), e.g. https://partner-auth.ewp.home.arpa:47401.</summary>
    public string Authority { get; init; } = string.Empty;

    /// <summary>The Partner Bank API's internal port (localhost only), e.g. https://partner-api.ewp.home.arpa:47404.</summary>
    public string ApiInternalBaseUrl { get; init; } = string.Empty;

    /// <summary>The address the Partner's own host names resolve to when this window calls the authorization server and the API (127.0.0.1: same machine).</summary>
    public string? ResolvePartnerHostsTo { get; init; }

    public TlsOptions Tls { get; init; } = new();

    public string WindowClientId { get; init; } = string.Empty;

    public string WindowClientSecret { get; init; } = string.Empty;

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Fail closed at start-up: the authorization server and the API must be https addresses,
    // │ and the window's client credentials must be set.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public void Validate()
    {
        foreach (var (name, value) in new[] { (nameof(Authority), Authority), (nameof(ApiInternalBaseUrl), ApiInternalBaseUrl) })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"PartnerBank:{name} must be an https address.");
        }
        if (string.IsNullOrWhiteSpace(WindowClientId) || string.IsNullOrWhiteSpace(WindowClientSecret))
            throw new InvalidOperationException("PartnerBank:WindowClientId and WindowClientSecret are required.");
    }
}