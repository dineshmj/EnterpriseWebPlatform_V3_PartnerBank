using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PartnerBank.Security;

public static class PortGuard
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ One deployable, two ports, two audiences: the mutual-TLS port serves ONLY the paths
    // │ meant for EWP's server; with exclusive, those paths are also never served on the
    // │ public port (the Products API). Anything else answers 404 - each port has the smallest
    // │ possible surface.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static IApplicationBuilder UsePortGuard(this IApplicationBuilder app, int mtlsPort, Func<PathString, bool> isServerToServer, bool exclusive) =>
        app.Use(async (context, next) =>
        {
            var onMtlsPort = context.Connection.LocalPort == mtlsPort;
            var serverToServer = isServerToServer(context.Request.Path);
            if ((onMtlsPort && !serverToServer) || (exclusive && !onMtlsPort && serverToServer))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
}