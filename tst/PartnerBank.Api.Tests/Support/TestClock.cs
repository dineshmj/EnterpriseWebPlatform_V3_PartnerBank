namespace PartnerBank.Api.Tests.Support;

/// <summary>A clock the test sets: StaffViews, SignedInstructions and GoldDesk read "now" only through TimeProvider.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}