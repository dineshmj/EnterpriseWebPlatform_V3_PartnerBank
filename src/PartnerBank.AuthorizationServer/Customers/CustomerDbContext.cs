using Microsoft.EntityFrameworkCore;

namespace PartnerBank.AuthorizationServer.Customers;

/// <summary>
/// The Partner Bank's customer accounts (sign-in) - kept by the authorization server, apart from the
/// API's business data. A customer referred by another organisation is linked to that organisation's
/// customer number, once.
/// </summary>
public sealed class CustomerDbContext(DbContextOptions<CustomerDbContext> options) : DbContext(options)
{
    public DbSet<PartnerCustomer> Customers => Set<PartnerCustomer>();

    public DbSet<PendingRegistration> PendingRegistrations => Set<PendingRegistration>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<PartnerCustomer>(customer =>
        {
            customer.HasKey(c => c.SubjectId);
            customer.HasIndex(c => c.Username).IsUnique();
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ One Partner Bank account per referred customer: the referring organisation's
            // │ customer number can be linked only once (SQLite treats NULLs as distinct, so
            // │ customers who registered directly are not affected).
            // └──────────────────────────────────────────────────────────────────────────────────────
            customer.HasIndex(c => new { c.ReferrerClientId, c.ReferrerCustomerNumber }).IsUnique();
        });

        model.Entity<PendingRegistration>(pending => pending.HasKey(p => p.Id));
    }
}

public sealed class PartnerCustomer
{
    public string SubjectId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>ASP.NET Core Identity's PasswordHasher (PBKDF2) - never the password itself.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? DateOfBirth { get; set; }

    /// <summary>The organisation that referred and verified this customer (its client ID), if any.</summary>
    public string? ReferrerClientId { get; set; }

    /// <summary>The customer's number at that organisation (e.g. EWP's CUST-100009).</summary>
    public string? ReferrerCustomerNumber { get; set; }

    public bool PhoneVerified { get; set; }

    public int FailedSignIns { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A registration waiting for its one-time code (5 minutes, 5 tries).</summary>
public sealed class PendingRegistration
{
    public string Id { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DateOfBirth { get; set; } = string.Empty;

    public string ReferrerClientId { get; set; } = string.Empty;

    public string ReferrerCustomerNumber { get; set; } = string.Empty;

    /// <summary>The one-time code as a salted SHA-256 hash - the code itself is never stored.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public string CodeSalt { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string ReturnUrl { get; set; } = string.Empty;
}