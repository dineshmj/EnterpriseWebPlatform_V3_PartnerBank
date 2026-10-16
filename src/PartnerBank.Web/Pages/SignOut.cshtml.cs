using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PartnerBank.Web.Pages;

/// <summary>Signs the customer out of the window AND of the Partner Bank (OpenID Connect sign-out).</summary>
[IgnoreAntiforgeryToken]
public sealed class SignOutModel(IAntiforgery antiforgery) : PageModel
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ POST only. While the window still has a session, the form's anti-forgery token must be
    // │ valid (no sign-out by a page on another site). If the window's session has ALREADY
    // │ ended (10 idle minutes), the token - issued for the signed-in customer - can no longer
    // │ match: there is nothing left to protect here, so the sign-out goes on to the Partner
    // │ Bank, which asks "Sign out?" itself when it gets no id_token_hint. Either way the next
    // │ person at the desk starts from nothing.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostAsync()
    {
        if (User.Identity?.IsAuthenticated == true && !await antiforgery.IsRequestValidAsync(HttpContext))
            return BadRequest();

        return SignOut(new AuthenticationProperties { RedirectUri = "/SignedOut" },
            CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme);
    }

    public IActionResult OnGet() => RedirectToPage("/Index");
}