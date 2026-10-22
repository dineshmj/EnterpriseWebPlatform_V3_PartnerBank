using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using PartnerBank.Api.Distribution;
using PartnerBank.Api.Tests.Support;

namespace PartnerBank.Api.Tests.Distribution;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The staff view handoff (doc/Architecture.md D15, Threat-Model T28): each signed
// │ instruction (its jti) pushes ONCE, and each request_uri opens ONE page, within 60
// │ seconds. Someone who copies the instruction off the wire, or the address off the
// │ staff member's screen, gets nothing. Redeem removes the reference while reading it,
// │ so two requests at the same moment cannot both open the page.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class StaffViews_PushAndRedeem_Edge_OneUse
{
    private const string Account = "PB-DGA-7K3M9Q";

    private static readonly Instructions.Actor Sophie = new("sophie", "SYD001");

    private TestClock clock = null!;
    private StaffViews views = null!;

    [SetUp]
    public void SetUp()
    {
        clock = new TestClock(new DateTimeOffset(2026, 10, 22, 3, 0, 0, TimeSpan.Zero));
        views = new StaffViews(clock, NullLogger<StaffViews>.Instance);
    }

    [Test]
    [Category("Happy")]
    public void Given_APushedView_When_ItIsRedeemed_Then_ItNamesTheAccountTheDistributorAndTheStaffMember()
    {
        // Given
        var requestUri = views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie);

        // When
        var view = views.Redeem(requestUri);

        // Then
        requestUri.Should().StartWith(StaffViews.RequestUriPrefix);
        view.Should().NotBeNull();
        view!.AccountNumber.Should().Be(Account);
        view.DistributorClientId.Should().Be(InstructionSigner.EwpClientId);
        view.Actor.Should().Be(Sophie);
    }

    [Test]
    [Category("Security")]
    public void Given_AnInstructionAlreadyPushed_When_TheSameInstructionIsPushedAgain_Then_NoSecondViewIsIssued()
    {
        // Given
        views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie).Should().NotBeNull();

        // When
        var replayed = views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie);

        // Then
        replayed.Should().BeNull("a replayed signed instruction never yields a second view (the API answers 409)");
    }

    [Test]
    [Category("Security")]
    public void Given_AnInstructionPushed_When_ItIsReplayedLateInItsFiveMinuteLife_Then_ItIsStillRefused()
    {
        // Given - the jti is remembered a little longer than the instruction's 5 minutes (+30 s clock skew)
        views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie);
        clock.Advance(TimeSpan.FromSeconds(5 * 60 + 29));

        // When
        var replayed = views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie);

        // Then
        replayed.Should().BeNull("the instruction is still valid at the signature check, so the jti must still be known here");
    }

    [Test]
    [Category("Security")]
    public void Given_AViewAlreadyOpened_When_TheSameRequestUriIsRedeemedAgain_Then_NothingIsShown()
    {
        // Given
        var requestUri = views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie);
        views.Redeem(requestUri).Should().NotBeNull();

        // When
        var again = views.Redeem(requestUri);

        // Then
        again.Should().BeNull("a request_uri opens one page (reload, back button, copied address: nothing)");
    }

    [Test]
    [Category("Security")]
    public async Task Given_APushedView_When_ManyRedeemsArriveAtTheSameMoment_Then_ExactlyOneOpensIt()
    {
        // Given
        var requestUri = views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie);
        using var start = new ManualResetEventSlim();

        // When - 32 threads wait at the same gate, then all redeem at once
        var attempts = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return views.Redeem(requestUri);
            }))
            .ToList();
        start.Set();
        var opened = await Task.WhenAll(attempts);

        // Then
        opened.Count(v => v is not null).Should().Be(1, "removing the reference while reading it makes the one use atomic");
    }

    [Test]
    [Category("Security")]
    public void Given_APushedView_When_ItIsRedeemedAfterSixtySeconds_Then_NothingIsShown()
    {
        // Given
        var requestUri = views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie);
        clock.Advance(StaffViews.Lifetime + TimeSpan.FromSeconds(1));

        // When
        var late = views.Redeem(requestUri);

        // Then
        late.Should().BeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("urn:partner-bank:staff-view:")]
    [TestCase("urn:partner-bank:staff-view:bWFkZS11cC1yZWZlcmVuY2UtdGhhdC13YXMtbmV2ZXItcHVzaGVk")]
    [TestCase("urn:partner-bank:handoff:bWFkZS11cC1yZWZlcmVuY2UtdGhhdC13YXMtbmV2ZXItcHVzaGVk")]
    [Category("Security")]
    public void Given_APushedView_When_AnotherOrMalformedRequestUriIsRedeemed_Then_NothingIsShown(string? requestUri)
    {
        // Given
        views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie);

        // When
        var view = views.Redeem(requestUri);

        // Then
        view.Should().BeNull("a guessed, empty or other kind of reference gets the same \"not available\"");
    }

    [Test]
    [Category("Security")]
    public void Given_APushedView_When_AnOverlongRequestUriIsRedeemed_Then_NothingIsShown()
    {
        // Given
        var requestUri = views.Push("jti-1", Account, InstructionSigner.EwpClientId, Sophie)!;

        // When
        var view = views.Redeem(requestUri + new string('A', 64));

        // Then
        view.Should().BeNull("the input is bounded before it is hashed");
        views.Redeem(requestUri).Should().NotBeNull("the genuine reference is untouched by the failed attempt");
    }
}