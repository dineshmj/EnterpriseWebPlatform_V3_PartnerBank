using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using PartnerBank.Api.Configuration;
using PartnerBank.Api.Data;
using PartnerBank.Api.Distribution;
using PartnerBank.Api.Gold;

namespace PartnerBank.Api.Api;

/// <summary>
/// The API the Partner's OWN window calls (PartnerBank.Web) - only on the localhost port, only with
/// the customer's access token. Every operation is for the customer the token names.
/// </summary>
public static class InternalApi
{
    public const string WindowPolicy = "Window";

    public sealed record ApplyRequest(string? Handoff, string? ProductCode);

    public sealed record ApplicationView(string Number, string ProductCode, string ProductName, string Status, DateTimeOffset SubmittedAt);

    public static void MapInternalApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/internal").RequireAuthorization(WindowPolicy);

        api.MapGet("/products", async (PartnerDbContext db, CancellationToken cancellationToken) =>
            await db.Products.AsNoTracking().OrderBy(p => p.Code).ToListAsync(cancellationToken));

        api.MapPost("/handoffs/{id}/open", OpenHandoffAsync);
        api.MapGet("/handoffs/{id}", HandoffAsync);
        api.MapPost("/applications", ApplyAsync);
        api.MapGet("/applications/{number}", ApplicationAsync);
        api.MapGet("/applications", MyApplicationsAsync);
        api.MapGet("/holdings", MyHoldingsAsync);

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Redeeming a staff view: no customer token exists here (the customer is not signing
        // │ in) - the one-time, 60-second, 256-bit request_uri IS the authorisation, and this
        // │ port is reachable from this machine only (the Partner Bank's own web). Any failure
        // │ is the same 404.
        // └──────────────────────────────────────────────────────────────────────────────────────
        endpoints.MapPost("/internal/staff-views/redeem", RedeemStaffViewAsync).AllowAnonymous();
    }

    public sealed record RedeemRequest(string? RequestUri);

    public sealed record StaffViewPage(
        string AccountNumber, string HolderName, string EwpCustomerNumber, DateTimeOffset OpenedAt, string Distributor,
        string StaffLanId, string StaffBranch, DateTimeOffset ViewedAt, GoldDesk.HoldingView Holding);

    private static async Task<IResult> RedeemStaffViewAsync(
        RedeemRequest request, StaffViews views, GoldDesk desk, TimeProvider clock, CancellationToken cancellationToken)
    {
        var view = views.Redeem(request.RequestUri);
        var account = view is null ? null : await desk.AccountAsync(view.AccountNumber, view.DistributorClientId, cancellationToken);
        if (view is null || account is null)
            return Results.NotFound();
        return Results.Ok(new StaffViewPage(account.Number, account.HolderName, account.EwpCustomerNumber, account.OpenedAt,
            desk.Name(view.DistributorClientId), view.Actor.LanId, view.Actor.Branch, clock.GetUtcNow(),
            await desk.HoldingAsync(account.Number, cancellationToken)));
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The link opens only for the customer it was made for (the token's subject must
    // │ match), only once, and only before it expires. Every failure is the same 404 - the API
    // │ never confirms that a link exists for someone else.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> OpenHandoffAsync(
        string id, ClaimsPrincipal customer, PartnerDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var link = await db.Handoffs.FindAsync([id], cancellationToken);
        var now = clock.GetUtcNow();
        if (link is null || link.CustomerSubject != Subject(customer) || link.OpenedAt is not null || now > link.ExpiresAt)
            return Results.NotFound();

        link.OpenedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> HandoffAsync(
        string id, ClaimsPrincipal customer, PartnerDbContext db, ApiOptions settings, TimeProvider clock, CancellationToken cancellationToken) =>
        await UsableLinkAsync(id, customer, db, settings, clock, cancellationToken) is null ? Results.NotFound() : Results.NoContent();

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ The application carries EWP's reference and client ID from the link, so EWP's server
    // │ can follow its status through the external API - and nobody else can.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> ApplyAsync(
        ApplyRequest request, ClaimsPrincipal customer, PartnerDbContext db, ApiOptions settings, TimeProvider clock, CancellationToken cancellationToken)
    {
        var link = await UsableLinkAsync(request.Handoff, customer, db, settings, clock, cancellationToken);
        if (link is null)
            return Results.NotFound();
        var product = string.IsNullOrWhiteSpace(request.ProductCode) ? null : await db.Products.FindAsync([request.ProductCode], cancellationToken);
        if (product is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["productCode"] = ["Unknown product."] });

        var now = clock.GetUtcNow();
        var application = new ProductApplication
        {
            Number = NewApplicationNumber(now),
            ProductCode = product.Code,
            CustomerSubject = link.CustomerSubject,
            ReferrerClientId = link.ReferrerClientId,
            EwpReference = link.EwpReference,
            SubmittedAt = now,
            DecideAfter = now.AddSeconds(settings.DecisionDelaySeconds)
        };
        db.Applications.Add(application);
        link.ApplicationNumber = application.Number;
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ApplicationView(application.Number, product.Code, product.Name, application.Status, application.SubmittedAt));
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A customer sees only their OWN application.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> ApplicationAsync(
        string number, ClaimsPrincipal customer, PartnerDbContext db, CancellationToken cancellationToken)
    {
        var subject = Subject(customer);
        var application = await db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Number == number && a.CustomerSubject == subject, cancellationToken);
        if (application is null)
            return Results.NotFound();
        var product = await db.Products.FindAsync([application.ProductCode], cancellationToken);
        return Results.Ok(new ApplicationView(application.Number, application.ProductCode, product?.Name ?? application.ProductCode, application.Status, application.SubmittedAt));
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ "My Partner Bank": the signed-in customer's OWN applications and gold - the subject
    // │ comes from the customer's token, so no one can ask for someone else's.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> MyApplicationsAsync(ClaimsPrincipal customer, PartnerDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var subject = Subject(customer);
        var applications = await db.Applications.Where(a => a.CustomerSubject == subject).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (applications.Count(a => a.DecideIfDue(now)) > 0)
            await db.SaveChangesAsync(cancellationToken);
        var names = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Code, p => p.Name, cancellationToken);
        return Results.Ok(applications
            .OrderByDescending(a => a.SubmittedAt)
            .Select(a => new ApplicationView(a.Number, a.ProductCode, names.GetValueOrDefault(a.ProductCode, a.ProductCode), a.Status, a.SubmittedAt)));
    }

    private static async Task<IResult> MyHoldingsAsync(ClaimsPrincipal customer, GoldDesk desk, CancellationToken cancellationToken) =>
        Results.Ok(await desk.HoldingAsync(Subject(customer)!, cancellationToken));

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Only an opened link, for THIS customer, not yet used for an application, and within
    // │ the window's session time.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<Handoff?> UsableLinkAsync(
        string? id, ClaimsPrincipal customer, PartnerDbContext db, ApiOptions settings, TimeProvider clock, CancellationToken cancellationToken)
    {
        var link = string.IsNullOrWhiteSpace(id) ? null : await db.Handoffs.FindAsync([id], cancellationToken);
        return link is null || link.CustomerSubject != Subject(customer) || link.OpenedAt is null || link.ApplicationNumber is not null ||
               clock.GetUtcNow() > link.OpenedAt.Value.AddMinutes(settings.WindowSessionMinutes)
            ? null
            : link;
    }

    private static string? Subject(ClaimsPrincipal customer) => customer.FindFirst("sub")?.Value;

    private static string NewApplicationNumber(DateTimeOffset now)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<char> code = stackalloc char[6];
        for (var i = 0; i < code.Length; i++)
            code[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"PB-{now:yyMMdd}-{new string(code)}";
    }
}