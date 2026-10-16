using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer;
using Duende.IdentityServer.Services;

using PartnerBank.AuthorizationServer.Customers;

namespace PartnerBank.AuthorizationServer.Pages.Account;

/// <summary>The one-time code that confirms a registration; the account exists only after it.</summary>
public sealed class VerifyModel(IIdentityServerInteractionService interaction, CustomerStore customers, TimeProvider clock) : PageModel
{
    [BindProperty]
    public string? Id { get; set; }

    [BindProperty]
    public string? Code { get; set; }

    public string? Name { get; private set; }

    /// <summary>Development only: the code, shown in place of an SMS.</summary>
    public string? DemoCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        Id = id;
        var pending = string.IsNullOrWhiteSpace(id) ? null : await customers.FindPendingAsync(id, HttpContext.RequestAborted);
        if (pending is null)
        {
            Problem = "This registration has expired. Go back to your bank and start again.";
            return Page();
        }
        Name = pending.Name;
        DemoCode = TempData["DemoCode"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var pending = string.IsNullOrWhiteSpace(Id) ? null : await customers.FindPendingAsync(Id, HttpContext.RequestAborted);
        if (pending is null)
        {
            Problem = "This registration has expired. Go back to your bank and start again.";
            return Page();
        }
        Name = pending.Name;

        var (outcome, customer) = await customers.CompleteRegistrationAsync(Id!, Code ?? string.Empty, HttpContext.RequestAborted);
        switch (outcome)
        {
            case CodeOutcome.WrongCode:
                ErrorMessage = "That code is not right. Check it and try again.";
                return Page();
            case CodeOutcome.TooManyAttempts:
            case CodeOutcome.Expired:
                Problem = "Too many attempts, or the code has expired. Go back to your bank and start again.";
                return Page();
            case CodeOutcome.AlreadyRegistered:
                Problem = "You already have a Partner Bank account - go back and sign in.";
                return Page();
        }

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Registered: signed in with what was proven just now - the chosen password and the
        // │ one-time code (amr pwd + otp). Then back to the authorization request ONLY if
        // │ IdentityServer recognises it (no open redirect) - next comes the consent page.
        // └──────────────────────────────────────────────────────────────────────────────────────
        await HttpContext.SignInAsync(new IdentityServerUser(customer!.SubjectId)
        {
            DisplayName = customer.Name,
            AuthenticationTime = clock.GetUtcNow().UtcDateTime,
            AuthenticationMethods = { "pwd", "otp" }
        });
        return interaction.IsValidReturnUrl(pending.ReturnUrl) ? Redirect(pending.ReturnUrl) : LocalRedirect("~/");
    }
}