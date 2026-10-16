using System.Security.Cryptography;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using PartnerBank.Products.Configuration;
using PartnerBank.Products.Data;

namespace PartnerBank.Products.Pages;

/// <summary>The products window: the customer chooses a product and applies (one application per window).</summary>
[Authorize]
public sealed class ProductsModel(ProductsDbContext db, ProductsOptions settings, TimeProvider clock) : PageModel
{
    public IReadOnlyList<Product> Products { get; private set; } = [];

    public string? Handoff { get; private set; }

    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? handoff)
    {
        if (await OpenWindowAsync(handoff) is null)
            return Page();
        Products = await db.Products.AsNoTracking().OrderBy(p => p.Code).ToListAsync(HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? handoff, string? productCode)
    {
        var link = await OpenWindowAsync(handoff);
        if (link is null)
            return Page();
        var product = string.IsNullOrWhiteSpace(productCode) ? null : await db.Products.FindAsync([productCode], HttpContext.RequestAborted);
        if (product is null)
            return BadRequest();

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ The application carries EWP's reference and client ID from the link, so EWP's server
        // │ can follow its status through the API - and nobody else can.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var now = clock.GetUtcNow();
        var application = new ProductApplication
        {
            Number = NewApplicationNumber(now),
            ProductCode = product.Code,
            CustomerSubject = link.CustomerSubject,
            ReferrerClientId = link.ReferrerClientId,
            EwpReference = link.EwpReference,
            SubmittedAt = now,
            DecideAfter = now.AddSeconds(settings.DecisionDelaySeconds)
        };
        db.Applications.Add(application);
        link.ApplicationNumber = application.Number;
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return RedirectToPage("/Applied", new { number = application.Number });
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Only an opened link, for THIS signed-in customer, not yet used for an application, and
    // │ within the window's session time. The form's anti-forgery token is checked by Razor
    // │ Pages on every POST.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private async Task<Handoff?> OpenWindowAsync(string? handoff)
    {
        Handoff = handoff;
        var link = string.IsNullOrWhiteSpace(handoff) ? null : await db.Handoffs.FindAsync([handoff], HttpContext.RequestAborted);
        if (link is null || link.CustomerSubject != User.FindFirst("sub")?.Value || link.OpenedAt is null ||
            link.ApplicationNumber is not null || clock.GetUtcNow() > link.OpenedAt.Value.AddMinutes(settings.WindowSessionMinutes))
        {
            Problem = "Go back to your bank's website to open the Partner Bank window again.";
            return null;
        }
        return link;
    }

    private static string NewApplicationNumber(DateTimeOffset now)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<char> code = stackalloc char[6];
        for (var i = 0; i < code.Length; i++)
            code[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"PB-{now:yyMMdd}-{new string(code)}";
    }
}