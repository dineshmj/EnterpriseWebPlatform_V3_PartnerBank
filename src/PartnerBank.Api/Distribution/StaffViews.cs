using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PartnerBank.Api.Distribution;

// ┌─ :: ARCHITECTURE + SECURITY :: Staff view handoff, PAR-style (doc/Architecture.md D15) ────────
// │ Why : A distributor's staff member at the counter must be able to open the Partner Bank's OWN
// │       page about one customer's account - without a Partner Bank sign-in of their own, and
// │       without anything in the browser's address that could be changed or reused.
// │ How : Like Pushed Authorization Requests (RFC 9126): the distributor PUSHES a signed request
// │       (mutual TLS, its bound token, an ES256 instruction naming the account and the staff
// │       member) and gets an opaque, random request_uri, valid for 60 seconds and ONE use. The
// │       browser carries only that request_uri to the Partner Bank's web, which redeems it on the
// │       localhost-only internal port. Only its SHA-256 is kept; each instruction (jti) pushes once.
// │       Kept in memory: a lost handoff costs one click. Every push and redemption is logged.
// │ Ref : RFC 9126 (request_uri, short life, one use) | RFC 9101 (signed request) | RFC 8705
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class StaffViews(TimeProvider clock, ILogger<StaffViews> logger)
{
    public const string RequestUriPrefix = "urn:partner-bank:staff-view:";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public sealed record Pushed(string AccountNumber, string DistributorClientId, Instructions.Actor Actor, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Pushed> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> usedInstructions = new(StringComparer.Ordinal);

    /// <summary>A new request_uri for this view; null when the same signed instruction was pushed before (a replay).</summary>
    public string? Push(string instructionId, string accountNumber, string distributorClientId, Instructions.Actor actor)
    {
        var now = clock.GetUtcNow();
        Sweep(now);
        // The signed instruction lives 5 minutes: remember its jti a little longer than that.
        if (!usedInstructions.TryAdd(instructionId, now.AddMinutes(6)))
            return null;

        var id = Base64Url(RandomNumberGenerator.GetBytes(32));
        pending[Hash(id)] = new Pushed(accountNumber, distributorClientId, actor, now.Add(Lifetime));
        logger.LogInformation("Staff view of {Account} pushed by {Distributor} for {LanId} ({Branch}).", accountNumber, distributorClientId, actor.LanId, actor.Branch);
        return RequestUriPrefix + id;
    }

    /// <summary>The pushed view, ONCE: removed as it is redeemed; null when unknown, used or expired.</summary>
    public Pushed? Redeem(string? requestUri)
    {
        if (requestUri is null || !requestUri.StartsWith(RequestUriPrefix, StringComparison.Ordinal) || requestUri.Length > RequestUriPrefix.Length + 64)
            return null;
        if (!pending.TryRemove(Hash(requestUri[RequestUriPrefix.Length..]), out var view) || clock.GetUtcNow() > view.ExpiresAt)
            return null;
        logger.LogInformation("Staff view of {Account} opened by {LanId} ({Branch}) of {Distributor}.", view.AccountNumber, view.Actor.LanId, view.Actor.Branch, view.DistributorClientId);
        return view;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, view) in pending)
            if (now > view.ExpiresAt) pending.TryRemove(key, out _);
        foreach (var (key, until) in usedInstructions)
            if (now > until) usedInstructions.TryRemove(key, out _);
    }

    private static string Hash(string id) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(id)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}