using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PartnerBank.Security;

// ┌─ :: OBSERVABILITY + SECURITY :: Liveness and readiness endpoints ──────────────────────────────
// │ Why : The orchestrator must restart a dead process, but must not restart one whose database
// │       volume is only briefly unavailable - it should just stop sending it traffic. And a health
// │       answer must not tell an attacker what is inside.
// │ How : /health/live (the process works) and /health/ready (it can do its job: its SQLite
// │       database opens) are separate, chosen by tag. The body lists each check's name and status
// │       only - no exception, no connection string, no file path. Anonymous by design: only the
// │       kubelet's probes and the cluster's network reach the ports that serve them (the API: its
// │       internal port, the authorization server and the window: their public port).
// │       The same shape as EWP's endpoints (EWP src/Common/Observability/HealthEndpoints.cs).
// │ Ref : doc/Containers.md (run-time contract) | doc/Kubernetes.md | OWASP A05
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Kubernetes-style health endpoints, the same on every Partner Bank deployable:
///  - GET /health/live  : liveness  - is the process working? (failing -> restart it)
///  - GET /health/ready : readiness - can it do its job now? (failing -> no traffic)
/// Healthy and Degraded answer 200, Unhealthy 503.
/// </summary>
public static class HealthEndpoints
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    /// <summary>The checks every deployable has: "self" (the process answers), for both probes.</summary>
    public static IHealthChecksBuilder AddPartnerHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag, ReadyTag]);

    /// <summary>Maps /health/live and /health/ready (anonymous, not cached).</summary>
    public static IEndpointRouteBuilder MapPartnerHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", Options(LiveTag)).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", Options(ReadyTag)).AllowAnonymous();
        return endpoints;
    }

    private static HealthCheckOptions Options(string tag) => new()
    {
        Predicate = check => check.Tags.Contains(tag),
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        },
        ResponseWriter = WriteResponseAsync
    };

    // SEC: names and statuses only - a failing check's exception stays in the log.
    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString() })
        }));
    }
}