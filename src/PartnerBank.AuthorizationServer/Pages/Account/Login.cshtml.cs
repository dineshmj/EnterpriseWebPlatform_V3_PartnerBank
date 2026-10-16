using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;

namespace PartnerBank.AuthorizationServer.Pages.Account;

/// <summary>
/// The Partner Bank's own sign-in, reached only through an authorization request that EWP pushed
/// first (PAR). Demo customers only (TestUserStore) - a minimal PoC, not a product sign-in.
/// </summary>
public sealed class LoginModel(IIdentityServerInteractionService interaction, TestUserStore users) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public string? ClientName { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        Input.ReturnUrl = returnUrl;
        ClientName = (await interaction.GetAuthorizationContextAsync(returnUrl, HttpContext.RequestAborted))?.Client.ClientName;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var context = await interaction.GetAuthorizationContextAsync(Input.ReturnUrl, HttpContext.RequestAborted);
        ClientName = context?.Client.ClientName;

        if (!ModelState.IsValid || !users.ValidateCredentials(Input.Username, Input.Password))
        {
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ One message for an unknown user and a wrong password: the page never reveals which
            // │ usernames exist.
            // └──────────────────────────────────────────────────────────────────────────────────────
            ErrorMessage = "Unknown username or password.";
            return Page();
        }

        var user = users.FindByUsername(Input.Username!);
        await HttpContext.SignInAsync(new IdentityServerUser(user.SubjectId) { DisplayName = user.Username });

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Back to the authorization request only if IdentityServer recognises it; anything else
        // │ goes to this site's own home page - never to an address taken from the form (open
        // │ redirect).
        // └──────────────────────────────────────────────────────────────────────────────────────
        return context is not null ? Redirect(Input.ReturnUrl!) : LocalRedirect("~/");
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