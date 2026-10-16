using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using PartnerBank.Web.Services;

namespace PartnerBank.Web.Pages;

/// <summary>
/// EWP opens the window here with its one-time link. The customer is signed in first (single
/// sign-on at the Partner's authorization server), then the API opens the link - for them only.
/// </summary>
[Authorize]
public sealed class StartModel(PartnerApi api) : PageModel
{
    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? handoff)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ One message for every failure - the page never confirms that a link exists for
        // │ someone else (the API checks the subject, single use and expiry).
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(handoff) || !await api.OpenHandoffAsync(handoff, HttpContext.RequestAborted))
        {
            Problem = "The link is not valid, has expired, or was already used.";
            return Page();
        }
        return RedirectToPage("/Products", new { handoff });
    }
}