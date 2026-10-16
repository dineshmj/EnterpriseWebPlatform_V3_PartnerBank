using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PartnerBank.Api.Distribution;

/// <summary>
/// The two instructions a distributor may sign, each backed by a paper form the customer signed at
/// its counter: opening a Digital Gold account (Form VII) and a purchase for it (a transfer
/// authorization slip).
/// </summary>
public static partial class Instructions
{
    public const string AccountOpening = "digital_gold_account_opening";
    public const string Purchase = "digital_gold_purchase";
    public const decimal MaxPurchaseAmount = 50_000m;

    public sealed record Actor(string LanId, string Branch);

    public sealed record Opening(
        string EwpCustomerNumber, string Name, DateOnly DateOfBirth, string Mobile,
        string FormNumber, DateOnly FormSignedOn, string FormScanSha256, Actor Actor);

    public sealed record GoldPurchase(
        string GoldAccountNumber, string EwpCustomerNumber, decimal Amount, string DebtorBsb, string DebtorAccountNumber,
        string SlipNumber, DateOnly SlipSignedOn, string SlipMobile, string SlipScanSha256, Actor Actor);

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Strict, allow-list validation even though the instruction is signed: a signature
    // │ proves WHO sent it, not that it is sensible. Exactly the expected fields (an unknown
    // │ field is refused, never ignored), the customer's identity verified by EWP (kycVerified),
    // │ an adult, the form's scan identified by its SHA-256 - the evidence EWP must keep.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static Opening? TryOpening(JsonObject instruction, DateOnly today, int formValidDays, out string? error)
    {
        error = null;
        if (!OnlyFields(instruction, out error, "type", "customer", "form", "actor") || Text(instruction, "type") != AccountOpening)
            return Fail(ref error, $"The instruction must be a {AccountOpening} with customer, form and actor only.");

        if (instruction["customer"] is not JsonObject customer || !OnlyFields(customer, out error, "ewpCustomerNumber", "name", "dateOfBirth", "mobile", "kycVerified"))
            return Fail(ref error, "customer: ewpCustomerNumber, name, dateOfBirth, mobile and kycVerified are required.");
        var number = Text(customer, "ewpCustomerNumber");
        var name = Text(customer, "name");
        var mobile = Text(customer, "mobile");
        if (number is null || !CustomerNumberPattern().IsMatch(number) || name is null || !NamePattern().IsMatch(name) ||
            !Date(customer, "dateOfBirth", out var dateOfBirth) || mobile is null || !MobilePattern().IsMatch(mobile))
        {
            return Fail(ref error, "customer: an EWP customer number (CUST-...), a name, a date of birth (yyyy-MM-dd) and an Australian mobile number (+614........) are required.");
        }
        if (customer["kycVerified"]?.GetValueKind() != System.Text.Json.JsonValueKind.True)
            return Fail(ref error, "customer.kycVerified must be true: the Partner Bank opens accounts only for customers the distributor has identified.");
        if (dateOfBirth > today.AddYears(-18))
            return Fail(ref error, "The customer must be at least 18 years old.");

        if (instruction["form"] is not JsonObject form || !Evidence(form, "number", out var formNumber, out var signedOn, out var scan, out error) ||
            signedOn > today || signedOn < today.AddDays(-formValidDays))
        {
            return Fail(ref error, $"form: the number, the date Form VII was signed (within the last {formValidDays} days) and the SHA-256 of its scan are required.");
        }

        return TryActor(instruction, out var actor, out error)
            ? new Opening(number, name.Trim(), dateOfBirth, mobile, formNumber!, signedOn, scan!, actor!)
            : null;
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A purchase: the amount in AUD (two decimals, at most MaxPurchaseAmount), the paying
    // │ account, and the customer's transfer authorization slip - signed TODAY (Sydney): the
    // │ slip authorises a purchase at today's price, not an open-ended one.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static GoldPurchase? TryPurchase(JsonObject instruction, DateOnly today, out string? error)
    {
        error = null;
        if (!OnlyFields(instruction, out error, "type", "goldAccountNumber", "customer", "instructedAmount", "debtorAccount", "slip", "actor") ||
            Text(instruction, "type") != Purchase)
        {
            return Fail<GoldPurchase>(ref error, $"The instruction must be a {Purchase} with goldAccountNumber, customer, instructedAmount, debtorAccount, slip and actor only.");
        }

        var account = Text(instruction, "goldAccountNumber");
        if (account is null || !GoldAccountPattern().IsMatch(account))
            return Fail<GoldPurchase>(ref error, "goldAccountNumber (PB-DGA-...) is required.");
        if (instruction["customer"] is not JsonObject customer || !OnlyFields(customer, out error, "ewpCustomerNumber") ||
            Text(customer, "ewpCustomerNumber") is not { } number || !CustomerNumberPattern().IsMatch(number))
        {
            return Fail<GoldPurchase>(ref error, "customer.ewpCustomerNumber (CUST-...) is required.");
        }

        if (instruction["instructedAmount"] is not JsonObject amount || !OnlyFields(amount, out error, "currency", "amount") || Text(amount, "currency") != "AUD" ||
            Text(amount, "amount") is not { } amountText || !AmountPattern().IsMatch(amountText) ||
            !decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0 || value > MaxPurchaseAmount)
        {
            return Fail<GoldPurchase>(ref error, $"instructedAmount must be AUD, more than 0 and at most {MaxPurchaseAmount:N0}, written with two decimals (e.g. \"20.00\").");
        }

        if (instruction["debtorAccount"] is not JsonObject debtor || !OnlyFields(debtor, out error, "bsb", "accountNumber") ||
            Text(debtor, "bsb") is not { } bsb || !BsbPattern().IsMatch(bsb) || Text(debtor, "accountNumber") is not { } accountNumber || !AccountPattern().IsMatch(accountNumber))
        {
            return Fail<GoldPurchase>(ref error, "debtorAccount: a BSB (123-456) and an account number (5-9 digits) are required.");
        }

        if (instruction["slip"] is not JsonObject slip || !Evidence(slip, "number", out var slipNumber, out var signedOn, out var scan, out error, "mobile") ||
            Text(slip, "mobile") is not { } mobile || !MobilePattern().IsMatch(mobile) || signedOn != today)
        {
            return Fail<GoldPurchase>(ref error, "slip: the number, today's date (Sydney), the mobile number written on it and the SHA-256 of its scan are required.");
        }

        return TryActor(instruction, out var actor, out error)
            ? new GoldPurchase(account, number, value, bsb, accountNumber, slipNumber!, signedOn, mobile, scan!, actor!)
            : null;
    }

    private static bool Evidence(JsonObject evidence, string numberField, out string? number, out DateOnly signedOn, out string? scan, out string? error, params string[] extra)
    {
        number = Text(evidence, numberField);
        scan = Text(evidence, "scanSha256");
        signedOn = default;
        return OnlyFields(evidence, out error, [numberField, "signedOn", "scanSha256", .. extra]) &&
               number is not null && FormNumberPattern().IsMatch(number) &&
               Date(evidence, "signedOn", out signedOn) &&
               scan is not null && Sha256Pattern().IsMatch(scan);
    }

    private static bool TryActor(JsonObject instruction, out Actor? actor, out string? error)
    {
        actor = null;
        error = null;
        if (instruction["actor"] is not JsonObject value || !OnlyFields(value, out error, "lanId", "branch") ||
            Text(value, "lanId") is not { } lanId || !LanIdPattern().IsMatch(lanId) || Text(value, "branch") is not { } branch || !BranchPattern().IsMatch(branch))
        {
            error ??= "actor: the staff member's LAN ID and branch are required.";
            return false;
        }
        actor = new Actor(lanId, branch);
        return true;
    }

    private static bool OnlyFields(JsonObject value, out string? error, params string[] allowed)
    {
        var unknown = value.Select(p => p.Key).Where(k => !allowed.Contains(k)).ToList();
        error = unknown.Count == 0 ? null : $"Unknown field(s): {string.Join(", ", unknown)}.";
        return error is null && allowed.All(value.ContainsKey);
    }

    private static string? Text(JsonObject value, string name) =>
        value[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static bool Date(JsonObject value, string name, out DateOnly date) =>
        DateOnly.TryParseExact(Text(value, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static T? Fail<T>(ref string? error, string message) where T : class
    {
        error ??= message;
        return null;
    }

    private static Opening? Fail(ref string? error, string message) => Fail<Opening>(ref error, message);

    [GeneratedRegex(@"^\d{1,5}\.\d{2}$")]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"^\d{3}-\d{3}$")]
    private static partial Regex BsbPattern();

    [GeneratedRegex(@"^\d{5,9}$")]
    private static partial Regex AccountPattern();

    [GeneratedRegex(@"^CUST-\d{1,12}$")]
    private static partial Regex CustomerNumberPattern();

    [GeneratedRegex(@"^PB-DGA-[A-Z0-9]{6}$")]
    private static partial Regex GoldAccountPattern();

    [GeneratedRegex(@"^[\p{L}\p{M}' .-]{1,140}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\+614\d{8}$")]
    private static partial Regex MobilePattern();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9-]{3,31}$")]
    private static partial Regex FormNumberPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[a-z0-9]{2,20}$")]
    private static partial Regex LanIdPattern();

    [GeneratedRegex(@"^[A-Z0-9]{1,20}$")]
    private static partial Regex BranchPattern();
}