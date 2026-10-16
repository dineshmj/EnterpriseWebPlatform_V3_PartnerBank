using System.Globalization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

using PartnerBank.Web.Services;

namespace PartnerBank.Web.Pages;

/// <summary>
/// "My Partner Bank": the signed-in customer's own gold, its value today, their applications and
/// everything that happened, in plain sentences - read through the API with the customer's own token.
/// </summary>
[Authorize]
public sealed class MyModel(PartnerApi api) : PageModel
{
    private static readonly CultureInfo Australia = CultureInfo.GetCultureInfo("en-AU");

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ The Partner Bank stores every time in UTC; customers see Sydney time.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    public HoldingsView? Holdings { get; private set; }

    public IReadOnlyList<ApplicationView> Applications { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Holdings = await api.HoldingsAsync(HttpContext.RequestAborted);
        Applications = await api.MyApplicationsAsync(HttpContext.RequestAborted);
    }

    public static string Money(decimal amount) => amount.ToString("N2", Australia);

    public static string When(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Sydney).ToString("d MMM yyyy, h:mm tt", Australia);
}