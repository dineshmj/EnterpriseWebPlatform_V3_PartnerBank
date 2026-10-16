using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using PartnerBank.Api.Configuration;
using PartnerBank.Api.Data;

namespace PartnerBank.Api.Gold;

/// <summary>The Partner Bank's Digital Gold desk: placing orders, receiving payments, allocating grams.</summary>
public sealed class GoldDesk(PartnerDbContext db, TimeProvider clock, ApiOptions settings)
{
    public static readonly TimeSpan QuoteLock = TimeSpan.FromMinutes(5);

    private static readonly CultureInfo Australia = CultureInfo.GetCultureInfo("en-AU");

    public sealed record PlaceResult(GoldOrder Order, bool Created);

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The order comes ONLY from the consented, signed authorisation in the customer's token:
    // │ amount, paying account, EWP's customer and the acting staff member. Nothing is taken
    // │ from a request body. The token's ID makes it once-only: a second call with the same
    // │ token returns the same order.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<PlaceResult> PlaceAsync(JsonObject purchase, string customerSubject, string referrerClientId, string authorizationRef, CancellationToken cancellationToken)
    {
        var existing = await db.GoldOrders.FirstOrDefaultAsync(o => o.AuthorizationRef == authorizationRef, cancellationToken);
        if (existing is not null)
            return new PlaceResult(existing, Created: false);

        var now = clock.GetUtcNow();
        var amount = decimal.Parse((string)purchase["instructedAmount"]!["amount"]!, CultureInfo.InvariantCulture);
        var price = GoldPrice.PerGram(now);
        var order = new GoldOrder
        {
            Number = NewNumber(now),
            CustomerSubject = customerSubject,
            ReferrerClientId = referrerClientId,
            AuthorizationRef = authorizationRef,
            EwpCustomerNumber = (string)purchase["customer"]!["ewpCustomerNumber"]!,
            ActorLanId = (string)purchase["actor"]!["lanId"]!,
            ActorBranch = (string)purchase["actor"]!["branch"]!,
            AmountAud = amount,
            DebtorBsb = (string)purchase["debtorAccount"]!["bsb"]!,
            DebtorAccountNumber = (string)purchase["debtorAccount"]!["accountNumber"]!,
            PricePerGram = price,
            Grams = GoldPrice.GramsFor(amount, price),
            QuotedAt = now,
            QuoteExpiresAt = now + QuoteLock
        };
        db.GoldOrders.Add(order);
        Record(order.CustomerSubject, now, order.Number,
            $"Order {order.Number}: Digital Gold for AUD {Money(amount)} - {order.Grams:0.0000} g at AUD {Money(price)}/g, price locked until {Time(order.QuoteExpiresAt)}. Waiting for payment from your {Name(referrerClientId)} account.");
        await db.SaveChangesAsync(cancellationToken);
        return new PlaceResult(order, Created: true);
    }

    public sealed record AdviceResult(GoldOrder? Order, string? Conflict);

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A payment advice from the referring organisation: only for ITS order, only for the
    // │ exact amount, only while the order awaits payment. The same advice again (same network
    // │ reference) changes nothing - grams are allocated once.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<AdviceResult> ApplyPaymentAsync(string number, string referrerClientId, string networkReference, decimal amount, CancellationToken cancellationToken)
    {
        var order = await db.GoldOrders.FirstOrDefaultAsync(o => o.Number == number && o.ReferrerClientId == referrerClientId, cancellationToken);
        if (order is null)
            return new AdviceResult(null, null);
        if (order.Status == GoldOrder.Allocated && order.NetworkReference == networkReference)
            return new AdviceResult(order, null);
        if (order.Status != GoldOrder.AwaitingPayment)
            return new AdviceResult(order, $"Order {number} is {order.Status}: it does not await a payment.");
        if (amount != order.AmountAud)
            return new AdviceResult(order, $"The payment of AUD {Money(amount)} does not match the order's AUD {Money(order.AmountAud)}.");

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Paid within the lock: the locked grams. Paid later: the grams at today's price (the
        // │ customer still gets gold for every dollar paid) - and the activity says so.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var now = clock.GetUtcNow();
        order.NetworkReference = networkReference;
        order.PaidAt = now;
        Record(order.CustomerSubject, now, order.Number, $"Payment of AUD {Money(amount)} received from {Name(referrerClientId)} (reference {networkReference}).");
        if (now > order.QuoteExpiresAt)
        {
            order.PricePerGram = GoldPrice.PerGram(now);
            order.Grams = GoldPrice.GramsFor(order.AmountAud, order.PricePerGram);
            Record(order.CustomerSubject, now, order.Number,
                $"The price lock had run out: re-quoted at AUD {Money(order.PricePerGram)}/g - {order.Grams:0.0000} g.");
        }

        var holding = await db.GoldHoldings.FindAsync([order.CustomerSubject], cancellationToken);
        if (holding is null)
        {
            holding = new GoldHolding { CustomerSubject = order.CustomerSubject };
            db.GoldHoldings.Add(holding);
        }
        holding.Grams += order.Grams;
        holding.UpdatedAt = now;
        order.Status = GoldOrder.Allocated;
        order.AllocatedAt = now;
        Record(order.CustomerSubject, now, order.Number, $"{order.Grams:0.0000} g of gold allocated to you. You now hold {holding.Grams:0.0000} g.");

        await db.SaveChangesAsync(cancellationToken);
        return new AdviceResult(order, null);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Only the referring organisation, only an order still awaiting payment: allocated gold
    // │ is never taken back through this door.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<AdviceResult> CancelAsync(string number, string referrerClientId, string reason, CancellationToken cancellationToken)
    {
        var order = await db.GoldOrders.FirstOrDefaultAsync(o => o.Number == number && o.ReferrerClientId == referrerClientId, cancellationToken);
        if (order is null)
            return new AdviceResult(null, null);
        if (order.Status == GoldOrder.Cancelled)
            return new AdviceResult(order, null);
        if (order.Status != GoldOrder.AwaitingPayment)
            return new AdviceResult(order, $"Order {number} is {order.Status}: only an order awaiting payment can be cancelled.");

        var now = clock.GetUtcNow();
        order.Status = GoldOrder.Cancelled;
        order.CancelledAt = now;
        order.CancelReason = reason;
        Record(order.CustomerSubject, now, order.Number, $"Order {order.Number} cancelled by {Name(referrerClientId)}: {reason}");
        await db.SaveChangesAsync(cancellationToken);
        return new AdviceResult(order, null);
    }

    public sealed record HoldingView(decimal Grams, decimal PricePerGram, decimal ValueAud, DateTimeOffset PricedAt, IReadOnlyList<ActivityView> Activity);

    public sealed record ActivityView(DateTimeOffset At, string Text, string? Reference);

    /// <summary>A customer's grams, today's value (the grams never change with the price) and their latest activity.</summary>
    public async Task<HoldingView> HoldingAsync(string customerSubject, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var grams = (await db.GoldHoldings.FindAsync([customerSubject], cancellationToken))?.Grams ?? 0m;
        var price = GoldPrice.PerGram(now);
        var activity = await db.Activity.AsNoTracking()
            .Where(a => a.CustomerSubject == customerSubject)
            .OrderByDescending(a => a.Id)
            .Take(50)
            .Select(a => new ActivityView(a.At, a.Text, a.Reference))
            .ToListAsync(cancellationToken);
        return new HoldingView(grams, price, decimal.Round(grams * price, 2), now, activity);
    }

    private void Record(string customerSubject, DateTimeOffset at, string reference, string text) =>
        db.Activity.Add(new ActivityEntry { CustomerSubject = customerSubject, At = at, Reference = reference, Text = text });

    private static string Money(decimal amount) => amount.ToString("N2", Australia);

    /// <summary>The referring organisation as the customer knows it (e.g. "Four Walls"), not its technical client ID.</summary>
    private string Name(string referrerClientId) => referrerClientId == settings.EwpClientId ? settings.EwpDisplayName : referrerClientId;

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ Times are STORED in UTC; a time written into a customer's activity sentence is in
    // │ Sydney time - the same as the times the window shows next to each entry.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static string Time(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Sydney).ToString("h:mm:ss tt", Australia) + " (Sydney time)";

    private static string NewNumber(DateTimeOffset now)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<char> code = stackalloc char[6];
        for (var i = 0; i < code.Length; i++)
            code[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"PB-G-{now:yyMMdd}-{new string(code)}";
    }
}