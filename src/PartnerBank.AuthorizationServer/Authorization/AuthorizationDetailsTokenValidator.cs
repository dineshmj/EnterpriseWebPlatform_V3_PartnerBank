using System.Security.Claims;

using Duende.IdentityServer;
using Duende.IdentityServer.Validation;

using PartnerBank.AuthorizationServer.Configuration;

namespace PartnerBank.AuthorizationServer.Authorization;

/// <summary>
/// When EWP exchanges an authorization code, the access token carries the authorization detail the
/// customer approved (RFC 9396: the authorization_details claim) - so the Partner API knows exactly
/// what this token allows, not just a broad scope.
/// </summary>
public sealed class AuthorizationDetailsTokenValidator(PartnerBankOptions settings) : ICustomTokenRequestValidator
{
    public Task ValidateAsync(CustomTokenRequestValidationContext context, CancellationToken cancellationToken)
    {
        var request = context.Result!.ValidatedRequest;
        if (request.ClientId != settings.EwpClient.ClientId || request.AuthorizationCode is null)
            return Task.CompletedTask;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ From the authorization code, where the detail of the SIGNED request object was kept -
        // │ validated again here, and only then put into the token. No code, no signed request:
        // │ no authorization detail.
        // └──────────────────────────────────────────────────────────────────────────────────────
        string? signed = null;
        request.AuthorizationCode.Properties?.TryGetValue(AuthorizationDetails.ParameterName, out signed);
        if (AuthorizationDetails.TryValidate(signed, out var normalized, out var error))
        {
            request.ClientClaims.Add(new Claim(AuthorizationDetails.ParameterName, normalized!, IdentityServerConstants.ClaimValueTypes.Json));
        }
        else
        {
            context.Result.IsError = true;
            context.Result.Error = "invalid_grant";
            context.Result.ErrorDescription = error;
        }
        return Task.CompletedTask;
    }
}