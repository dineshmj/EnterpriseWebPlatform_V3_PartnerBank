using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using PartnerBank.Products.Data;

namespace PartnerBank.Products.Pages;

/// <summary>The confirmation after applying - shown only to the customer who applied.</summary>
[Authorize]
public sealed class AppliedModel(ProductsDbContext db) : PageModel
{
    public ProductApplication? Application { get; private set; }

    public string? ProductName { get; private set; }

    public async Task OnGetAsync(string? number)
    {
        var subject = User.FindFirst("sub")?.Value;
        Application = await db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Number == number && a.CustomerSubject == subject, HttpContext.RequestAborted);
        if (Application is not null)
            ProductName = (await db.Products.FindAsync([Application.ProductCode], HttpContext.RequestAborted))?.Name;
    }
}