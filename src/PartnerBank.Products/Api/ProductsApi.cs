using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using PartnerBank.Products.Configuration;
using PartnerBank.Products.Data;

namespace PartnerBank.Products.Api;

/// <summary>The API EWP's server calls - only on the mutual-TLS port, only with a token bound to its certificate.</summary>
public static partial class ProductsApi
{
    public const string EwpPolicy = "Ewp";
    public const string ProductsReadPolicy = "ProductsRead";
    public const string ApplicationsWritePolicy = "ApplicationsWrite";

    public sealed record HandoffRequest(string? EwpReference);

    public sealed record HandoffResponse(string HandoffUrl, DateTimeOffset ExpiresAt);

    public sealed record ApplicationView(string Number, string ProductCode, string Status, DateTimeOffset SubmittedAt, DateTimeOffset? DecidedAt);

    public static void MapProductsApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization(EwpPolicy);

        api.MapGet("/products", async (ProductsDbContext db, CancellationToken cancellationToken) =>
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
        HandoffRequest request, ClaimsPrincipal caller, ProductsDbContext db, ProductsOptions settings,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var subject = caller.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            return Results.Problem("A window link needs the customer's own token (one with a subject), not a machine token.", statusCode: StatusCodes.Status403Forbidden);
        if (request.EwpReference is null || !ReferencePattern().IsMatch(request.EwpReference))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ewpReference"] = ["1-64 letters, digits or hyphens."] });

        var now = clock.GetUtcNow();
        var handoff = new Handoff
        {
            Id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            CustomerSubject = subject,
            ReferrerClientId = caller.FindFirst("client_id")!.Value,
            EwpReference = request.EwpReference,
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
        string? ewpReference, ClaimsPrincipal caller, ProductsDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (ewpReference is null || !ReferencePattern().IsMatch(ewpReference))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ewpReference"] = ["1-64 letters, digits or hyphens."] });

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

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex ReferencePattern();
}