using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using PartnerBank.Products.Data;

namespace PartnerBank.Products.Pages;

/// <summary>
/// EWP opens the window here with its one-time link. The customer is signed in first (single
/// sign-on at the Partner's authorization server), then the link is checked and opened.
/// </summary>
[Authorize]
public sealed class StartModel(ProductsDbContext db, TimeProvider clock) : PageModel
{
    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? handoff)
    {
        var link = string.IsNullOrWhiteSpace(handoff) ? null : await db.Handoffs.FindAsync([handoff], HttpContext.RequestAborted);
        var now = clock.GetUtcNow();

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The link opens only for the customer it was made for (the signed-in subject must
        // │ match), only once, and only before it expires. One message for every failure - the
        // │ page never confirms that a link exists for someone else.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (link is null || link.CustomerSubject != User.FindFirst("sub")?.Value || link.OpenedAt is not null || now > link.ExpiresAt)
        {
            Problem = "The link is not valid, has expired, or was already used.";
            return Page();
        }

        link.OpenedAt = now;
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return RedirectToPage("/Products", new { handoff });
    }
}