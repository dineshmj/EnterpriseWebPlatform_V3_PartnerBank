using System.Security.Claims;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using PartnerBank.Api.Data;
using PartnerBank.Api.Gold;

namespace PartnerBank.Api.Api;

/// <summary>Digital Gold for EWP's server (external port, mutual TLS, tokens bound to EWP's certificate).</summary>
public static partial class GoldApi
{
    public sealed record OrderView(
        string Number, string Status, decimal AmountAud, decimal PricePerGram, decimal Grams,
        DateTimeOffset QuotedAt, DateTimeOffset QuoteExpiresAt, string? NetworkReference, DateTimeOffset? AllocatedAt, string? CancelReason);

    public sealed record PaymentAdvice(string? NetworkReference, decimal? Amount);

    public sealed record CancelRequest(string? Reason);

    public static void MapGoldApi(this IEndpointRouteBuilder endpoints)
    {
        var gold = endpoints.MapGroup("/api").RequireAuthorization(ExternalApi.EwpPolicy);

        gold.MapPost("/gold-orders", PlaceAsync).RequireAuthorization(ExternalApi.ApplicationsWritePolicy);
        gold.MapGet("/gold-orders/{number}", OrderAsync);
        gold.MapPost("/gold-orders/{number}/payment-advice", PaymentAdviceAsync).RequireAuthorization(ExternalApi.ApplicationsWritePolicy);
        gold.MapPost("/gold-orders/{number}/cancel", CancelAsync).RequireAuthorization(ExternalApi.ApplicationsWritePolicy);
        gold.MapGet("/holdings", HoldingsAsync);

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The gold price is public information: readable without a token, but only on the
        // │ internal port (localhost) - for the authorization server's consent page.
        // └──────────────────────────────────────────────────────────────────────────────────────
        endpoints.MapGet("/internal/gold-price", (TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            return Results.Ok(new { pricePerGram = GoldPrice.PerGram(now), currency = "AUD", at = now });
        }).AllowAnonymous();
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Placing an order needs the CUSTOMER'S token carrying an approved digital_gold_purchase;
    // │ everything about the order comes from that detail.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> PlaceAsync(ClaimsPrincipal caller, GoldDesk desk, CancellationToken cancellationToken)
    {
        var purchase = TokenDetails.Single(caller, "digital_gold_purchase");
        var subject = caller.FindFirst("sub")?.Value;
        var tokenId = caller.FindFirst("jti")?.Value;
        if (purchase is null || subject is null || tokenId is null)
            return Results.Problem("Placing a gold order needs the customer's token with an approved digital_gold_purchase.", statusCode: StatusCodes.Status403Forbidden);

        var (order, created) = await desk.PlaceAsync(purchase, subject, caller.FindFirst("client_id")!.Value, tokenId, cancellationToken);
        return created ? Results.Created($"/api/gold-orders/{order.Number}", View(order)) : Results.Ok(View(order));
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ EWP sees only the orders it referred (its client ID); any other number is a 404.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> OrderAsync(string number, ClaimsPrincipal caller, PartnerDbContext db, CancellationToken cancellationToken)
    {
        var referrer = caller.FindFirst("client_id")!.Value;
        var order = await db.GoldOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Number == number && o.ReferrerClientId == referrer, cancellationToken);
        return order is null ? Results.NotFound() : Results.Ok(View(order));
    }

    private static async Task<IResult> PaymentAdviceAsync(
        string number, PaymentAdvice advice, ClaimsPrincipal caller, GoldDesk desk, CancellationToken cancellationToken)
    {
        if (advice.NetworkReference is null || !NetworkReferencePattern().IsMatch(advice.NetworkReference) ||
            advice.Amount is not { } amount || amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["advice"] = ["networkReference (4-64 letters, digits or hyphens) and amount (AUD, in cents) are required."]
            });
        }

        var result = await desk.ApplyPaymentAsync(number, caller.FindFirst("client_id")!.Value, advice.NetworkReference, amount, cancellationToken);
        return Outcome(result);
    }

    private static async Task<IResult> CancelAsync(
        string number, CancelRequest request, ClaimsPrincipal caller, GoldDesk desk, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 200)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["A reason (1-200 characters) is required."] });

        var result = await desk.CancelAsync(number, caller.FindFirst("client_id")!.Value, reason, cancellationToken);
        return Outcome(result);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Holdings need the CUSTOMER'S token with an approved holdings_view, and show only that
    // │ customer's grams and activity.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> HoldingsAsync(ClaimsPrincipal caller, GoldDesk desk, CancellationToken cancellationToken)
    {
        var subject = caller.FindFirst("sub")?.Value;
        if (TokenDetails.Single(caller, "holdings_view") is null || subject is null)
            return Results.Problem("Holdings need the customer's token with an approved holdings_view.", statusCode: StatusCodes.Status403Forbidden);
        return Results.Ok(await desk.HoldingAsync(subject, cancellationToken));
    }

    private static IResult Outcome(GoldDesk.AdviceResult result) =>
        result.Order is null ? Results.NotFound()
        : result.Conflict is not null ? Results.Problem(result.Conflict, statusCode: StatusCodes.Status409Conflict)
        : Results.Ok(View(result.Order));

    private static OrderView View(GoldOrder order) => new(
        order.Number, order.Status, order.AmountAud, order.PricePerGram, order.Grams,
        order.QuotedAt, order.QuoteExpiresAt, order.NetworkReference, order.AllocatedAt, order.CancelReason);

    [GeneratedRegex("^[A-Za-z0-9-]{4,64}$")]
    private static partial Regex NetworkReferencePattern();
}