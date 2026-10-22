using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using PartnerBank.Api.Configuration;
using PartnerBank.Api.Data;
using PartnerBank.Api.Distribution;
using PartnerBank.Api.Gold;

namespace PartnerBank.Api.Tests.Support;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The Partner Bank's own model on an in-memory SQLite database, one per test - the same
// │ engine the PoC runs on (partner-bank.db). "Once only" is finally enforced by UNIQUE
// │ indexes (InstructionId, AuthorizationRef, SlipNumber): a real relational database is
// │ needed for those to bite, so EF Core's in-memory provider would prove nothing here.
// │ Each Desk() call is a new request: a new DbContext.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>An in-memory Partner Bank database, a clock the test sets, and GoldDesk built the way the API builds it.</summary>
internal sealed class GoldDeskDatabase : IDisposable
{
    public const string Ewp = InstructionSigner.EwpClientId;

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly ApiOptions settings = new() { EwpClientId = Ewp, EwpDisplayName = "Four Walls" };

    public GoldDeskDatabase()
    {
        connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    /// <summary>2 pm in Sydney on 22 October 2026 - the day the forms below were signed.</summary>
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.FromHours(11)));

    public GoldDesk Desk(params IInterceptor[] interceptors) =>
        new(NewContext(interceptors), Clock, settings, NullLogger<GoldDesk>.Instance);

    public PartnerDbContext NewContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<PartnerDbContext>().UseSqlite(connection).AddInterceptors(interceptors).Options);

    public T Count<T>(Func<PartnerDbContext, T> query)
    {
        using var db = NewContext();
        return query(db);
    }

    public static Instructions.Opening Opening(string customer = "CUST-100001", string form = "VII-261022-0001") =>
        new(customer, "Camilla Parker", new DateOnly(1988, 7, 14), "+61400100001", form,
            new DateOnly(2026, 10, 22), new string('a', 64), new Instructions.Actor("sophie", "SYD001"));

    public static Instructions.GoldPurchase Purchase(string goldAccount, string slip = "TAS-261022-0001", string customer = "CUST-100001") =>
        new(goldAccount, customer, 20.00m, "062-000", "12345678", slip,
            new DateOnly(2026, 10, 22), "+61400100001", new string('b', 64), new Instructions.Actor("sophie", "SYD001"));

    public void Dispose() => connection.Dispose();
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The race made deterministic: just before THIS request saves, ANOTHER request with the
// │ same signed instruction runs to the end and commits. Both passed the "seen before?"
// │ check, so only the database's unique index stands between them and a second record.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>Runs <paramref name="other"/> once, just before the DbContext it is attached to saves.</summary>
internal sealed class CommitsFirst(Func<Task> other) : SaveChangesInterceptor
{
    private bool done;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!done)
        {
            done = true;
            await other();
        }
        return result;
    }
}