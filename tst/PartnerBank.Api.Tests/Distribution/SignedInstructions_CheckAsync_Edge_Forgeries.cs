using System.Security.Cryptography;

using AwesomeAssertions;

using PartnerBank.Api.Distribution;
using PartnerBank.Api.Tests.Support;

namespace PartnerBank.Api.Tests.Distribution;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ What makes a distributor instruction once-only and attributable: only EWP's key, only
// │ ES256, only this token type and audience, signed by the CALLER, a short life, and an
// │ ID (jti). The check itself does not remember IDs - the store behind each instruction
// │ does (StaffViews: jti set; GoldDesk: unique InstructionId / AuthorizationRef) - so here
// │ the tests prove the jti is required and handed on unchanged.
// │ The token's lifetime is checked by the token handler against the system clock, the
// │ "iat" rules against the injected clock: the test clock is set to real time.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class SignedInstructions_CheckAsync_Edge_Forgeries
{
    private InstructionSigner ewp = null!;
    private TestClock clock = null!;
    private SignedInstructions instructions = null!;

    [SetUp]
    public void SetUp()
    {
        ewp = new InstructionSigner();
        clock = new TestClock(DateTimeOffset.UtcNow);
        instructions = new SignedInstructions(ewp.Settings, clock);
    }

    [TearDown]
    public void TearDown() => ewp.Dispose();

    [Test]
    [Category("Happy")]
    public async Task Given_AnInstructionSignedByEwp_When_EwpSendsIt_Then_TheInstructionAndItsIdAreHandedOn()
    {
        // Given
        var compact = ewp.Sign(InstructionSigner.StaffView(), clock.GetUtcNow(), jti: "4c1f0f5e-2b7a-4f7e-9d43-0b1f2a6c9e11");

        // When
        var (result, error) = await instructions.CheckAsync(compact, InstructionSigner.EwpClientId);

        // Then
        error.Should().BeNull();
        result!.InstructionId.Should().Be("4c1f0f5e-2b7a-4f7e-9d43-0b1f2a6c9e11", "the jti is what the stores make once-only");
        result.Instruction["type"]!.GetValue<string>().Should().Be("staff_account_view");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnUnsignedCopy_When_ItIsSent_Then_ItIsRefused()
    {
        // Given - "alg": "none", the payload copied from a genuine instruction
        var compact = ewp.Unsigned(ewp.Sign(InstructionSigner.StaffView(), clock.GetUtcNow()));

        // When
        var (result, error) = await instructions.CheckAsync(compact, InstructionSigner.EwpClientId);

        // Then
        result.Should().BeNull();
        error.Should().Contain("not validly signed");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnInstructionSignedWithAnotherKey_When_ItIsSent_Then_ItIsRefused()
    {
        // Given
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var compact = ewp.Sign(InstructionSigner.StaffView(), clock.GetUtcNow(), signWith: stranger);

        // When
        var (result, _) = await instructions.CheckAsync(compact, InstructionSigner.EwpClientId);

        // Then
        result.Should().BeNull();
    }

    [Test]
    [Category("Security")]
    public async Task Given_AGenuineInstruction_When_ItsAccountIsChangedAfterSigning_Then_ItIsRefused()
    {
        // Given
        var genuine = ewp.Sign(InstructionSigner.StaffView(goldAccount: "PB-DGA-7K3M9Q"), clock.GetUtcNow());
        var altered = InstructionSigner.Altered(genuine, "PB-DGA-7K3M9Q", "PB-DGA-ZZZZZZ");

        // When
        var (result, _) = await instructions.CheckAsync(altered, InstructionSigner.EwpClientId);

        // Then
        result.Should().BeNull();
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnInstructionWithoutAnId_When_ItIsSent_Then_ItIsRefused()
    {
        // Given
        var compact = ewp.Sign(InstructionSigner.StaffView(), clock.GetUtcNow(), jti: null);

        // When
        var (result, error) = await instructions.CheckAsync(compact, InstructionSigner.EwpClientId);

        // Then
        result.Should().BeNull("without a jti nothing could tell a replay from a new instruction");
        error.Should().Contain("jti");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnInstructionValidForAnHour_When_ItIsSent_Then_ItIsRefused()
    {
        // Given - validly signed, but a long life would widen the window for a replay
        var compact = ewp.Sign(InstructionSigner.StaffView(), clock.GetUtcNow(), lifetime: TimeSpan.FromHours(1));

        // When
        var (result, error) = await instructions.CheckAsync(compact, InstructionSigner.EwpClientId);

        // Then
        result.Should().BeNull();
        error.Should().Contain("300 seconds");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnInstructionSignedSixMinutesAgo_When_ItIsSent_Then_ItIsRefusedAsExpired()
    {
        // Given
        var compact = ewp.Sign(InstructionSigner.StaffView(), clock.GetUtcNow().AddMinutes(-6));

        // When
        var (result, _) = await instructions.CheckAsync(compact, InstructionSigner.EwpClientId);

        // Then
        result.Should().BeNull("a copied instruction is useless once its 5 minutes are over");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnInstructionSignedByEwp_When_AnotherClientSendsIt_Then_ItIsRefused()
    {
        // Given - EWP's genuine signature, but the call comes with another client's token
        var compact = ewp.Sign(InstructionSigner.StaffView(), clock.GetUtcNow());

        // When
        var (result, error) = await instructions.CheckAsync(compact, "Another.Distributor");

        // Then
        result.Should().BeNull();
        error.Should().Contain("another caller");
    }

    [TestCase("JWT", "partner-api")]
    [TestCase("partner-instruction+jwt", "partner-auth")]
    [Category("Security")]
    public async Task Given_AnEwpSignedTokenOfAnotherKind_When_ItIsSentAsAnInstruction_Then_ItIsRefused(string type, string audience)
    {
        // Given - signed with EWP's key, but meant as something else (another type or audience)
        var compact = ewp.Sign(InstructionSigner.StaffView(), clock.GetUtcNow(), type: type, audience: audience);

        // When
        var (result, _) = await instructions.CheckAsync(compact, InstructionSigner.EwpClientId);

        // Then
        result.Should().BeNull("a token EWP signed for another purpose is never read as an instruction");
    }
}