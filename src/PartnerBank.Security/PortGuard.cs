using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PartnerBank.Security;

public static class PortGuard
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ One deployable, two ports, two audiences: the mutual-TLS port serves ONLY the paths
    // │ meant for EWP's server; with exclusive, those paths are also never served on the
    // │ public port. Anything else answers 404 - each port has the smallest possible surface.
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

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Each port serves only ITS path prefixes (e.g. EWP's /api on the mutual-TLS port, the
    // │ window's /internal and the probes' /health on the internal port): a request for
    // │ anything else - or on a port not listed - answers 404.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static IApplicationBuilder UsePathPrefixPerPort(this IApplicationBuilder app, IReadOnlyDictionary<int, PathString[]> prefixesByPort) =>
        app.Use(async (context, next) =>
        {
            if (!prefixesByPort.TryGetValue(context.Connection.LocalPort, out var prefixes) || !prefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
}