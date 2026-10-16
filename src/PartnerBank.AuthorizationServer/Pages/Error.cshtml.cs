using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer.Services;

namespace PartnerBank.AuthorizationServer.Pages;

/// <summary>IdentityServer's error page, e.g. an authorization request that was not pushed first.</summary>
public sealed class ErrorModel(IIdentityServerInteractionService interaction, IWebHostEnvironment environment) : PageModel
{
    public string? Error { get; private set; }

    public string? Description { get; private set; }

    public string? RequestId { get; private set; }

    public async Task OnGetAsync(string? errorId)
    {
        var message = await interaction.GetErrorContextAsync(errorId, HttpContext.RequestAborted);
        Error = message?.Error;
        RequestId = message?.RequestId;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The technical description only in Development: elsewhere the person sees the error
        // │ code and a request ID to quote - no internals.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (environment.IsDevelopment())
            Description = message?.ErrorDescription;
    }
}