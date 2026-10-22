using AwesomeAssertions;

using PartnerBank.Api.Tests.Support;

namespace PartnerBank.Api.Tests.Gold;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Opening a Digital Gold account on EWP's signed Form VII instruction, once only. EWP
// │ resends the SAME instruction (same jti) when an answer is lost - that must return the
// │ same account, not a second one, and not a second SMS to the customer. The database's
// │ unique indexes are the last word when two copies arrive at the same moment.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class GoldDesk_OpenAccountAsync_Edge_Replays
{
    private GoldDeskDatabase database = null!;

    [SetUp]
    public void SetUp() => database = new GoldDeskDatabase();

    [TearDown]
    public void TearDown() => database.Dispose();

    [Test]
    [Category("Happy")]
    public async Task Given_ANewFormVii_When_EwpSendsTheSignedInstruction_Then_OneAccountIsOpened()
    {
        // When
        var result = await database.Desk().OpenAccountAsync(GoldDeskDatabase.Opening(), GoldDeskDatabase.Ewp, "jti-open-1", CancellationToken.None);

        // Then
        result.Created.Should().BeTrue();
        result.Conflict.Should().BeNull();
        result.Account!.Number.Should().MatchRegex("^PB-DGA-[A-Z0-9]{6}$");
        result.Account.InstructionId.Should().Be("jti-open-1");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnAccountOpened_When_TheSameInstructionIsResent_Then_TheSameAccountComesBackAndNothingIsAdded()
    {
        // Given
        var first = await database.Desk().OpenAccountAsync(GoldDeskDatabase.Opening(), GoldDeskDatabase.Ewp, "jti-open-1", CancellationToken.None);
        var activityBefore = database.Count(db => db.Activity.Count());

        // When
        var resent = await database.Desk().OpenAccountAsync(GoldDeskDatabase.Opening(), GoldDeskDatabase.Ewp, "jti-open-1", CancellationToken.None);

        // Then
        resent.Created.Should().BeFalse();
        resent.Conflict.Should().BeNull("a resend is not an error: EWP gets the answer it lost");
        resent.Account!.Number.Should().Be(first.Account!.Number);
        database.Count(db => db.GoldAccounts.Count()).Should().Be(1);
        database.Count(db => db.Activity.Count()).Should().Be(activityBefore, "no second activity entry and no second SMS");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnAccountOpened_When_TheSameFormComesInANewInstruction_Then_ItIsRefused()
    {
        // Given
        await database.Desk().OpenAccountAsync(GoldDeskDatabase.Opening(), GoldDeskDatabase.Ewp, "jti-open-1", CancellationToken.None);

        // When - a new jti, but the same customer and the same signed Form VII
        var copy = await database.Desk().OpenAccountAsync(GoldDeskDatabase.Opening(), GoldDeskDatabase.Ewp, "jti-open-2", CancellationToken.None);

        // Then
        copy.Created.Should().BeFalse();
        copy.Conflict.Should().Contain("already has Digital Gold account");
        database.Count(db => db.GoldAccounts.Count()).Should().Be(1);
    }

    [Test]
    [Category("Security")]
    public async Task Given_TheSameInstructionCommittedByAnotherRequest_When_ThisRequestSaves_Then_NoSecondAccountIsOpened()
    {
        // Given - both requests passed the "seen before?" check; the other one commits first
        string? otherAccount = null;
        var commitsFirst = new CommitsFirst(async () =>
            otherAccount = (await database.Desk().OpenAccountAsync(GoldDeskDatabase.Opening(), GoldDeskDatabase.Ewp, "jti-open-1", CancellationToken.None)).Account!.Number);

        // When
        var result = await database.Desk(commitsFirst).OpenAccountAsync(GoldDeskDatabase.Opening(), GoldDeskDatabase.Ewp, "jti-open-1", CancellationToken.None);

        // Then
        result.Created.Should().BeFalse();
        result.Conflict.Should().Contain("at the same moment");
        result.Account!.Number.Should().Be(otherAccount, "the loser answers with the account the winner opened");
        database.Count(db => db.GoldAccounts.Count()).Should().Be(1, "the unique index on InstructionId refused the second insert");
    }
}