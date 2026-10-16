using Microsoft.AspNetCore.Builder;

namespace PartnerBank.Security;

public static class SecurityHeaders
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Browser protections on every page: scripts, styles and images only from this host
    // │ (no inline code - XSS), never framed (clickjacking), no MIME sniffing, no referrer to
    // │ other sites. A response that already set its own policy (IdentityServer's form_post
    // │ page, with its script hash) keeps it.
    // │
    // │ form-action: forms may submit to this site AND to exactly the origins their navigation
    // │ legitimately continues to - Chromium applies form-action to the REDIRECTS that follow a
    // │ form submission too (e.g. "Sign out" redirects to the authorization server; "Approve"
    // │ redirects to the client's registered callback). The origins come from configuration
    // │ (registered redirect addresses), never from a request.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IEnumerable<string>? formActionOrigins = null)
    {
        var formAction = string.Join(' ', new[] { "'self'" }.Concat(Origins(formActionOrigins)));
        var policy = $"default-src 'self'; object-src 'none'; base-uri 'self'; form-action {formAction}; frame-ancestors 'none'";

        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                if (!headers.ContainsKey("Content-Security-Policy"))
                    headers.ContentSecurityPolicy = policy;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
            await next();
        });
    }

    /// <summary>The distinct https origins (scheme, host, port) of the given addresses.</summary>
    private static IEnumerable<string> Origins(IEnumerable<string>? addresses) =>
        (addresses ?? [])
            .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.GetLeftPart(UriPartial.Authority) : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
}