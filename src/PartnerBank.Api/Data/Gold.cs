namespace PartnerBank.Api.Data;

/// <summary>
/// A Digital Gold order, placed from ONE consented authorisation (the customer's token): the AUD
/// amount is fixed by the customer's approval; the grams are locked for a few minutes at the price of
/// the moment the order is placed, and allocated once the payment has arrived.
/// </summary>
public sealed class GoldOrder
{
    public const string AwaitingPayment = "AWAITING_PAYMENT";
    public const string Allocated = "ALLOCATED";
    public const string Cancelled = "CANCELLED";

    public long Id { get; set; }

    public string Number { get; set; } = string.Empty;

    /// <summary>The Partner Bank customer (the token's subject).</summary>
    public string CustomerSubject { get; set; } = string.Empty;

    public string ReferrerClientId { get; set; } = string.Empty;

    /// <summary>The token the order was placed with (its jti): one authorisation, one order.</summary>
    public string AuthorizationRef { get; set; } = string.Empty;

    public string EwpCustomerNumber { get; set; } = string.Empty;

    public string ActorLanId { get; set; } = string.Empty;

    public string ActorBranch { get; set; } = string.Empty;

    public decimal AmountAud { get; set; }

    public string DebtorBsb { get; set; } = string.Empty;

    public string DebtorAccountNumber { get; set; } = string.Empty;

    /// <summary>The price per gram the grams were worked out at (the locked quote, or the price at payment if the lock had run out).</summary>
    public decimal PricePerGram { get; set; }

    public decimal Grams { get; set; }

    public DateTimeOffset QuotedAt { get; set; }

    public DateTimeOffset QuoteExpiresAt { get; set; }

    public string Status { get; set; } = AwaitingPayment;

    public string? NetworkReference { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset? AllocatedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public string? CancelReason { get; set; }
}

/// <summary>The grams a customer owns. They change ONLY through allocations - never through a price.</summary>
public sealed class GoldHolding
{
    public string CustomerSubject { get; set; } = string.Empty;

    public decimal Grams { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One line of a customer's activity at the Partner Bank, in the order it happened.</summary>
public sealed class ActivityEntry
{
    public long Id { get; set; }

    public string CustomerSubject { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? Reference { get; set; }
}