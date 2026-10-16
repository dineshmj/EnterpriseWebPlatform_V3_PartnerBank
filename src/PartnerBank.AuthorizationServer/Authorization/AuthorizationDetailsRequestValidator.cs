using Duende.IdentityServer.Validation;

using PartnerBank.AuthorizationServer.Configuration;

namespace PartnerBank.AuthorizationServer.Authorization;

/// <summary>
/// Runs for every authorization request - when EWP pushes it (PAR) and again when the browser
/// arrives with its request_uri. EWP's requests must state, INSIDE the signed request object,
/// exactly one valid authorization detail.
/// </summary>
public sealed class AuthorizationDetailsRequestValidator(PartnerBankOptions settings) : ICustomAuthorizeRequestValidator
{
    public Task ValidateAsync(CustomAuthorizeRequestValidationContext context, CancellationToken cancellationToken)
    {
        var request = context.Result!.ValidatedRequest;
        if (request.ClientId != settings.EwpClient.ClientId)
            return Task.CompletedTask;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Read ONLY from the signed request object (JAR) - a value outside it (in the plain
        // │ parameters) is ignored, so nothing that was not signed by EWP can be authorised.
        // └──────────────────────────────────────────────────────────────────────────────────────
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ A fresh sign-in for every request from EWP: its customers sign in at a bank's desk,
        // │ on a browser that is not theirs - no earlier Partner Bank session may be reused. The
        // │ prompt=login must be INSIDE the signed request, so nothing on the way can remove it.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var prompt = request.RequestObjectValues?.FirstOrDefault(c => c.Type == "prompt")?.Value;
        if (prompt is null || !prompt.Split(' ').Contains("login"))
        {
            context.Result.IsError = true;
            context.Result.Error = "invalid_request";
            context.Result.ErrorDescription = "Requests from EWP must ask for a fresh sign-in (prompt=login in the signed request).";
            return Task.CompletedTask;
        }

        var signed = AuthorizationDetails.FromRequestObject(request.RequestObjectValues);
        if (!AuthorizationDetails.TryValidate(signed, out _, out var error))
        {
            context.Result.IsError = true;
            context.Result.Error = "invalid_authorization_details";
            context.Result.ErrorDescription = error;
        }
        return Task.CompletedTask;
    }
}