namespace PartnerBank.AuthorizationServer.Security;

public static class MtlsPortGuard
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The mutual-TLS port serves ONLY the server-to-server endpoints (PAR, token,
    // │ revocation, introspection). Pages meant for people - authorize, sign-in, sign-out,
    // │ errors - answer 404 there: everything a browser does stays on the public port, and the
    // │ mTLS port has the smallest possible surface.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static readonly string[] ServerToServer =
    [
        "/connect/par",
        "/connect/token",
        "/connect/revocation",
        "/connect/introspect"
    ];

    public static IApplicationBuilder UseMtlsPortGuard(this IApplicationBuilder app, int mtlsPort) =>
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort == mtlsPort &&
                !ServerToServer.Any(path => context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
}