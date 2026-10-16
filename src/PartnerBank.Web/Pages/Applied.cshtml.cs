using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

using PartnerBank.Web.Services;

namespace PartnerBank.Web.Pages;

/// <summary>The confirmation after applying - the API returns it only to the customer who applied.</summary>
[Authorize]
public sealed class AppliedModel(PartnerApi api) : PageModel
{
    public ApplicationView? Application { get; private set; }

    public async Task OnGetAsync(string? number)
    {
        if (!string.IsNullOrWhiteSpace(number))
            Application = await api.ApplicationAsync(number, HttpContext.RequestAborted);
    }
}