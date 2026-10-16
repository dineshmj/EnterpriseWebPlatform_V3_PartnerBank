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