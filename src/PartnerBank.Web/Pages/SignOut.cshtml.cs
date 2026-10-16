using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PartnerBank.Web.Pages;

/// <summary>Signs the customer out of the window AND of the Partner Bank (OpenID Connect sign-out).</summary>
public sealed class SignOutModel : PageModel
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ POST only, with the form's anti-forgery token (no sign-out by a link on another site).
    // │ Ends the window's session and the authorization server's (with the id_token as hint,
    // │ so it signs out without asking) - the next person at the desk starts from nothing.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public IActionResult OnPost() =>
        SignOut(new AuthenticationProperties { RedirectUri = "/" },
            CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme);

    public IActionResult OnGet() => RedirectToPage("/Index");
}