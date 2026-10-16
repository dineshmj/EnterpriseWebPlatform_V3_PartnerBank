# EWP V3 - Partner Bank

A **separate organisation** for the EnterpriseWebPlatform V3 proof of concept: a partner bank whose products EWP (Four Walls Inc.) offers to its customers. It lives in its own repository and runs on its own machine (the home PC), so the integration crosses a real network, between two hosts with their own certificates.

The journey follows the FAPI 2.0 pattern - mutual TLS and a pushed authorization request first, the Partner's window only after that:

```
1. EWP's server ──mTLS (EWP's client certificate)──► authorization server: PAR with a SIGNED request object (JAR)
   stating exactly what is authorised (authorization_details) ──► request_uri (60 s, once)
   Customer's browser ──request_uri──► Partner Bank sign-in ──► CONSENT (exactly what EWP asks) ──► back to EWP
   with a one-time code (or access_denied, without one, if the customer declines)
   EWP's server ──mTLS──► token: an access token for THIS customer, BOUND to EWP's certificate,
   carrying the authorization_details the customer authorised

2. EWP's server ──mTLS + token──► Partner API  POST /api/handoffs {ewpReference}  ──► one-time window link (2 min)

3. EWP opens the link: the Partner's products window - the customer is already signed in at the Partner
   (single sign-on, no second password) - shows the products; the customer applies

4. EWP's server ──mTLS + token──► Partner API  GET /api/applications?ewpReference=…  ──► the application's status

Digital Gold (the customer approves an AUD amount; the grams are locked when the order is placed):

5. EWP's server ──mTLS + the purchase token──► POST /api/gold-orders  ──► order, grams locked 5 minutes, AWAITING_PAYMENT
   EWP pays the Partner Bank (EWP's Payments) ──► POST /api/gold-orders/{number}/payment-advice  ──► ALLOCATED
   (or POST /api/gold-orders/{number}/cancel when the payment failed)
   EWP's server ──mTLS + a holdings-view token──► GET /api/holdings  ──► grams, today's value, activity
```

| Deployable | Ports | What |
|---|---|---|
| `PartnerBank.AuthorizationServer` - Duende IdentityServer 8 | 47401 public (people, the Partner's window), 47411 mutual TLS (EWP's server) | sign-in, PAR, tokens |
| `PartnerBank.Api` - ASP.NET Core minimal APIs, **SQLite (`partner-bank.db`)** | 47403 external (EWP's server, mutual TLS), 47404 internal (the Partner's window - **localhost only**) | the Partner Bank's domain microservice: it alone owns the data |
| `PartnerBank.Web` - ASP.NET Core Razor Pages | 47402 (the customer's browser) | the Partner's window: holds no data, calls the internal API with the customer's own token |
| `PartnerBank.Security` - class library | - | what they share: TLS, client certificates, trusted back-channel, security headers, port guards |

Architecture, decisions and the security model: [doc/Architecture.md](doc/Architecture.md) and [doc/Threat-Model.md](doc/Threat-Model.md).

Host names: `partner-auth`, `partner-products` and `partner-api` `.ewp.home.arpa` (`.home.arpa` is reserved for home networks, RFC 8375). Demo: an application is approved two minutes after it is submitted (`DecisionDelaySeconds`).

## Certificates

The certificates come from EWP's repository - its local certificate authority plays the neutral trust body both organisations accept:

```powershell
# On the laptop, in EnterpriseWebPlatform_V3:
powershell.exe -ExecutionPolicy Bypass -File .\ps\pki\New-EwpLocalPki.ps1
```

It writes to `%USERPROFILE%\.ewp-pki` (outside every repository). This repository needs four of those files:

| File | What |
|---|---|
| `ewp-trust-root.cer` | the root that client certificates (and the Partner's own servers) must chain to |
| `ewp-request-signing.cer` | EWP's request-signing certificate - the PUBLIC part only: its key verifies EWP's signed requests |
| `partner-bank-server.pfx` | the Partner Bank's TLS certificate (all three host names) |
| `partner-bank-server.pfx.password` | its password |

On the home PC, copy those four into `%USERPROFILE%\.ewp-pki`. Never copy `ewp-trust-root.pfx` (the root's private key), `ewp-partner-client.pfx` or `ewp-request-signing.pfx` (EWP's private keys) - they belong to the other organisations.

## Running

The simplest way, on either machine - it builds once, then opens one window per deployable (authorization server, API, window - each after the one it depends on):

```powershell
.\ps\Start-PartnerBank.ps1          # on the laptop: localhost only
.\ps\Start-PartnerBank.ps1 -Lan     # on the home PC: reachable from the home network
.\ps\Start-PartnerBank.ps1 -Stop    # stop all three
```

By hand, or to debug one of them:

**On the laptop** (development): start both deployables from Visual Studio (multiple start-up projects), or

```powershell
dotnet run --project src\PartnerBank.AuthorizationServer
dotnet run --project src\PartnerBank.Api
dotnet run --project src\PartnerBank.Web
```

They listen on `localhost` only. The API and the window reach the authorization server (and the window the API) at 127.0.0.1 (`ResolvePartnerHostsTo`), so no hosts-file entry is needed for them to talk.

**On the home PC** (192.168.1.12), to be reachable from the laptop:

```powershell
# Once, in an administrator PowerShell - only from the home network (Private profile):
New-NetFirewallRule -DisplayName "EWP Partner Bank - authorization server" -Direction Inbound -Protocol TCP -LocalPort 47401 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "EWP Partner Bank - authorization server (mTLS)" -Direction Inbound -Protocol TCP -LocalPort 47411 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "EWP Partner Bank - products window" -Direction Inbound -Protocol TCP -LocalPort 47402 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "EWP Partner Bank - API (mTLS)" -Direction Inbound -Protocol TCP -LocalPort 47403 -Action Allow -Profile Private

# Each time:
.\ps\Start-PartnerBank.ps1 -Lan
```

No rule for 47404: the API's internal port listens on localhost only, even with `-Lan`.

The first start of each asks Windows Firewall: allow it on **Private networks** only (never Cancel - that adds a block rule).

And on the laptop, in `C:\Windows\System32\drivers\etc\hosts` (administrator) - for the browser:

```
192.168.1.12   partner-auth.ewp.home.arpa partner-products.ewp.home.arpa partner-api.ewp.home.arpa
```

## Checking it

Both tools are single-file .NET 10 apps acting from EWP's side (EWP's client certificate and request-signing key, trusting only the PoC's root); they connect to the given address directly, so they need no hosts-file entry. The handshake check also proves the refusals: an unsigned request, one altered on the way, one signed with another key, and missing, unknown or invalid `authorization_details`.

```powershell
dotnet run tools\HandshakeCheck\HandshakeCheck.cs -- 192.168.1.12   # the authorization server: 21 checks
dotnet run tools\JourneyCheck\JourneyCheck.cs -- 192.168.1.12       # the whole journey, as EWP AND as the customer's browser: 31 checks
```

Leave out the address for the Partner Bank on the same machine. The handshake check prints a link for a browser test; the browser needs `ewp-trust-root.cer` in "Trusted Root Certification Authorities" to open it without a warning.

Demo customers (Development only): `alex.partner` and `sam.partner` - see the authorization server's `appsettings.Development.json`. Delete `src\PartnerBank.Api\partner-bank.db` (with the API stopped) to start the Partner Bank's data again - and after an update that adds tables: the API refuses to start with an older database and says so. To look inside: `sqlite3 src\PartnerBank.Api\partner-bank.db "select Number, Status, AmountAud, Grams from GoldOrders;"`.

## Security controls

**Authorization server**

| # | Control | Where |
|---|---|---|
| 1 | **Mutual TLS client authentication (RFC 8705 `tls_client_auth`)**: EWP has no shared secret; its client certificate must chain to the PoC's trust root ONLY, be valid now, carry the client-authentication purpose, and have exactly the configured subject | `PartnerBank.Security/ClientCertificates.cs`, `Configuration/Config.cs` |
| 2 | **Pushed Authorization Requests required** for every client: an authorization request that was not pushed first is refused; a pushed one is valid for 60 s and used once | `Program.cs` (`PushedAuthorization`), `Config.cs` |
| 3 | **PKCE (S256) required**, plain PKCE refused | `Config.cs` |
| 4 | **Certificate-bound access tokens** (`cnf.x5t#S256`): a stolen token is useless without EWP's private key | `Program.cs` (`MutualTls`) |
| 5 | **Short lifetimes**: authorization code 60 s, access token 5 min, no refresh tokens | `Config.cs` |
| 6 | **Two ports, TLS 1.2 / 1.3 only**: the public port never asks for a client certificate (no certificate picker for customers); the mTLS port REQUIRES one in the TLS handshake - without it there is no connection | `appsettings.json` (`Kestrel:Endpoints`), `PartnerBank.Security/ClientCertificates.cs` |
| 6a | **The mTLS port serves only PAR, token, revocation and introspection**; every page for people answers 404 there | `Program.cs`, `PartnerBank.Security/PortGuard.cs` |
| 7 | **Fixed issuer**, never taken from the request's Host header | `Program.cs` |
| 8 | **Fail closed at start-up**: no server certificate, password or trust root - no server | `PartnerBank.Security/TlsOptions.cs` |
| 9 | **Browser security headers**: CSP without inline code, never framed, no MIME sniffing, no referrer | `PartnerBank.Security/SecurityHeaders.cs` |
| 10 | **No open redirect** after sign-in; one message for an unknown user and a wrong password | `Pages/Account/Login.cshtml.cs` |
| 11 | **Error details only in Development** | `Pages/Error.cshtml.cs` |
| 12 | **The products window is a confidential client** with a hashed secret, code + PKCE and PAR - like every client | `Config.cs` |
| 12a | **Signed requests (JAR, RFC 9101 - FAPI 2.0 Message Signing)**: every authorization request from EWP is a request object signed with EWP's request-signing key (ES256, registered as a JWK with its key ID; a separate key from the mutual-TLS one), typed `oauth-authz-req+jwt` (strict), with issuer, audience and lifetime checked; an unsigned, altered or wrongly signed request is refused | `Config.cs` (`RequireRequestObject`), `Program.cs` (`StrictJarValidation`), `Authorization/RequestSigningKey.cs` |
| 12b | **Exactly what is authorised (RAR, RFC 9396)**: `authorization_details` must sit INSIDE the signed request - exactly one detail of a known type (`digital_gold_purchase`, `product_application`, `holdings_view`), only its fields, every value allow-listed (AUD amounts 0.01-50,000 in cents, BSB and account formats, the EWP-verified customer, the acting staff member); anything else refuses the request | `Authorization/AuthorizationDetails.cs`, `Authorization/AuthorizationDetailsRequestValidator.cs` |
| 12c | **The approved detail travels into the access token** (`authorization_details` claim) through the stored authorization code - never through the browser - validated again on the way | `Authorization/AuthorizationDetailsResponseGenerator.cs`, `Authorization/AuthorizationDetailsTokenValidator.cs` |
| 12d | **The customer's consent for every request from EWP** (no consent is remembered): the page shows the signed `authorization_details` in plain words, who offered it (EWP and its staff member) and which of the customer's details EWP shared; account numbers masked to the last four digits; only an explicit Approve grants anything - anything else returns `access_denied` without a code | `Pages/Consent/`, `Config.cs` (`RequireConsent`, `AllowRememberConsent`) |

**API (`PartnerBank.Api`)**

| # | Control | Where |
|---|---|---|
| 13 | **Each port serves one path prefix**: `/api` only on the external port (a client certificate is REQUIRED in the TLS handshake), `/internal` only on the internal port, which listens on **localhost only** | `Program.cs`, `appsettings.json`, `PartnerBank.Security/PortGuard.cs` |
| 14 | **Bound tokens on every external call**: an access token (`at+jwt`, audience `partner-api`) is accepted only over a connection whose client certificate chains to the trust root AND matches the token's `cnf.x5t#S256` - the same token over another certificate is refused | `Security/BoundTokens.cs` |
| 15 | **Only EWP on the external API** (its client ID), each endpoint with its scope; EWP sees only the applications it referred | `Program.cs` (policies), `Api/ExternalApi.cs` |
| 16 | **Only the Partner's window on the internal API** (its client ID, the `partner.customer` scope, a person in the token); every operation is for the customer the token names - EWP's token is refused there | `Program.cs` (policies), `Api/InternalApi.cs` |
| 17 | **A window link is for ONE customer**: created only from the customer's own token (a machine token is refused), random 256 bits, valid 2 minutes, opens once, and only for the customer it names; one application per link, within 30 minutes | `Api/ExternalApi.cs`, `Api/InternalApi.cs` |
| 18 | **The Partner's data lives in the API only**; inputs are validated before they reach the database (references 1-64 safe characters, known product codes) | `Data/PartnerDbContext.cs`, `Api/References.cs` |
| 18a | **Digital Gold from the consented authorisation only**: an order takes its amount, paying account, customer and staff member from the token's approved `digital_gold_purchase` - never from a request body; one order per authorisation (the token's ID is unique), a token approved for anything else is refused | `Api/GoldApi.cs`, `Gold/GoldDesk.cs`, `Data/PartnerDbContext.cs` |
| 18b | **Payment advice**: only for EWP's own order, only for the exact amount, only while it awaits payment; the same advice again changes nothing (grams allocated once); allocated gold cannot be cancelled; grams rounded DOWN, never more gold than paid for; a payment after the 5-minute lock is allocated at the price of the moment, and the activity says so | `Gold/GoldDesk.cs`, `Gold/GoldPrice.cs` |
| 18c | **Holdings only with a consented holdings view**, only the customer's own grams and activity; the gold price is public, readable without a token on the localhost-only internal port | `Api/GoldApi.cs` |
| 19 | **The authorization server is trusted only through the PoC's root** (discovery, keys): a server with another certificate is never believed | `PartnerBank.Security/TrustedBackchannel.cs` |

**Window (`PartnerBank.Web`)**

| # | Control | Where |
|---|---|---|
| 20 | **No data, no decisions**: the window calls the internal API with the customer's own access token, kept in its server-side session cookie - never in the browser | `Services/PartnerApi.cs` |
| 21 | **Single sign-on as a confidential client** (secret, code + PKCE, PAR) asking for the customer scope | `Program.cs` |
| 22 | **The session cookie** is `__Host-`, HttpOnly, Secure, SameSite=Lax, 20 minutes sliding; anti-forgery tokens on every form; one message for every link failure | `Program.cs`, `Pages/` |

**Not done in this PoC** (a production partner would): certificate revocation checks (the PoC's root publishes no revocation list), sign-in throttling and lockout, real customer accounts (demo users only), persistent grant and key stores (in memory / local files), a real credit decision, a Duende licence, and consent screens.