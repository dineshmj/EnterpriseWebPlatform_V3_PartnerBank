using Microsoft.EntityFrameworkCore;

namespace PartnerBank.Api.Data;

/// <summary>The Partner Bank's own data: its products, the one-time window links EWP asked for, and the applications made through them.</summary>
public sealed class PartnerDbContext(DbContextOptions<PartnerDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<Handoff> Handoffs => Set<Handoff>();

    public DbSet<ProductApplication> Applications => Set<ProductApplication>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Product>(product =>
        {
            product.HasKey(p => p.Code);
            product.HasData(
                new Product { Code = "PB-TD-12", Name = "Partner Term Deposit, 12 months", Summary = "4.60% p.a. fixed for 12 months, from AUD 5,000.", Kind = "Deposit" },
                new Product { Code = "PB-SAVER", Name = "Partner Bonus Saver", Summary = "Up to 5.10% p.a. when you add AUD 200 each month.", Kind = "Deposit" });
        });

        model.Entity<Handoff>(handoff =>
        {
            handoff.HasKey(h => h.Id);
            handoff.Property(h => h.Id).HasMaxLength(64);
            handoff.Property(h => h.EwpReference).HasMaxLength(64);
        });

        model.Entity<ProductApplication>(application =>
        {
            application.HasKey(a => a.Id);
            application.HasIndex(a => a.Number).IsUnique();
            application.HasIndex(a => new { a.ReferrerClientId, a.EwpReference });
        });
    }
}

public sealed class Product
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;
}

/// <summary>A one-time link to the products window, created by EWP for ONE signed-in customer.</summary>
public sealed class Handoff
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The customer the link is for (the "sub" of EWP's token) - only they can open it.</summary>
    public string CustomerSubject { get; set; } = string.Empty;

    public string ReferrerClientId { get; set; } = string.Empty;

    /// <summary>EWP's own reference for the customer's journey (e.g. its workflow ID).</summary>
    public string EwpReference { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? OpenedAt { get; set; }

    public string? ApplicationNumber { get; set; }
}

public sealed class ProductApplication
{
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";

    public long Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public string ProductCode { get; set; } = string.Empty;

    public string CustomerSubject { get; set; } = string.Empty;

    public string ReferrerClientId { get; set; } = string.Empty;

    public string EwpReference { get; set; } = string.Empty;

    public string Status { get; set; } = Submitted;

    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>Demo: when the Partner decides. Until then the application stays SUBMITTED.</summary>
    public DateTimeOffset DecideAfter { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Demo decision: once DecideAfter has passed, the application is approved.</summary>
    public bool DecideIfDue(DateTimeOffset now)
    {
        if (Status != Submitted || now < DecideAfter)
            return false;
        Status = Approved;
        DecidedAt = now;
        return true;
    }
}