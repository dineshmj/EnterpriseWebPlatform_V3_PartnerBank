using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using PartnerBank.Web.Services;

namespace PartnerBank.Web.Pages;

// ┌─ :: SECURITY :: The distributor's staff view (read-only, one account, one use) ───────────────
// │ Why : EWP's staff member at the counter opens the Partner Bank's own record of ONE customer's
// │       account - they have no Partner Bank sign-in, and the customer is not signing in either.
// │ How : The address carries only the request_uri EWP pushed (PAR-style, signed, mutual TLS); it is
// │       redeemed once, server-side, on the localhost-only API port. The page is a snapshot: no
// │       cookie, no session, no form, never cached (no-store); reloading shows "already used" -
// │       EWP's button makes a new one. The staff member and branch from EWP's signed request are
// │       shown on the page and logged by the API.
// │ Ref : doc/Architecture.md D15 | RFC 9126 | OWASP A01
// └────────────────────────────────────────────────────────────────────────────────────────────────
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StaffViewModel(PartnerApi api) : PageModel
{
    public StaffViewPage? View { get; private set; }

    public async Task OnGetAsync([FromQuery(Name = "request_uri")] string? requestUri)
    {
        if (!string.IsNullOrWhiteSpace(requestUri) && requestUri.Length <= 128)
            View = await api.RedeemStaffViewAsync(requestUri, HttpContext.RequestAborted);
        if (View is null)
            Response.StatusCode = StatusCodes.Status404NotFound;
    }
}