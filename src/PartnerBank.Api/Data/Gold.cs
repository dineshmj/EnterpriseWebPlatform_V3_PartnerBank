namespace PartnerBank.Api.Data;

/// <summary>
/// A Digital Gold account the Partner Bank opened on a DISTRIBUTOR's signed instruction (Form VII,
/// signed by the customer at the distributor's counter). The Partner Bank is its system of record;
/// the distributor only places purchases for it and reads its holding.
/// </summary>
public sealed class GoldAccount
{
    public long Id { get; set; }

    /// <summary>The account number, e.g. PB-DGA-7K3M9Q - also the key of its holding and activity.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>The distributor that introduced the customer (its client ID) - the only one that sees this account.</summary>
    public string DistributorClientId { get; set; } = string.Empty;

    public string EwpCustomerNumber { get; set; } = string.Empty;

    public string HolderName { get; set; } = string.Empty;

    public DateOnly DateOfBirth { get; set; }

    /// <summary>The customer's mobile number from Form VII: notices go there; each transfer slip must carry the same number.</summary>
    public string Mobile { get; set; } = string.Empty;

    public string FormNumber { get; set; } = string.Empty;

    public DateOnly FormSignedOn { get; set; }

    /// <summary>SHA-256 of the signed form's scan, as the distributor keeps it: the evidence of the customer's application and consent.</summary>
    public string FormScanSha256 { get; set; } = string.Empty;

    /// <summary>The signed instruction the account was opened with (its jti): one instruction, one account.</summary>
    public string InstructionId { get; set; } = string.Empty;

    public string ActorLanId { get; set; } = string.Empty;

    public string ActorBranch { get; set; } = string.Empty;

    public DateTimeOffset OpenedAt { get; set; }
}

/// <summary>
/// A Digital Gold order, placed from ONE authorisation - the customer's consented token, or a
/// distributor's signed purchase instruction (the customer's transfer authorization slip): the AUD
/// amount is fixed by it; the grams are locked for a few minutes at the price of the moment the
/// order is placed, and allocated once the payment has arrived.
/// </summary>
public sealed class GoldOrder
{
    public const string AwaitingPayment = "AWAITING_PAYMENT";
    public const string Allocated = "ALLOCATED";
    public const string Cancelled = "CANCELLED";

    public long Id { get; set; }

    public string Number { get; set; } = string.Empty;

    /// <summary>The holder: the customer's sign-in subject, or the Digital Gold account number (an account opened by a distributor).</summary>
    public string CustomerSubject { get; set; } = string.Empty;

    public string ReferrerClientId { get; set; } = string.Empty;

    /// <summary>The token or the signed instruction the order was placed with (its jti): one authorisation, one order.</summary>
    public string AuthorizationRef { get; set; } = string.Empty;

    public string EwpCustomerNumber { get; set; } = string.Empty;

    /// <summary>The Digital Gold account (an order placed by a distributor).</summary>
    public string? GoldAccountNumber { get; set; }

    /// <summary>The customer's transfer authorization slip: its number (once only) and the SHA-256 of its scan.</summary>
    public string? SlipNumber { get; set; }

    public string? SlipScanSha256 { get; set; }

    /// <summary>The distributor's commission on this purchase (AUD), payable once the grams are allocated.</summary>
    public decimal CommissionAud { get; set; }

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