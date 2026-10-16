using PartnerBank.Security;

namespace PartnerBank.Api.Configuration;

/// <summary>The "PartnerBank" section of the Partner Bank API's appsettings.</summary>
public sealed class ApiOptions
{
    public const string SectionName = "PartnerBank";

    /// <summary>The Partner Bank's authorization server (its public port), e.g. https://partner-auth.ewp.home.arpa:47401.</summary>
    public string Authority { get; init; } = string.Empty;

    /// <summary>The API for EWP's server - the mutual-TLS port, e.g. https://partner-api.ewp.home.arpa:47403.</summary>
    public string ExternalBaseUrl { get; init; } = string.Empty;

    /// <summary>The API for the Partner's own window - localhost only, e.g. https://partner-api.ewp.home.arpa:47404.</summary>
    public string InternalBaseUrl { get; init; } = string.Empty;

    /// <summary>The Partner's window, for the links EWP opens, e.g. https://partner-products.ewp.home.arpa:47402.</summary>
    public string WindowBaseUrl { get; init; } = string.Empty;

    /// <summary>The address the Partner's own host names resolve to when this deployable calls the authorization server (127.0.0.1: same machine).</summary>
    public string? ResolvePartnerHostsTo { get; init; }

    public TlsOptions Tls { get; init; } = new();

    /// <summary>The only organisation that may call the external API: EWP's client ID at the authorization server.</summary>
    public string EwpClientId { get; init; } = string.Empty;

    /// <summary>The only client that may call the internal API: the Partner's own window.</summary>
    public string WindowClientId { get; init; } = string.Empty;

    /// <summary>How long a window link is valid before it is opened (seconds).</summary>
    public int HandoffLifetimeSeconds { get; init; } = 120;

    /// <summary>How long an opened window stays usable to apply (minutes).</summary>
    public int WindowSessionMinutes { get; init; } = 30;

    /// <summary>Demo: an application is decided (approved) this long after it was submitted (seconds).</summary>
    public int DecisionDelaySeconds { get; init; } = 120;

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Fail closed at start-up: every address must be https, and both client IDs must be
    // │ set - a missing value never leaves a policy open to any client.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public void Validate()
    {
        foreach (var (name, value) in new[]
                 {
                     (nameof(Authority), Authority), (nameof(ExternalBaseUrl), ExternalBaseUrl),
                     (nameof(InternalBaseUrl), InternalBaseUrl), (nameof(WindowBaseUrl), WindowBaseUrl)
                 })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"PartnerBank:{name} must be an https address.");
        }
        if (string.IsNullOrWhiteSpace(EwpClientId) || string.IsNullOrWhiteSpace(WindowClientId))
            throw new InvalidOperationException("PartnerBank:EwpClientId and WindowClientId are required.");
    }
}