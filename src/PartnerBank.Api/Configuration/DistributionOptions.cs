namespace PartnerBank.Api.Configuration;

/// <summary>The terms of EWP's distribution agreement with the Partner Bank ("PartnerBank:Distribution").</summary>
public sealed class DistributionOptions
{
    /// <summary>EWP's commission on each Digital Gold purchase it places, in percent of the amount (e.g. 0.5).</summary>
    public decimal CommissionPercent { get; init; } = 0.5m;

    /// <summary>How long a signed instruction is valid after EWP signed it (seconds).</summary>
    public int InstructionLifetimeSeconds { get; init; } = 300;

    /// <summary>How old a signed Form VII may be when the account is opened (days).</summary>
    public int FormValidDays { get; init; } = 30;
}