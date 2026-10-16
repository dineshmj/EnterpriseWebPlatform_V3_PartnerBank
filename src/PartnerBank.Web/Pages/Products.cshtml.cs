using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using PartnerBank.Web.Services;

namespace PartnerBank.Web.Pages;

/// <summary>The products window: the customer chooses a product and applies (one application per window).</summary>
[Authorize]
public sealed class ProductsModel(PartnerApi api) : PageModel
{
    private const string Closed = "Go back to your bank's website to open the Partner Bank window again.";

    public IReadOnlyList<ProductView> Products { get; private set; } = [];

    public string? Handoff { get; private set; }

    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? handoff)
    {
        Handoff = handoff;
        if (string.IsNullOrWhiteSpace(handoff) || !await api.HandoffUsableAsync(handoff, HttpContext.RequestAborted))
        {
            Problem = Closed;
            return Page();
        }
        Products = await api.ProductsAsync(HttpContext.RequestAborted);
        return Page();
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The form's anti-forgery token is checked by Razor Pages on every POST; the API decides
    // │ whether this customer may still apply with this link.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostAsync(string? handoff, string? productCode)
    {
        Handoff = handoff;
        var application = string.IsNullOrWhiteSpace(handoff) || string.IsNullOrWhiteSpace(productCode)
            ? null
            : await api.ApplyAsync(handoff, productCode, HttpContext.RequestAborted);
        if (application is null)
        {
            Problem = Closed;
            return Page();
        }
        return RedirectToPage("/Applied", new { number = application.Number });
    }
}