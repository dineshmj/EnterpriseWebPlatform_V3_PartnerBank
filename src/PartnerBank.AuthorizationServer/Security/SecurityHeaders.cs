namespace PartnerBank.AuthorizationServer.Security;

public static class SecurityHeaders
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Browser protections on every page: scripts, styles and images only from this host
    // │ (no inline code - XSS), never framed (clickjacking), forms post only here, no MIME
    // │ sniffing, no referrer to other sites. A response that already set its own policy
    // │ (IdentityServer's form_post page, with its script hash) keeps it.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private const string ContentSecurityPolicy =
        "default-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                if (!headers.ContainsKey("Content-Security-Policy"))
                    headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
            await next();
        });
}