using AwesomeAssertions;

using PartnerBank.Api.Data;
using PartnerBank.Api.Tests.Support;

namespace PartnerBank.Api.Tests.Gold;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A purchase on EWP's signed transfer-slip instruction, once only. EWP's Partner Bank
// │ adapter resends the SAME instruction (same jti) after a time-out - that must return
// │ the same order, never a second order the customer would be asked to pay for. One slip
// │ buys once, even in a new instruction. Only the distributor that introduced the account
// │ can buy for it.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class GoldDesk_PlacePurchaseAsync_Edge_Replays
{
    private GoldDeskDatabase database = null!;
    private string goldAccount = null!;

    [SetUp]
    public async Task SetUp()
    {
        database = new GoldDeskDatabase();
        var opened = await database.Desk().OpenAccountAsync(GoldDeskDatabase.Opening(), GoldDeskDatabase.Ewp, "jti-open-1", CancellationToken.None);
        goldAccount = opened.Account!.Number;
    }

    [TearDown]
    public void TearDown() => database.Dispose();

    [Test]
    [Category("Happy")]
    public async Task Given_AnOpenAccount_When_EwpSendsASignedSlip_Then_OneOrderAwaitsPayment()
    {
        // When
        var result = await database.Desk().PlacePurchaseAsync(GoldDeskDatabase.Purchase(goldAccount), GoldDeskDatabase.Ewp, "jti-buy-1", CancellationToken.None);

        // Then
        result.Created.Should().BeTrue();
        result.Order!.Status.Should().Be(GoldOrder.AwaitingPayment);
        result.Order.AuthorizationRef.Should().Be("jti-buy-1");
        result.Order.CommissionAud.Should().Be(0.10m, "0.5 % of AUD 20.00");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnOrderPlaced_When_TheSameInstructionIsResent_Then_TheSameOrderComesBackAndNothingIsAdded()
    {
        // Given - the first answer was lost on the way back to EWP
        var first = await database.Desk().PlacePurchaseAsync(GoldDeskDatabase.Purchase(goldAccount), GoldDeskDatabase.Ewp, "jti-buy-1", CancellationToken.None);
        var activityBefore = database.Count(db => db.Activity.Count());

        // When
        var resent = await database.Desk().PlacePurchaseAsync(GoldDeskDatabase.Purchase(goldAccount), GoldDeskDatabase.Ewp, "jti-buy-1", CancellationToken.None);

        // Then
        resent.Created.Should().BeFalse();
        resent.Conflict.Should().BeNull("a resend is not an error: EWP gets the answer it lost");
        resent.Order!.Number.Should().Be(first.Order!.Number);
        resent.Order.Grams.Should().Be(first.Order.Grams, "the price locked by the first call stands");
        database.Count(db => db.GoldOrders.Count()).Should().Be(1);
        database.Count(db => db.Activity.Count()).Should().Be(activityBefore, "no second activity entry and no second SMS");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnOrderPlaced_When_TheSameSlipComesInANewInstruction_Then_ItIsRefused()
    {
        // Given
        await database.Desk().PlacePurchaseAsync(GoldDeskDatabase.Purchase(goldAccount), GoldDeskDatabase.Ewp, "jti-buy-1", CancellationToken.None);

        // When - a new jti around the same signed slip
        var copy = await database.Desk().PlacePurchaseAsync(GoldDeskDatabase.Purchase(goldAccount), GoldDeskDatabase.Ewp, "jti-buy-2", CancellationToken.None);

        // Then
        copy.Created.Should().BeFalse();
        copy.Conflict.Should().Contain("has already been used");
        database.Count(db => db.GoldOrders.Count()).Should().Be(1, "one slip, one purchase");
    }

    [Test]
    [Category("Security")]
    public async Task Given_TheSameInstructionCommittedByAnotherRequest_When_ThisRequestSaves_Then_NoSecondOrderIsPlaced()
    {
        // Given - both requests passed the "seen before?" check; the other one commits first
        var commitsFirst = new CommitsFirst(() =>
            database.Desk().PlacePurchaseAsync(GoldDeskDatabase.Purchase(goldAccount), GoldDeskDatabase.Ewp, "jti-buy-1", CancellationToken.None));

        // When
        var result = await database.Desk(commitsFirst).PlacePurchaseAsync(GoldDeskDatabase.Purchase(goldAccount), GoldDeskDatabase.Ewp, "jti-buy-1", CancellationToken.None);

        // Then
        result.Created.Should().BeFalse();
        result.Conflict.Should().Contain("at the same moment");
        database.Count(db => db.GoldOrders.Count()).Should().Be(1, "the unique indexes on AuthorizationRef and SlipNumber refused the second insert");
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnAccountEwpIntroduced_When_AnotherDistributorSendsASlipForIt_Then_TheAccountIsNotFound()
    {
        // When
        var result = await database.Desk().PlacePurchaseAsync(GoldDeskDatabase.Purchase(goldAccount), "Another.Distributor", "jti-buy-1", CancellationToken.None);

        // Then
        result.AccountFound.Should().BeFalse("another distributor's call gets the same 404 as an unknown account");
        database.Count(db => db.GoldOrders.Count()).Should().Be(0);
    }

    [Test]
    [Category("Security")]
    public async Task Given_AnOpenAccount_When_ASlipNamesAnotherCustomer_Then_ItIsRefused()
    {
        // When
        var result = await database.Desk().PlacePurchaseAsync(GoldDeskDatabase.Purchase(goldAccount, customer: "CUST-100002"), GoldDeskDatabase.Ewp, "jti-buy-1", CancellationToken.None);

        // Then
        result.Created.Should().BeFalse();
        result.Conflict.Should().Contain("belongs to another customer");
    }
}