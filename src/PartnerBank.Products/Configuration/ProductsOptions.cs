using PartnerBank.Security;

namespace PartnerBank.Products.Configuration;

/// <summary>The "PartnerBank" section of the products deployable's appsettings.</summary>
public sealed class ProductsOptions
{
    public const string SectionName = "PartnerBank";

    /// <summary>The Partner Bank's authorization server (its public port), e.g. https://partner-auth.ewp.home.arpa:47401.</summary>
    public string Authority { get; init; } = string.Empty;

    /// <summary>The products window - the public port, e.g. https://partner-products.ewp.home.arpa:47402.</summary>
    public string WindowBaseUrl { get; init; } = string.Empty;

    /// <summary>The API for EWP's server - the mutual-TLS port, e.g. https://partner-api.ewp.home.arpa:47403.</summary>
    public string ApiBaseUrl { get; init; } = string.Empty;

    /// <summary>The address the Partner's own host names resolve to when this deployable calls the authorization server (127.0.0.1: same machine).</summary>
    public string? ResolvePartnerHostsTo { get; init; }

    public TlsOptions Tls { get; init; } = new();

    public string WindowClientId { get; init; } = string.Empty;

    public string WindowClientSecret { get; init; } = string.Empty;

    /// <summary>The only organisation that may call the API: EWP's client ID at the authorization server.</summary>
    public string EwpClientId { get; init; } = string.Empty;

    /// <summary>How long a window link is valid before it is opened (seconds).</summary>
    public int HandoffLifetimeSeconds { get; init; } = 120;

    /// <summary>How long an opened window stays usable to apply (minutes).</summary>
    public int WindowSessionMinutes { get; init; } = 30;

    /// <summary>Demo: an application is decided (approved) this long after it was submitted (seconds).</summary>
    public int DecisionDelaySeconds { get; init; } = 120;

    public void Validate()
    {
        foreach (var (name, value) in new[] { (nameof(Authority), Authority), (nameof(WindowBaseUrl), WindowBaseUrl), (nameof(ApiBaseUrl), ApiBaseUrl) })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"PartnerBank:{name} must be an https address.");
        }
        if (string.IsNullOrWhiteSpace(WindowClientId) || string.IsNullOrWhiteSpace(WindowClientSecret) || string.IsNullOrWhiteSpace(EwpClientId))
            throw new InvalidOperationException("PartnerBank:WindowClientId, WindowClientSecret and EwpClientId are required.");
    }
}