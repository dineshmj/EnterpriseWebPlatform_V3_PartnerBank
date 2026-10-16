using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer;
using Duende.IdentityServer.Services;

using PartnerBank.AuthorizationServer.Customers;

namespace PartnerBank.AuthorizationServer.Pages.Account;

/// <summary>
/// The Partner Bank's sign-in. A customer referred by another organisation is recognised from its
/// signed request: "Welcome back" when already linked, otherwise an offer to register with the
/// details the organisation verified.
/// </summary>
public sealed class LoginModel(IIdentityServerInteractionService interaction, CustomerStore customers, TimeProvider clock) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public string? ClientName { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>The referred customer's name when they already have an account here.</summary>
    public string? WelcomeBack { get; private set; }

    /// <summary>True when the referred customer has no account yet and may register.</summary>
    public bool CanRegister { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        Input.ReturnUrl = returnUrl;
        await RecogniseAsync(returnUrl);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var context = await interaction.GetAuthorizationContextAsync(Input.ReturnUrl, HttpContext.RequestAborted);
        await RecogniseAsync(Input.ReturnUrl);

        if (!ModelState.IsValid)
        {
            ErrorMessage = "Unknown username or password.";
            return Page();
        }

        var (outcome, customer) = await customers.SignInAsync(Input.Username!, Input.Password!, HttpContext.RequestAborted);
        if (outcome != SignInOutcome.Success)
        {
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ One message for an unknown user and a wrong password (no username enumeration);
            // │ a locked account says so only in general terms.
            // └──────────────────────────────────────────────────────────────────────────────────────
            ErrorMessage = outcome == SignInOutcome.Locked
                ? $"Too many attempts. Try again in {CustomerStore.Lockout.TotalMinutes:0} minutes."
                : "Unknown username or password.";
            return Page();
        }

        await HttpContext.SignInAsync(new IdentityServerUser(customer!.SubjectId)
        {
            DisplayName = customer.Name,
            AuthenticationTime = clock.GetUtcNow().UtcDateTime,
            AuthenticationMethods = { "pwd" }
        });

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Back to the authorization request only if IdentityServer recognises it; anything else
        // │ goes to this site's own home page - never to an address taken from the form (open
        // │ redirect).
        // └──────────────────────────────────────────────────────────────────────────────────────
        return context is not null ? Redirect(Input.ReturnUrl!) : LocalRedirect("~/");
    }

    private async Task RecogniseAsync(string? returnUrl)
    {
        var context = await interaction.GetAuthorizationContextAsync(returnUrl, HttpContext.RequestAborted);
        ClientName = context?.Client.ClientName;
        var referred = ReferredCustomer.From(context);
        if (referred is null)
            return;

        var linked = await customers.FindLinkedAsync(referred.ReferrerClientId, referred.CustomerNumber, HttpContext.RequestAborted);
        if (linked is not null)
        {
            WelcomeBack = linked.Name;
            Input.Username ??= linked.Username;
        }
        else
        {
            CanRegister = true;
        }
    }

    public sealed class LoginInput
    {
        [Required]
        [StringLength(100)]
        public string? Username { get; set; }

        [Required]
        [StringLength(200)]
        public string? Password { get; set; }

        public string? ReturnUrl { get; set; }
    }
}