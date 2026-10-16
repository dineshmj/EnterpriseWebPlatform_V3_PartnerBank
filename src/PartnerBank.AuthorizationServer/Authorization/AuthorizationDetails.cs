using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PartnerBank.AuthorizationServer.Authorization;

/// <summary>
/// RFC 9396 Rich Authorization Requests: what EWP asks the customer to authorise, stated precisely.
/// Three types: a Digital Gold purchase (amount and paying account), a product application, and a
/// view of the customer's holdings. Each names the EWP-verified customer and the acting staff member.
/// </summary>
public static partial class AuthorizationDetails
{
    public const string ParameterName = "authorization_details";

    public const string DigitalGoldPurchase = "digital_gold_purchase";
    public const string ProductApplication = "product_application";
    public const string HoldingsView = "holdings_view";

    public static readonly IReadOnlyList<string> SupportedTypes = [DigitalGoldPurchase, ProductApplication, HoldingsView];

    /// <summary>
    /// The authorization_details of a signed request object, as one JSON array. A JSON array in a JWT
    /// may arrive as one claim per element: they are put back together, so "exactly one detail" is
    /// still checked over all of them.
    /// </summary>
    public static string? FromRequestObject(IEnumerable<System.Security.Claims.Claim>? requestObjectValues)
    {
        var values = requestObjectValues?.Where(c => c.Type == ParameterName).Select(c => c.Value.Trim()).ToList() ?? [];
        return values.Count switch
        {
            0 => null,
            1 when values[0].StartsWith('[') => values[0],
            _ => "[" + string.Join(",", values) + "]"
        };
    }

    /// <summary>The largest Digital Gold purchase in one authorisation (AUD).</summary>
    public const decimal MaxPurchaseAmount = 50_000m;

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Strict, allow-list validation of what is being authorised: exactly ONE detail, a known
    // │ type, only the fields that type has, every value matching its pattern. Anything else
    // │ refuses the whole request - a detail the Partner Bank does not fully understand is
    // │ never authorised. The validated detail is returned re-serialised (no unknown fields,
    // │ canonical form), and that is what travels into the token.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static bool TryValidate(string? json, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
            return Fail("authorization_details is required: state exactly what the customer is asked to authorise.", out error);

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return Fail("authorization_details is not valid JSON.", out error); }

        if (root is not JsonArray { Count: 1 } array || array[0] is not JsonObject detail)
            return Fail("authorization_details must hold exactly one detail.", out error);

        var type = Text(detail, "type");
        JsonObject? clean = type switch
        {
            DigitalGoldPurchase => Purchase(detail, out error),
            ProductApplication => Application(detail, out error),
            HoldingsView => Holdings(detail, out error),
            _ => null
        };
        if (clean is null)
            return Fail(error ?? $"Unsupported authorization_details type '{type}'.", out error);

        normalized = new JsonArray(clean).ToJsonString();
        return true;
    }

    private static JsonObject? Purchase(JsonObject detail, out string? error)
    {
        if (!OnlyFields(detail, out error, "type", "instructedAmount", "debtorAccount", "customer", "actor"))
            return null;

        var amount = detail["instructedAmount"] as JsonObject;
        var account = detail["debtorAccount"] as JsonObject;
        if (amount is null || !OnlyFields(amount, out error, "currency", "amount") || Text(amount, "currency") != "AUD" ||
            !decimal.TryParse(Text(amount, "amount"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            value <= 0 || value > MaxPurchaseAmount || decimal.Round(value, 2) != value || !AmountPattern().IsMatch(Text(amount, "amount")!))
        {
            error ??= $"instructedAmount must be AUD, more than 0 and at most {MaxPurchaseAmount:N0}, written with two decimals (e.g. \"10.00\").";
            return null;
        }
        if (account is null || !OnlyFields(account, out error, "bsb", "accountNumber") ||
            !BsbPattern().IsMatch(Text(account, "bsb") ?? "") || !AccountPattern().IsMatch(Text(account, "accountNumber") ?? ""))
        {
            error ??= "debtorAccount must have a bsb (999-999) and an accountNumber (5-9 digits).";
            return null;
        }

        var customer = Customer(detail, out error);
        var actor = customer is null ? null : Actor(detail, out error);
        return customer is null || actor is null
            ? null
            : new JsonObject
            {
                ["type"] = DigitalGoldPurchase,
                ["instructedAmount"] = new JsonObject { ["currency"] = "AUD", ["amount"] = Text(amount, "amount") },
                ["debtorAccount"] = new JsonObject { ["bsb"] = Text(account, "bsb"), ["accountNumber"] = Text(account, "accountNumber") },
                ["customer"] = customer,
                ["actor"] = actor
            };
    }

    private static JsonObject? Application(JsonObject detail, out string? error)
    {
        if (!OnlyFields(detail, out error, "type", "productCode", "customer", "actor"))
            return null;
        var productCode = Text(detail, "productCode");
        if (productCode is null || !ProductPattern().IsMatch(productCode))
        {
            error = "productCode must name one of the Partner Bank's products (e.g. PB-TD-12).";
            return null;
        }

        var customer = Customer(detail, out error);
        var actor = customer is null ? null : Actor(detail, out error);
        return customer is null || actor is null
            ? null
            : new JsonObject { ["type"] = ProductApplication, ["productCode"] = productCode, ["customer"] = customer, ["actor"] = actor };
    }

    private static JsonObject? Holdings(JsonObject detail, out string? error)
    {
        if (!OnlyFields(detail, out error, "type", "customer", "actor"))
            return null;
        var customer = Customer(detail, out error);
        var actor = customer is null ? null : Actor(detail, out error);
        return customer is null || actor is null
            ? null
            : new JsonObject { ["type"] = HoldingsView, ["customer"] = customer, ["actor"] = actor };
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The customer as EWP has verified them (KYC). These are verified FACTS from a trusted
    // │ organisation - used to recognise or pre-fill, never to sign the customer in: the
    // │ customer still authenticates at the Partner Bank.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static JsonObject? Customer(JsonObject detail, out string? error)
    {
        error = null;
        if (detail["customer"] is not JsonObject customer || !OnlyFields(customer, out error, "ewpCustomerNumber", "name", "dateOfBirth", "kycVerified"))
        {
            error ??= "customer is required.";
            return null;
        }
        var number = Text(customer, "ewpCustomerNumber");
        var name = Text(customer, "name");
        var birth = Text(customer, "dateOfBirth");
        if (number is null || !CustomerNumberPattern().IsMatch(number) || name is null || !NamePattern().IsMatch(name) ||
            !DateOnly.TryParseExact(birth, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOfBirth) ||
            dateOfBirth >= DateOnly.FromDateTime(DateTime.UtcNow) || customer["kycVerified"]?.GetValueKind() != JsonValueKind.True)
        {
            error = "customer needs ewpCustomerNumber (CUST-…), name, dateOfBirth (yyyy-MM-dd, in the past) and kycVerified: true.";
            return null;
        }
        return new JsonObject { ["ewpCustomerNumber"] = number, ["name"] = name, ["dateOfBirth"] = birth, ["kycVerified"] = true };
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The staff member acting for the customer, as EWP has authenticated them. Recorded for
    // │ the Partner Bank's audit; NEVER authenticated here and never given any access.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static JsonObject? Actor(JsonObject detail, out string? error)
    {
        error = null;
        if (detail["actor"] is not JsonObject actor || !OnlyFields(actor, out error, "lanId", "branch") ||
            !LanIdPattern().IsMatch(Text(actor, "lanId") ?? "") || !BranchPattern().IsMatch(Text(actor, "branch") ?? ""))
        {
            error ??= "actor needs lanId (2-20 lower-case letters or digits) and branch (1-20 upper-case letters or digits).";
            return null;
        }
        return new JsonObject { ["lanId"] = Text(actor, "lanId"), ["branch"] = Text(actor, "branch") };
    }

    private static bool OnlyFields(JsonObject node, out string? error, params string[] allowed)
    {
        var unknown = node.Select(p => p.Key).Where(k => !allowed.Contains(k)).ToList();
        error = unknown.Count == 0 ? null : $"Unknown field(s): {string.Join(", ", unknown)}.";
        return unknown.Count == 0;
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    [GeneratedRegex(@"^\d{1,5}\.\d{2}$")]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"^\d{3}-\d{3}$")]
    private static partial Regex BsbPattern();

    [GeneratedRegex(@"^\d{5,9}$")]
    private static partial Regex AccountPattern();

    [GeneratedRegex(@"^PB-[A-Z0-9-]{1,20}$")]
    private static partial Regex ProductPattern();

    [GeneratedRegex(@"^CUST-\d{1,12}$")]
    private static partial Regex CustomerNumberPattern();

    [GeneratedRegex(@"^[\p{L}\p{M}' .-]{1,140}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z0-9]{2,20}$")]
    private static partial Regex LanIdPattern();

    [GeneratedRegex(@"^[A-Z0-9]{1,20}$")]
    private static partial Regex BranchPattern();
}