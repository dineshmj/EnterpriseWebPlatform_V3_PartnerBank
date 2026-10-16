using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer.Services;

namespace PartnerBank.AuthorizationServer.Pages.Account;

/// <summary>Ends the customer's Partner Bank session (OpenID Connect sign-out), then returns to the app that asked.</summary>
public sealed class LogoutModel(IIdentityServerInteractionService interaction) : PageModel
{
    [BindProperty]
    public string? LogoutId { get; set; }

    public bool Confirm { get; private set; }

    public bool SignedOut { get; private set; }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A sign-out request from one of our apps (with a valid id_token_hint) signs out at once;
    // │ one without it - e.g. a link on another site - asks first (no sign-out by forgery).
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnGetAsync(string? logoutId)
    {
        LogoutId = logoutId;
        var context = await interaction.GetLogoutContextAsync(logoutId, HttpContext.RequestAborted);
        if (context.ShowSignoutPrompt)
        {
            Confirm = true;
            return Page();
        }
        return await SignOutAsync();
    }

    public Task<IActionResult> OnPostAsync() => SignOutAsync();

    private async Task<IActionResult> SignOutAsync()
    {
        var context = await interaction.GetLogoutContextAsync(LogoutId, HttpContext.RequestAborted);
        await HttpContext.SignOutAsync();
        if (!string.IsNullOrEmpty(context.PostLogoutRedirectUri))
            return Redirect(context.PostLogoutRedirectUri);
        SignedOut = true;
        return Page();
    }
}