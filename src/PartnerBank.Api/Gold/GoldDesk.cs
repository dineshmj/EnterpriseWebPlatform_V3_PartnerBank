using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using PartnerBank.Api.Configuration;
using PartnerBank.Api.Data;
using PartnerBank.Api.Distribution;

namespace PartnerBank.Api.Gold;

/// <summary>The Partner Bank's Digital Gold desk: placing orders, receiving payments, allocating grams.</summary>
public sealed class GoldDesk(PartnerDbContext db, TimeProvider clock, ApiOptions settings, ILogger<GoldDesk> logger)
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

    public sealed record OpenResult(GoldAccount? Account, bool Created, string? Conflict);

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Opening a Digital Gold account on a distributor's SIGNED instruction (the customer's
    // │ Form VII). Once only: the same instruction again returns the same account; a second
    // │ account for the same customer, or a second use of the same form, is refused. The
    // │ Partner Bank then tells the CUSTOMER itself (to the mobile on the form) - it did not see
    // │ the form being signed, so the customer hears about the account from the Partner Bank too.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<OpenResult> OpenAccountAsync(Instructions.Opening opening, string distributorClientId, string instructionId, CancellationToken cancellationToken)
    {
        var same = await db.GoldAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.InstructionId == instructionId, cancellationToken);
        if (same is not null)
            return new OpenResult(same, Created: false, null);

        var existing = await AccountOfCustomerAsync(opening.EwpCustomerNumber, distributorClientId, cancellationToken);
        if (existing is not null)
            return new OpenResult(existing, Created: false, $"Customer {opening.EwpCustomerNumber} already has Digital Gold account {existing.Number}.");
        if (await db.GoldAccounts.AnyAsync(a => a.FormNumber == opening.FormNumber, cancellationToken))
            return new OpenResult(null, Created: false, $"Form {opening.FormNumber} has already been used to open an account.");

        var now = clock.GetUtcNow();
        var account = new GoldAccount
        {
            Number = NewCode("PB-DGA-"),
            DistributorClientId = distributorClientId,
            EwpCustomerNumber = opening.EwpCustomerNumber,
            HolderName = opening.Name,
            DateOfBirth = opening.DateOfBirth,
            Mobile = opening.Mobile,
            FormNumber = opening.FormNumber,
            FormSignedOn = opening.FormSignedOn,
            FormScanSha256 = opening.FormScanSha256,
            InstructionId = instructionId,
            ActorLanId = opening.Actor.LanId,
            ActorBranch = opening.Actor.Branch,
            OpenedAt = now
        };
        db.GoldAccounts.Add(account);
        Record(account.Number, now, account.Number,
            $"Digital Gold account {account.Number} opened for {account.HolderName} through {Name(distributorClientId)} (application form {account.FormNumber}, signed {account.FormSignedOn.ToString("d MMMM yyyy", Australia)}).");
        Notify(account, now, $"Partner Bank: your Digital Gold account {account.Number} is open. You applied through {Name(distributorClientId)}. Not you? Call us on 1300 000 000.");

        if (await SaveOnceAsync(cancellationToken))
            return new OpenResult(account, Created: true, null);
        return new OpenResult(await db.GoldAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.InstructionId == instructionId, cancellationToken),
            Created: false, "The same customer or form was processed at the same moment - check the account before trying again.");
    }

    public sealed record PurchaseResult(GoldOrder? Order, bool Created, bool AccountFound, string? Conflict);

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A purchase on a distributor's SIGNED instruction (the customer's transfer authorization
    // │ slip): only for an account THIS distributor introduced and for THAT customer; the mobile
    // │ number written on the slip must be the account's (a second check the Partner Bank can
    // │ make itself); one slip, one purchase; the same instruction again returns the same order.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<PurchaseResult> PlacePurchaseAsync(Instructions.GoldPurchase purchase, string distributorClientId, string instructionId, CancellationToken cancellationToken)
    {
        var same = await db.GoldOrders.AsNoTracking().FirstOrDefaultAsync(o => o.AuthorizationRef == instructionId, cancellationToken);
        if (same is not null)
            return new PurchaseResult(same, Created: false, AccountFound: true, null);

        var account = await AccountAsync(purchase.GoldAccountNumber, distributorClientId, cancellationToken);
        if (account is null)
            return new PurchaseResult(null, Created: false, AccountFound: false, null);
        if (account.EwpCustomerNumber != purchase.EwpCustomerNumber)
            return new PurchaseResult(null, Created: false, AccountFound: true, $"Account {account.Number} belongs to another customer.");
        if (account.Mobile != purchase.SlipMobile)
            return new PurchaseResult(null, Created: false, AccountFound: true, "The mobile number on the transfer slip does not match the account's.");
        if (await db.GoldOrders.AnyAsync(o => o.SlipNumber == purchase.SlipNumber, cancellationToken))
            return new PurchaseResult(null, Created: false, AccountFound: true, $"Transfer slip {purchase.SlipNumber} has already been used.");

        var now = clock.GetUtcNow();
        var price = GoldPrice.PerGram(now);
        var order = new GoldOrder
        {
            Number = NewNumber(now),
            CustomerSubject = account.Number,
            ReferrerClientId = distributorClientId,
            AuthorizationRef = instructionId,
            EwpCustomerNumber = account.EwpCustomerNumber,
            GoldAccountNumber = account.Number,
            SlipNumber = purchase.SlipNumber,
            SlipScanSha256 = purchase.SlipScanSha256,
            ActorLanId = purchase.Actor.LanId,
            ActorBranch = purchase.Actor.Branch,
            AmountAud = purchase.Amount,
            DebtorBsb = purchase.DebtorBsb,
            DebtorAccountNumber = purchase.DebtorAccountNumber,
            PricePerGram = price,
            Grams = GoldPrice.GramsFor(purchase.Amount, price),
            CommissionAud = decimal.Round(purchase.Amount * settings.Distribution.CommissionPercent / 100m, 2, MidpointRounding.AwayFromZero),
            QuotedAt = now,
            QuoteExpiresAt = now + QuoteLock
        };
        db.GoldOrders.Add(order);
        Record(account.Number, now, order.Number,
            $"Order {order.Number}: Digital Gold for AUD {Money(order.AmountAud)} (transfer slip {order.SlipNumber}) - {order.Grams:0.0000} g at AUD {Money(price)}/g, price locked until {Time(order.QuoteExpiresAt)}. Waiting for payment from your {Name(distributorClientId)} account.");
        Notify(account, now, $"Partner Bank: order {order.Number} for AUD {Money(order.AmountAud)} of Digital Gold placed through {Name(distributorClientId)}. Not you? Call us on 1300 000 000.");

        if (await SaveOnceAsync(cancellationToken))
            return new PurchaseResult(order, Created: true, AccountFound: true, null);
        return new PurchaseResult(null, Created: false, AccountFound: true,
            $"Transfer slip {purchase.SlipNumber} or this instruction was processed at the same moment - check the account before trying again.");
    }

    /// <summary>A Digital Gold account - only if THIS distributor introduced it.</summary>
    public Task<GoldAccount?> AccountAsync(string number, string distributorClientId, CancellationToken cancellationToken) =>
        db.GoldAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Number == number && a.DistributorClientId == distributorClientId, cancellationToken);

    public Task<GoldAccount?> AccountOfCustomerAsync(string ewpCustomerNumber, string distributorClientId, CancellationToken cancellationToken) =>
        db.GoldAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.EwpCustomerNumber == ewpCustomerNumber && a.DistributorClientId == distributorClientId, cancellationToken);

    /// <summary>Today's date in Sydney - the date a transfer slip must carry.</summary>
    public DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Sydney).DateTime);

    // The database's unique indexes are the final word on "once only": a race loses here, it never counts twice.
    private async Task<bool> SaveOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ PoC: the SMS is written to the log and to the customer's activity, the number masked
    // │ to its last three digits (no full phone numbers in logs). In production: the SMS provider.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private void Notify(GoldAccount account, DateTimeOffset at, string message)
    {
        logger.LogInformation("SMS to {Mobile} (simulated): {Message}", $"****{account.Mobile[^3..]}", message);
        Record(account.Number, at, account.Number, $"Notice sent by SMS to your mobile ending {account.Mobile[^3..]}: \"{message}\"");
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

    private static string NewNumber(DateTimeOffset now) => NewCode($"PB-G-{now:yyMMdd}-");

    private static string NewCode(string prefix)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<char> code = stackalloc char[6];
        for (var i = 0; i < code.Length; i++)
            code[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return prefix + new string(code);
    }
}