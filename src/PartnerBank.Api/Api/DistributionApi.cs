using System.Security.Claims;

using PartnerBank.Api.Data;
using PartnerBank.Api.Distribution;
using PartnerBank.Api.Gold;

namespace PartnerBank.Api.Api;

/// <summary>
/// Digital Gold through a DISTRIBUTOR (EWP at its assisted-channel counter): accounts opened on
/// the customer's signed Form VII, purchases on the customer's signed transfer authorization slip,
/// the holding read back. External port, mutual TLS, EWP's own token bound to its certificate.
/// </summary>
public static class DistributionApi
{
    public const string DistributionPolicy = "Distribution";

    public sealed record AccountView(string Number, string EwpCustomerNumber, string HolderName, DateTimeOffset OpenedAt);

    public sealed record AccountHoldingView(string Number, string HolderName, GoldDesk.HoldingView Holding);

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ The distributor model (doc/Architecture.md D14): the customer signs paper forms at the
    // │ distributor's counter and never signs in here; the distributor authenticates itself
    // │ (mutual TLS, a token from client credentials, bound to its certificate) and SIGNS each
    // │ instruction. The Partner Bank stays the system of record of the account and its gold.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static void MapDistributionApi(this IEndpointRouteBuilder endpoints)
    {
        var distribution = endpoints.MapGroup("/api/distribution")
            .RequireAuthorization(ExternalApi.EwpPolicy)
            .RequireAuthorization(DistributionPolicy);

        distribution.MapPost("/gold-accounts", OpenAccountAsync);
        distribution.MapGet("/gold-accounts", AccountOfCustomerAsync);
        distribution.MapGet("/gold-accounts/{number}", AccountAsync);
        distribution.MapPost("/gold-orders", PlacePurchaseAsync);
        distribution.MapPost("/staff-views", PushStaffViewAsync);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A staff view only of an account THIS distributor introduced, for the customer the
    // │ instruction names (otherwise the same 404 as an unknown account); the answer is the
    // │ request_uri only, as PAR answers - no data about the account.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> PushStaffViewAsync(
        HttpRequest request, ClaimsPrincipal caller, SignedInstructions signed, GoldDesk desk, StaffViews views, CancellationToken cancellationToken)
    {
        var (instruction, problem) = await ReadAsync(request, caller, signed, cancellationToken);
        if (problem is not null)
            return problem;

        var view = Instructions.TryStaffView(instruction!.Instruction, out var error);
        if (view is null)
            return Invalid(error!);

        var account = await desk.AccountAsync(view.GoldAccountNumber, ClientId(caller), cancellationToken);
        if (account is null || account.EwpCustomerNumber != view.EwpCustomerNumber)
            return Results.Problem($"No Digital Gold account {view.GoldAccountNumber} for this customer and distributor.", statusCode: StatusCodes.Status404NotFound);

        var requestUri = views.Push(instruction.InstructionId, account.Number, ClientId(caller), view.Actor);
        return requestUri is null
            ? Results.Problem("This signed instruction was already used: sign a new one for each view.", statusCode: StatusCodes.Status409Conflict)
            : Results.Created((string?)null, new { request_uri = requestUri, expires_in = (int)StaffViews.Lifetime.TotalSeconds });
    }

    private static async Task<IResult> OpenAccountAsync(
        HttpRequest request, ClaimsPrincipal caller, SignedInstructions signed, GoldDesk desk, Configuration.ApiOptions settings, CancellationToken cancellationToken)
    {
        var (instruction, problem) = await ReadAsync(request, caller, signed, cancellationToken);
        if (problem is not null)
            return problem;

        var opening = Instructions.TryOpening(instruction!.Instruction, desk.Today(), settings.Distribution.FormValidDays, out var error);
        if (opening is null)
            return Invalid(error!);

        var result = await desk.OpenAccountAsync(opening, ClientId(caller), instruction.InstructionId, cancellationToken);
        if (result.Conflict is not null)
            return Results.Problem(result.Conflict, statusCode: StatusCodes.Status409Conflict);
        var view = View(result.Account!);
        return result.Created ? Results.Created($"/api/distribution/gold-accounts/{view.Number}", view) : Results.Ok(view);
    }

    private static async Task<IResult> PlacePurchaseAsync(
        HttpRequest request, ClaimsPrincipal caller, SignedInstructions signed, GoldDesk desk, CancellationToken cancellationToken)
    {
        var (instruction, problem) = await ReadAsync(request, caller, signed, cancellationToken);
        if (problem is not null)
            return problem;

        var purchase = Instructions.TryPurchase(instruction!.Instruction, desk.Today(), out var error);
        if (purchase is null)
            return Invalid(error!);

        var result = await desk.PlacePurchaseAsync(purchase, ClientId(caller), instruction.InstructionId, cancellationToken);
        if (!result.AccountFound)
            return Results.Problem($"No Digital Gold account {purchase.GoldAccountNumber} for this distributor.", statusCode: StatusCodes.Status404NotFound);
        if (result.Conflict is not null)
            return Results.Problem(result.Conflict, statusCode: StatusCodes.Status409Conflict);
        var view = GoldApi.View(result.Order!);
        return result.Created ? Results.Created($"/api/gold-orders/{view.Number}", view) : Results.Ok(view);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A distributor sees ONLY the accounts it introduced: any other number - or a customer
    // │ it did not introduce - is a 404, never a hint that the account exists.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> AccountAsync(string number, ClaimsPrincipal caller, GoldDesk desk, CancellationToken cancellationToken)
    {
        var account = await desk.AccountAsync(number, ClientId(caller), cancellationToken);
        return account is null
            ? Results.NotFound()
            : Results.Ok(new AccountHoldingView(account.Number, account.HolderName, await desk.HoldingAsync(account.Number, cancellationToken)));
    }

    private static async Task<IResult> AccountOfCustomerAsync(string? ewpCustomerNumber, ClaimsPrincipal caller, GoldDesk desk, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ewpCustomerNumber) || ewpCustomerNumber.Length > 20)
            return Invalid("ewpCustomerNumber is required.");
        var account = await desk.AccountOfCustomerAsync(ewpCustomerNumber, ClientId(caller), cancellationToken);
        return account is null ? Results.NotFound() : Results.Ok(View(account));
    }

    // The body must be EWP's signed instruction (application/jwt), signed by the caller itself.
    private static async Task<(SignedInstructions.Checked? Instruction, IResult? Problem)> ReadAsync(
        HttpRequest request, ClaimsPrincipal caller, SignedInstructions signed, CancellationToken cancellationToken)
    {
        var compact = await SignedInstructions.ReadBodyAsync(request, cancellationToken);
        if (compact is null)
            return (null, Results.Problem($"The body must be a signed instruction ({SignedInstructions.MediaType}, at most 16 KB).", statusCode: StatusCodes.Status415UnsupportedMediaType));

        var (instruction, error) = await signed.CheckAsync(compact, ClientId(caller));
        return instruction is null
            ? (null, Results.Problem(error, statusCode: StatusCodes.Status400BadRequest, title: "invalid_instruction"))
            : (instruction, null);
    }

    private static IResult Invalid(string error) => Results.ValidationProblem(new Dictionary<string, string[]> { ["instruction"] = [error] });

    private static string ClientId(ClaimsPrincipal caller) => caller.FindFirst("client_id")!.Value;

    private static AccountView View(GoldAccount account) => new(account.Number, account.EwpCustomerNumber, account.HolderName, account.OpenedAt);
}