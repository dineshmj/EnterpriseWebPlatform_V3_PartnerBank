using System.ComponentModel.DataAnnotations;
using System.Globalization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer.Services;

using PartnerBank.AuthorizationServer.Configuration;
using PartnerBank.AuthorizationServer.Customers;

namespace PartnerBank.AuthorizationServer.Pages.Account;

/// <summary>
/// A first-time customer referred by another organisation registers with the details that
/// organisation verified (read-only here), chooses a username and password, and confirms with a
/// one-time code (the next page).
/// </summary>
public sealed class RegisterModel(
    IIdentityServerInteractionService interaction, CustomerStore customers, PartnerBankOptions settings, ILogger<RegisterModel> logger) : PageModel
{
    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public ReferredCustomer? Customer { get; private set; }

    public string? Problem { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        Input.ReturnUrl = returnUrl;
        await LoadAsync(returnUrl);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The identity is read AGAIN from the signed request behind the return URL - nothing
        // │ about who the person is comes from the form.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!await LoadAsync(Input.ReturnUrl))
            return Page();

        var username = (Input.Username ?? string.Empty).Trim().ToLowerInvariant();
        ErrorMessage = await customers.CheckNewCredentialsAsync(username, Input.Password ?? string.Empty, Input.Confirm ?? string.Empty, HttpContext.RequestAborted);
        if (ErrorMessage is not null)
            return Page();

        var (pending, code) = await customers.StartRegistrationAsync(
            username, Input.Password!, Customer!.Name, Customer.DateOfBirth, Customer.ReferrerClientId, Customer.CustomerNumber,
            Input.ReturnUrl!, HttpContext.RequestAborted);

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The code would go by SMS to the phone number on file (a real bank: an SMS / push
        // │ provider). The PoC has no SMS: the code is written to this console, and shown on the
        // │ next page ONLY when PartnerBank:ShowOneTimeCodeOnPage is true (Development).
        // └──────────────────────────────────────────────────────────────────────────────────────
        logger.LogWarning("[DEMO SMS] To {Name}: your Partner Bank code is {Code} (valid {Minutes} minutes).",
            Customer.Name, code, CustomerStore.CodeLifetime.TotalMinutes);
        if (settings.ShowOneTimeCodeOnPage)
            TempData["DemoCode"] = code;

        return RedirectToPage("/Account/Verify", new { id = pending.Id });
    }

    private async Task<bool> LoadAsync(string? returnUrl)
    {
        var context = await interaction.GetAuthorizationContextAsync(returnUrl, HttpContext.RequestAborted);
        Customer = ReferredCustomer.From(context);
        if (Customer is null)
        {
            Problem = "Registration is possible when you come from a bank we work with. Go back to your bank and start again.";
            return false;
        }
        if (await customers.FindLinkedAsync(Customer.ReferrerClientId, Customer.CustomerNumber, HttpContext.RequestAborted) is not null)
        {
            Problem = "You already have a Partner Bank account - go back and sign in.";
            return false;
        }
        return true;
    }

    public static string BornOn(string dateOfBirth) =>
        DateOnly.ParseExact(dateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-AU"));

    public sealed class RegisterInput
    {
        [StringLength(30)]
        public string? Username { get; set; }

        [StringLength(200)]
        public string? Password { get; set; }

        [StringLength(200)]
        public string? Confirm { get; set; }

        public string? ReturnUrl { get; set; }
    }
}