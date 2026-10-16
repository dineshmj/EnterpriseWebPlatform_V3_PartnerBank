using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using PartnerBank.Api.Configuration;
using PartnerBank.Api.Data;

namespace PartnerBank.Api.Api;

/// <summary>The API EWP's server calls - only on the mutual-TLS port, only with a token bound to its certificate.</summary>
public static class ExternalApi
{
    public const string EwpPolicy = "Ewp";
    public const string ProductsReadPolicy = "EwpProductsRead";
    public const string ApplicationsWritePolicy = "EwpApplicationsWrite";
    public const string OrdersWritePolicy = "EwpOrdersWrite";

    public sealed record HandoffRequest(string? EwpReference);

    public sealed record HandoffResponse(string HandoffUrl, DateTimeOffset ExpiresAt);

    public sealed record ApplicationView(string Number, string ProductCode, string Status, DateTimeOffset SubmittedAt, DateTimeOffset? DecidedAt);

    public static void MapExternalApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization(EwpPolicy);

        api.MapGet("/products", async (PartnerDbContext db, CancellationToken cancellationToken) =>
                await db.Products.AsNoTracking().OrderBy(p => p.Code).ToListAsync(cancellationToken))
            .RequireAuthorization(ProductsReadPolicy);

        api.MapPost("/handoffs", CreateHandoffAsync).RequireAuthorization(ApplicationsWritePolicy);

        api.MapGet("/applications", ApplicationsAsync).RequireAuthorization(ProductsReadPolicy);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A window link for ONE customer: the subject is taken from the CUSTOMER'S token (the one
    // │ EWP received after the customer signed in here) - EWP cannot name someone else, and a
    // │ machine token (no person) is refused. The link is random (256 bits), valid two minutes,
    // │ and opens once.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> CreateHandoffAsync(
        HandoffRequest request, ClaimsPrincipal caller, PartnerDbContext db, ApiOptions settings,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var subject = caller.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            return Results.Problem("A window link needs the customer's own token (one with a subject), not a machine token.", statusCode: StatusCodes.Status403Forbidden);
        if (!References.IsValid(request.EwpReference))
            return References.Invalid();

        var now = clock.GetUtcNow();
        var handoff = new Handoff
        {
            Id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            CustomerSubject = subject,
            ReferrerClientId = caller.FindFirst("client_id")!.Value,
            EwpReference = request.EwpReference!,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(settings.HandoffLifetimeSeconds)
        };
        db.Handoffs.Add(handoff);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created((string?)null, new HandoffResponse($"{settings.WindowBaseUrl.TrimEnd('/')}/start?handoff={handoff.Id}", handoff.ExpiresAt));
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ EWP sees only the applications IT referred (its client ID), for one of its references -
    // │ never another organisation's.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> ApplicationsAsync(
        string? ewpReference, ClaimsPrincipal caller, PartnerDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!References.IsValid(ewpReference))
            return References.Invalid();

        var referrer = caller.FindFirst("client_id")!.Value;
        var applications = await db.Applications
            .Where(a => a.ReferrerClientId == referrer && a.EwpReference == ewpReference)
            .ToListAsync(cancellationToken);

        var now = clock.GetUtcNow();
        if (applications.Count(a => a.DecideIfDue(now)) > 0)
            await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(applications
            .OrderBy(a => a.SubmittedAt)
            .Select(a => new ApplicationView(a.Number, a.ProductCode, a.Status, a.SubmittedAt, a.DecidedAt)));
    }
}