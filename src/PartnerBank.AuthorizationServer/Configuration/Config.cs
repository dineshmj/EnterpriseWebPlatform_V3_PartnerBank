using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;

using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

namespace PartnerBank.AuthorizationServer.Configuration;

/// <summary>What the Partner Bank's authorization server knows: its scopes, its one client (EWP) and its demo customers.</summary>
public static class Config
{
    public const string ProductsRead = "partner.products.read";
    public const string ApplicationsWrite = "partner.applications.write";
    public const string Customer = "partner.customer";

    public static IEnumerable<IdentityResource> IdentityResources =>
    [
        new IdentityResources.OpenId(),
        new IdentityResources.Profile()
    ];

    public static IEnumerable<ApiScope> ApiScopes =>
    [
        new(ProductsRead, "See the Partner Bank's products"),
        new(ApplicationsWrite, "Apply for a Partner Bank product"),
        new(Customer, "The signed-in customer, through the Partner's own window")
    ];

    public static IEnumerable<ApiResource> ApiResources =>
    [
        new("partner-api", "Partner Bank API") { Scopes = { ProductsRead, ApplicationsWrite, Customer } }
    ];

    public static IEnumerable<Client> Clients(EwpClientOptions ewp, X509Certificate2 ewpRequestSigning, ProductsWindowClientOptions productsWindow)
    {
        if (string.IsNullOrWhiteSpace(productsWindow.ClientId) || string.IsNullOrWhiteSpace(productsWindow.Secret))
            throw new InvalidOperationException("PartnerBank:ProductsWindowClient:ClientId and Secret are required.");

        yield return new Client
        {
            ClientId = productsWindow.ClientId,
            ClientName = "Partner Bank products",

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ The Partner's OWN web app, on the Partner's own machine: a confidential client with
            // │ a secret (stored hashed here), code + PKCE, and - like every client - PAR. The
            // │ customer is already signed in at this server (they came through EWP's window), so
            // │ the window opens without asking again (single sign-on).
            // └──────────────────────────────────────────────────────────────────────────────────────
            ClientSecrets = { new Secret(productsWindow.Secret.Sha256()) },
            AllowedGrantTypes = GrantTypes.Code,
            RequirePushedAuthorization = true,
            RequirePkce = true,
            AllowPlainTextPkce = false,
            RedirectUris = productsWindow.RedirectUris,
            PostLogoutRedirectUris = productsWindow.PostLogoutRedirectUris,
            AllowedScopes = { IdentityServerConstants.StandardScopes.OpenId, IdentityServerConstants.StandardScopes.Profile, Customer },
            RequireConsent = false,
            AllowOfflineAccess = false,
            AuthorizationCodeLifetime = 60,

            // The customer's token for the internal API lives as long as the window's session (20 minutes).
            AccessTokenLifetime = 1200
        };

        if (string.IsNullOrWhiteSpace(ewp.ClientId) || string.IsNullOrWhiteSpace(ewp.CertificateSubject))
            throw new InvalidOperationException("PartnerBank:EwpClient:ClientId and CertificateSubject are required.");

        yield return new Client
        {
            ClientId = ewp.ClientId,
            ClientName = "Four Walls Inc. (EWP)",

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ RFC 8705 tls_client_auth: EWP has NO shared secret. It proves who it is with its
            // │ client certificate in the TLS handshake; the subject must match exactly, and the
            // │ certificate handler has already checked that it chains to the PoC's trust root and
            // │ is meant for client authentication. A renewed certificate (same subject) still works.
            // └──────────────────────────────────────────────────────────────────────────────────────
            ClientSecrets =
            {
                new Secret(ewp.CertificateSubject, "EWP client certificate (tls_client_auth)")
                {
                    Type = IdentityServerConstants.SecretTypes.X509CertificateName
                },
                // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                // │ EWP's request-signing key (public part, as a JWK with its key ID): it verifies the
                // │ signature of EWP's request objects. A separate key from the mutual-TLS one; it
                // │ authenticates no connection (no client-authentication validator is registered for
                // │ signed JWTs at the token endpoint).
                // └──────────────────────────────────────────────────────────────────────────────────────
                new Secret(Authorization.RequestSigningKey.ToJwk(ewpRequestSigning), "EWP request signing (JAR)")
                {
                    Type = IdentityServerConstants.SecretTypes.JsonWebKey
                }
            },

            // Code: a person (our customer) at the Partner's window. Client credentials: EWP's
            // own calls afterwards, e.g. an application's status.
            AllowedGrantTypes = GrantTypes.CodeAndClientCredentials,

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ FAPI 2.0 style: the authorization request is PUSHED first (server to server, over
            // │ mutual TLS) - the browser carries only a one-time request_uri, so nothing in the
            // │ request can be read or changed on the way. PKCE (S256) on top.
            // └──────────────────────────────────────────────────────────────────────────────────────
            RequirePushedAuthorization = true,
            RequirePkce = true,
            AllowPlainTextPkce = false,

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ FAPI 2.0 Message Signing: every authorization request from EWP is a SIGNED request
            // │ object (JAR, RFC 9101) - signature, issuer, audience and lifetime are checked; an
            // │ unsigned or altered request is refused. The Partner Bank keeps signed evidence of
            // │ exactly what EWP asked for (non-repudiation).
            // └──────────────────────────────────────────────────────────────────────────────────────
            RequireRequestObject = true,

            // The approved authorization_details travel into the access token under their own name.
            ClientClaimsPrefix = string.Empty,
            AlwaysSendClientClaims = true,

            RedirectUris = ewp.RedirectUris,
            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                ProductsRead,
                ApplicationsWrite
            },

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ The customer approves EVERY request from EWP on the consent page, seeing exactly
            // │ what is asked (the signed authorization_details) - one decision per request,
            // │ never remembered: approving one purchase is not approving the next.
            // └──────────────────────────────────────────────────────────────────────────────────────
            RequireConsent = true,
            AllowRememberConsent = false,
            AllowOfflineAccess = false,

            // Short-lived: a code for one minute, a token for five.
            AuthorizationCodeLifetime = 60,
            AccessTokenLifetime = 300,
            AccessTokenType = AccessTokenType.Jwt
        };
    }
}