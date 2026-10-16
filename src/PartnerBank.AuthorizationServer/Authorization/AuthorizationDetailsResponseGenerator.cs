using Duende.IdentityServer.Configuration;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.ResponseHandling;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Stores;
using Duende.IdentityServer.Validation;

namespace PartnerBank.AuthorizationServer.Authorization;

/// <summary>
/// Keeps the approved authorization detail WITH the authorization code: IdentityServer stores the
/// code, and the token request later reads the detail from it - nothing travels through the browser.
/// </summary>
public sealed class AuthorizationDetailsResponseGenerator(
    IdentityServerOptions options,
    TimeProvider timeProvider,
    ITokenService tokenService,
    IKeyMaterialService keyMaterialService,
    IAuthorizationCodeStore authorizationCodeStore,
    ILogger<AuthorizeResponseGenerator> logger,
    IEventService events)
    : AuthorizeResponseGenerator(options, timeProvider, tokenService, keyMaterialService, authorizationCodeStore, logger, events)
{
    protected override async Task<AuthorizationCode> CreateCodeAsync(ValidatedAuthorizeRequest request, CancellationToken ct)
    {
        var code = await base.CreateCodeAsync(request, ct);

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Only the detail from the SIGNED request object, validated again and re-serialised;
        // │ an invalid one never reaches the code (the request validator has already refused it).
        // └──────────────────────────────────────────────────────────────────────────────────────
        var signed = AuthorizationDetails.FromRequestObject(request.RequestObjectValues);
        if (signed is not null && AuthorizationDetails.TryValidate(signed, out var normalized, out _))
        {
            code.Properties ??= new Dictionary<string, string>();
            code.Properties[AuthorizationDetails.ParameterName] = normalized!;
        }
        return code;
    }
}