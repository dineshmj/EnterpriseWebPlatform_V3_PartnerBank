namespace PartnerBank.Api.Gold;

/// <summary>
/// The demo gold price: about AUD 165 per gram, moving gently (a 90-minute wave of ±1.5%) so a demo
/// shows the price changing while the grams a customer owns stay fixed.
/// </summary>
public static class GoldPrice
{
    private const decimal Base = 165.00m;
    private const double Swing = 0.015;
    private const double PeriodMinutes = 90;

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ A pure function of time, the same in every process: no market feed in the PoC. A real
    // │ partner would take its price from a market-data provider and keep each quote it gave.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static decimal PerGram(DateTimeOffset at)
    {
        var minutes = at.ToUnixTimeSeconds() / 60.0;
        var factor = 1 + Swing * Math.Sin(2 * Math.PI * minutes / PeriodMinutes);
        return decimal.Round(Base * (decimal)factor, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The grams an amount buys at a price, rounded DOWN to 4 decimals (never more gold than was paid for).</summary>
    public static decimal GramsFor(decimal amountAud, decimal pricePerGram) =>
        decimal.Floor(amountAud / pricePerGram * 10_000m) / 10_000m;
}