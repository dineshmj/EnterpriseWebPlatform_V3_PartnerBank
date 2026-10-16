using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using PartnerBank.AuthorizationServer.Configuration;

namespace PartnerBank.AuthorizationServer.Customers;

public enum SignInOutcome
{
    Success,
    Invalid,
    Locked
}

public enum CodeOutcome
{
    Registered,
    WrongCode,
    Expired,
    TooManyAttempts,
    AlreadyRegistered
}

/// <summary>The Partner Bank's customer accounts: sign-in with lockout, and registration with a one-time code.</summary>
public sealed partial class CustomerStore(CustomerDbContext db, TimeProvider clock, IPasswordHasher<PartnerCustomer> hasher, ILogger<CustomerStore> logger)
{
    public const int MaxFailedSignIns = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public const int MaxCodeAttempts = 5;
    public const int MinPasswordLength = 12;

    public Task<PartnerCustomer?> FindAsync(string subjectId, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.SubjectId == subjectId, cancellationToken);

    public Task<PartnerCustomer?> FindLinkedAsync(string referrerClientId, string referrerCustomerNumber, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.ReferrerClientId == referrerClientId && c.ReferrerCustomerNumber == referrerCustomerNumber, cancellationToken);

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Password check with lockout: after 5 wrong passwords the account is locked for 15
    // │ minutes (password guessing); a correct password resets the count. An unknown username
    // │ costs the same hashing work as a known one, so timing does not reveal which usernames
    // │ exist.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<(SignInOutcome Outcome, PartnerCustomer? Customer)> SignInAsync(string username, string password, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Username == username.Trim().ToLowerInvariant(), cancellationToken);
        if (customer is null)
        {
            hasher.HashPassword(new PartnerCustomer(), password);
            return (SignInOutcome.Invalid, null);
        }
        if (customer.LockedUntil > now)
            return (SignInOutcome.Locked, null);

        if (hasher.VerifyHashedPassword(customer, customer.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            customer.FailedSignIns++;
            if (customer.FailedSignIns >= MaxFailedSignIns)
            {
                customer.LockedUntil = now + Lockout;
                customer.FailedSignIns = 0;
                logger.LogWarning("Partner Bank account {Username} locked for {Minutes} minutes after {Count} wrong passwords.", customer.Username, Lockout.TotalMinutes, MaxFailedSignIns);
            }
            await db.SaveChangesAsync(cancellationToken);
            return (SignInOutcome.Invalid, null);
        }

        customer.FailedSignIns = 0;
        customer.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);
        return (SignInOutcome.Success, customer);
    }

    /// <summary>Why a chosen username or password is not acceptable - or null when both are.</summary>
    public async Task<string?> CheckNewCredentialsAsync(string username, string password, string confirm, CancellationToken cancellationToken)
    {
        if (!UsernamePattern().IsMatch(username))
            return "Choose a username of 3-30 lower-case letters, digits, dots or hyphens.";
        if (await db.Customers.AnyAsync(c => c.Username == username, cancellationToken))
            return "That username is taken - choose another.";
        if (password.Length < MinPasswordLength)
            return $"Choose a password of at least {MinPasswordLength} characters.";
        if (password != confirm)
            return "The two passwords are not the same.";
        return null;
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Registration for a customer the referring organisation verified: the identity comes
    // │ from its SIGNED request (never typed by the person), the password is hashed at once,
    // │ and the account exists only after the one-time code (6 digits, 5 minutes, 5 tries,
    // │ stored as a salted hash). The code is returned to the caller ONLY to be sent - in this
    // │ PoC, shown in Development in place of an SMS.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<(PendingRegistration Pending, string Code)> StartRegistrationAsync(
        string username, string password, string name, string dateOfBirth, string referrerClientId, string referrerCustomerNumber,
        string returnUrl, CancellationToken cancellationToken)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var salt = RandomNumberGenerator.GetBytes(16);
        var pending = new PendingRegistration
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            Username = username,
            PasswordHash = hasher.HashPassword(new PartnerCustomer { Username = username }, password),
            Name = name,
            DateOfBirth = dateOfBirth,
            ReferrerClientId = referrerClientId,
            ReferrerCustomerNumber = referrerCustomerNumber,
            CodeSalt = Convert.ToBase64String(salt),
            CodeHash = Hash(salt, code),
            ExpiresAt = clock.GetUtcNow() + CodeLifetime,
            ReturnUrl = returnUrl
        };
        db.PendingRegistrations.Add(pending);
        await db.SaveChangesAsync(cancellationToken);
        return (pending, code);
    }

    public Task<PendingRegistration?> FindPendingAsync(string id, CancellationToken cancellationToken) =>
        db.PendingRegistrations.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<(CodeOutcome Outcome, PartnerCustomer? Customer)> CompleteRegistrationAsync(string id, string code, CancellationToken cancellationToken)
    {
        var pending = await db.PendingRegistrations.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (pending is null || clock.GetUtcNow() > pending.ExpiresAt)
            return (CodeOutcome.Expired, null);
        if (pending.Attempts >= MaxCodeAttempts)
            return (CodeOutcome.TooManyAttempts, null);

        var expected = Convert.FromBase64String(pending.CodeHash);
        var given = Convert.FromBase64String(Hash(Convert.FromBase64String(pending.CodeSalt), code.Trim()));
        if (!CryptographicOperations.FixedTimeEquals(expected, given))
        {
            pending.Attempts++;
            await db.SaveChangesAsync(cancellationToken);
            return (pending.Attempts >= MaxCodeAttempts ? CodeOutcome.TooManyAttempts : CodeOutcome.WrongCode, null);
        }

        if (await db.Customers.AnyAsync(c => c.ReferrerClientId == pending.ReferrerClientId && c.ReferrerCustomerNumber == pending.ReferrerCustomerNumber, cancellationToken) ||
            await db.Customers.AnyAsync(c => c.Username == pending.Username, cancellationToken))
        {
            db.PendingRegistrations.Remove(pending);
            await db.SaveChangesAsync(cancellationToken);
            return (CodeOutcome.AlreadyRegistered, null);
        }

        var customer = new PartnerCustomer
        {
            SubjectId = $"pb-{Guid.NewGuid():N}",
            Username = pending.Username,
            PasswordHash = pending.PasswordHash,
            Name = pending.Name,
            DateOfBirth = pending.DateOfBirth,
            ReferrerClientId = pending.ReferrerClientId,
            ReferrerCustomerNumber = pending.ReferrerCustomerNumber,
            PhoneVerified = true,
            CreatedAt = clock.GetUtcNow()
        };
        db.Customers.Add(customer);
        db.PendingRegistrations.Remove(pending);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Partner Bank customer {Username} registered, referred by {Referrer} ({ReferrerCustomer}).",
            customer.Username, customer.ReferrerClientId, customer.ReferrerCustomerNumber);
        return (CodeOutcome.Registered, customer);
    }

    /// <summary>Development: the demo customers from appsettings become accounts (once).</summary>
    public async Task SeedAsync(IEnumerable<DemoCustomerOptions> demoCustomers, CancellationToken cancellationToken)
    {
        foreach (var demo in demoCustomers)
        {
            if (await db.Customers.AnyAsync(c => c.SubjectId == demo.SubjectId, cancellationToken))
                continue;
            var customer = new PartnerCustomer
            {
                SubjectId = demo.SubjectId,
                Username = demo.Username.ToLowerInvariant(),
                Name = demo.Name,
                CreatedAt = clock.GetUtcNow()
            };
            customer.PasswordHash = hasher.HashPassword(customer, demo.Password);
            db.Customers.Add(customer);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Hash(byte[] salt, string code) =>
        Convert.ToBase64String(SHA256.HashData([.. salt, .. Encoding.UTF8.GetBytes(code)]));

    [GeneratedRegex("^[a-z0-9.-]{3,30}$")]
    private static partial Regex UsernamePattern();
}