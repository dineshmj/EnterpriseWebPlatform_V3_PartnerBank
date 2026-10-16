using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

using PartnerBank.AuthorizationServer.Authorization;
using PartnerBank.AuthorizationServer.Configuration;

namespace PartnerBank.AuthorizationServer.Pages.Consent;

/// <summary>
/// The customer's consent at the Partner Bank: exactly what the referring organisation asks for (its
/// signed authorization_details), who offered it, and which of the customer's details it shared.
/// One decision per request - nothing is remembered.
/// </summary>
[Authorize]
public sealed class IndexModel(IIdentityServerInteractionService interaction, PartnerBankOptions settings) : PageModel
{
    private static readonly CultureInfo Australia = CultureInfo.GetCultureInfo("en-AU");

    [BindProperty]
    public string? ReturnUrl { get; set; }

    public string ClientName { get; private set; } = string.Empty;

    /// <summary>What the customer is asked to authorise, in plain words.</summary>
    public string Asked { get; private set; } = string.Empty;

    /// <summary>Further facts of the request (e.g. the paying account).</summary>
    public List<(string Label, string Value)> Facts { get; } = [];

    /// <summary>The customer's details the referring organisation shared (verified by it).</summary>
    public List<(string Label, string Value)> SharedDetails { get; } = [];

    public string? OfferedBy { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        ReturnUrl = returnUrl;
        var context = await interaction.GetAuthorizationContextAsync(returnUrl, HttpContext.RequestAborted);
        if (context is null)
            return RedirectToPage("/Error");
        if (!Describe(context))
        {
            await interaction.DenyAuthorizationAsync(context, InteractionError.AccessDenied, HttpContext.RequestAborted, "The request could not be shown to the customer.");
            return Redirect(returnUrl!);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? decision)
    {
        var context = await interaction.GetAuthorizationContextAsync(ReturnUrl, HttpContext.RequestAborted);
        if (context is null)
            return RedirectToPage("/Error");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Only an explicit "approve" grants anything; any other answer is a refusal, returned to
        // │ the referring organisation as access_denied (no code). The consent is for this request
        // │ only (RememberConsent = false), for exactly the scopes that were asked for. Razor
        // │ Pages checks the form's anti-forgery token, and the return address is IdentityServer's
        // │ own (the context above was found for it) - never an open redirect.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (decision == "approve" && Describe(context))
        {
            await interaction.GrantConsentAsync(context, new ConsentResponse
            {
                ScopesValuesConsented = context.ValidatedResources.RawScopeValues,
                RememberConsent = false
            }, HttpContext.RequestAborted);
        }
        else
        {
            await interaction.DenyAuthorizationAsync(context, InteractionError.AccessDenied, HttpContext.RequestAborted, "The customer declined.");
        }
        return Redirect(ReturnUrl!);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The page shows the SIGNED authorization detail, validated again - never the plain
    // │ request parameters. Account numbers are masked to their last four digits; the shared
    // │ personal details are shown so the customer sees what the other organisation passed on.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private bool Describe(AuthorizationRequest context)
    {
        ClientName = context.Client.ClientName ?? context.Client.ClientId;
        if (!AuthorizationDetails.TryValidate(AuthorizationDetails.FromRequestObject(context.RequestObjectValues), out var normalized, out _))
            return false;

        var detail = JsonNode.Parse(normalized!)!.AsArray()[0]!;
        switch ((string?)detail["type"])
        {
            case AuthorizationDetails.ProductApplication:
                var code = (string)detail["productCode"]!;
                Asked = $"Apply for {(settings.ProductNames.TryGetValue(code, out var name) ? name : code)}";
                break;
            case AuthorizationDetails.DigitalGoldPurchase:
                var amount = decimal.Parse((string)detail["instructedAmount"]!["amount"]!, CultureInfo.InvariantCulture);
                Asked = $"Buy Digital Gold for AUD {amount.ToString("N2", Australia)}";
                var account = (string)detail["debtorAccount"]!["accountNumber"]!;
                Facts.Add(("Paid from", $"your {ClientName} account {(string)detail["debtorAccount"]!["bsb"]!} ••••{account[^4..]}"));
                Facts.Add(("Grams", "quoted at today's price before you pay"));
                break;
            case AuthorizationDetails.HoldingsView:
                Asked = "Show your Partner Bank holdings and activity";
                break;
            default:
                return false;
        }

        var customer = detail["customer"]!;
        SharedDetails.Add(("Name", (string)customer["name"]!));
        SharedDetails.Add(("Date of birth", DateOnly.ParseExact((string)customer["dateOfBirth"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d MMMM yyyy", Australia)));
        SharedDetails.Add(("Customer number", $"{(string)customer["ewpCustomerNumber"]!} (identity verified by {ClientName})"));

        var actor = detail["actor"]!;
        OfferedBy = $"{ClientName} - staff member {(string)actor["lanId"]!}, branch {(string)actor["branch"]!}";
        return true;
    }
}