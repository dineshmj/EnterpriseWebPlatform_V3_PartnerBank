# EWP V3 - Partner Bank

A **separate organisation** for the EnterpriseWebPlatform V3 proof of concept: a partner bank whose products EWP (Four Walls Inc.) offers to its customers. It lives in its own repository and runs on its own machine (the home PC), so the integration crosses a real network, between two hosts with their own certificates.

The journey follows the FAPI 2.0 pattern - mutual TLS and a pushed authorization request first, the Partner's window only after that:

```
1. EWP's server ──mTLS (EWP's client certificate)──► authorization server: PAR with a SIGNED request object (JAR)
   stating exactly what is authorised (authorization_details) ──► request_uri (60 s, once)
   Customer's browser ──request_uri──► Partner Bank sign-in, ALWAYS fresh (prompt=login) - or, the first time,
   registration with the details EWP verified + a one-time code ──► CONSENT (exactly what EWP asks) ──► back to EWP
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

6. The customer, any time, in the Partner's own window: https://partner-products.ewp.home.arpa:47402/My

EWP's counter - EWP as the Partner Bank's DISTRIBUTOR (assisted channel: the customer signs paper forms
at EWP's counter and never signs in here; see doc/Architecture.md D14):

7. EWP's server ──mTLS──► token (client credentials, scope partner.distribution): EWP's OWN token, bound to its certificate
   Form VII (once): POST /api/distribution/gold-accounts      body: EWP's SIGNED instruction (application/jwt)
                    ──► Digital Gold account PB-DGA-…; the Partner Bank sends the customer an SMS itself
   Each purchase - the customer's transfer authorization slip:
                    POST /api/distribution/gold-orders        body: SIGNED instruction ──► order, grams locked
                    then the payment advice as in 5; EWP's commission is recorded on the order
   The holding:     GET  /api/distribution/gold-accounts/{number}  ──► grams, today's value, activity
   A staff view:    POST /api/distribution/staff-views         body: SIGNED instruction ──► a one-time request_uri (60 s)
                    the staff member's browser ──► https://partner-products.ewp.home.arpa:47402/StaffView?request_uri=…  (read-only, once)
   ("My Partner Bank" - grams, today's value, applications, activity; read with the customer's own token)
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
.\ps\Reset-PartnerBank.ps1          # erase the data (1) or the data and the customers' sign-in accounts (2); asks first
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
dotnet run tools\HandshakeCheck\HandshakeCheck.cs -- 192.168.1.12   # the authorization server: 22 checks
dotnet run tools\JourneyCheck\JourneyCheck.cs -- 192.168.1.12       # the whole journey, as EWP AND as the customer's browser: 70 checks
```

Unit tests need no running service: `dotnet test tst\PartnerBank.Api.Tests` (NUnit, 32 tests). They cover what makes a distributor instruction once-only, without a network: the signature check (forgeries, missing `jti`, too long a life, another caller), the same instruction resent or committed twice at the same moment (one account, one order; the unique indexes decide), and the staff view's one use (an instruction pushed twice, a `request_uri` redeemed twice or by 32 requests at once, after 60 seconds). They use the Partner Bank's own model on an in-memory SQLite database and a throw-away signing key in a temp folder, never `.ewp-pki`. Which threat each test covers: [doc/Threat-Model.md](doc/Threat-Model.md) section 5.

Leave out the address for the Partner Bank on the same machine. The handshake check prints a link for a browser test; the browser needs `ewp-trust-root.cer` in "Trusted Root Certification Authorities" to open it without a warning.

Customer accounts live in the authorization server's own database, `src\PartnerBank.AuthorizationServer\partner-identity.db` (passwords hashed). The demo customers `alex.partner` and `sam.partner` (Development, see `appsettings.Development.json`) are created there at start-up; customers EWP sends register themselves. In Development the registration's one-time code is shown on the page (and in the authorization server's console) in place of an SMS. Delete `src\PartnerBank.Api\partner-bank.db` (with the API stopped) to start the Partner Bank's data again - and after an update that adds tables: the API refuses to start with an older database and says so. To look inside: `sqlite3 src\PartnerBank.Api\partner-bank.db "select Number, Status, AmountAud, Grams from GoldOrders;"`.

## Containers and CI

Every pull request runs the CI gates (build, unit tests, vulnerable packages - [doc/CI.md](doc/CI.md)); `main` takes only a squash-merged pull request with "CI result" green. GitHub Actions builds the three container images (`partner-bank-authorization-server`, `partner-bank-api`, `partner-bank-web`), scans them, starts each one locked down with throw-away certificates, and publishes them to GHCR from `main` only. The images keep TLS inside the deployable (mutual TLS needs the client certificate in the handshake); an environment mounts the certificates and one `/data` volume. Run-time contract and known gaps: [doc/Containers.md](doc/Containers.md). Running locally is unchanged (`Start-PartnerBank.ps1`).

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
| 9 | **Browser security headers**: CSP without inline code, never framed, no MIME sniffing, no referrer; `form-action` allows this site plus only the origins a form's redirects legitimately lead to, from configuration (the window: the authorization server, for sign-out; the authorization server: its clients' registered redirect addresses) - browsers apply `form-action` to those redirects too | `PartnerBank.Security/SecurityHeaders.cs` |
| 10 | **No open redirect** after sign-in; one message for an unknown user and a wrong password | `Pages/Account/Login.cshtml.cs` |
| 10a | **Customer accounts of the Partner Bank's own**: passwords hashed (ASP.NET Core Identity's PasswordHasher, PBKDF2); 5 wrong passwords lock the account for 15 minutes; an unknown username costs the same work as a known one | `Customers/CustomerStore.cs` |
| 10b | **Registration only for a customer the referring organisation verified**, from its SIGNED request (the identity is shown read-only, never typed); once per referred customer (unique link); passwords of 12+ characters; the account exists only after a one-time code (6 digits, 5 minutes, 5 tries, stored as a salted hash); signed in with `amr` pwd + otp | `Pages/Account/Register.cshtml.cs`, `Verify.cshtml.cs`, `Customers/` |
| 10c | **A fresh sign-in for every request from EWP** (`prompt=login` must be inside the signed request - a bank's desk, a browser that is not the customer's); a 10-minute idle session; sign-out ends the session (with an `id_token_hint`, or after asking) | `Authorization/AuthorizationDetailsRequestValidator.cs`, `Program.cs`, `Pages/Account/Logout.cshtml.cs` |
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
| 18d | **Distributor instructions are SIGNED by EWP** (a JWT in the body, `application/jwt`): ES256 with EWP's request-signing key only, type `partner-instruction+jwt`, issuer = the calling client, audience `partner-api`, at most 5 minutes' life, an ID (`jti`); unsigned, altered, expired or otherwise-signed instructions are refused - the Partner Bank keeps signed evidence of every instruction | `Distribution/SignedInstructions.cs` |
| 18e | **Strict instruction contents** (allow-list, unknown fields refused): the customer identified by EWP (`kycVerified`), an adult, an Australian mobile; Form VII signed within 30 days, a transfer slip signed TODAY (Sydney); the SHA-256 of each scan; AUD with two decimals, at most 50,000 | `Distribution/Instructions.cs` |
| 18f | **Once only, enforced by the database**: one Digital Gold account per customer of a distributor, per Form VII and per instruction; one purchase per slip and per instruction; the same instruction again returns the same result | `Data/PartnerDbContext.cs`, `Gold/GoldDesk.cs` |
| 18g | **The Partner Bank's own checks of a distributor's purchase**: only for an account THIS distributor introduced (any other is a 404) and for that customer; the slip's mobile number must be the account's; the customer is told by SMS of every account and order (simulated: the log and the activity, the number masked) | `Gold/GoldDesk.cs`, `Api/DistributionApi.cs` |
| 18h | **Distribution needs EWP's own bound token with the distribution scope** (client credentials - no customer, no consent page: the customer's consent is the signed form); a customer-consent token is refused there | `Program.cs`, AS `Configuration/Config.cs` |
| 18i | **Staff view handoff, PAR-style** (D15): EWP pushes a SIGNED `staff_account_view` (one account, its customer, the staff member; mutual TLS, EWP's bound token) to `POST /api/distribution/staff-views` and gets an opaque `request_uri` (256 bits, 60 seconds, ONE use; only its SHA-256 kept; each signed instruction pushes once); only for an account EWP introduced and its own customer (404 otherwise); unknown fields refused; the answer carries nothing about the account | `Api/DistributionApi.cs`, `Distribution/StaffViews.cs`, `Distribution/Instructions.cs` |
| 19 | **The authorization server is trusted only through the PoC's root** (discovery, keys): a server with another certificate is never believed | `PartnerBank.Security/TrustedBackchannel.cs` |

**Window (`PartnerBank.Web`)**

| # | Control | Where |
|---|---|---|
| 20 | **No data, no decisions**: the window calls the internal API with the customer's own access token, kept in its server-side session cookie - never in the browser | `Services/PartnerApi.cs` |
| 21 | **Single sign-on as a confidential client** (secret, code + PKCE, PAR) asking for the customer scope | `Program.cs` |
| 21a | **"My Partner Bank" shows only the signed-in customer's own** grams, applications and activity: the internal API takes the customer from the token, never from the request; times shown in Sydney time | `Pages/My.cshtml.cs`, `PartnerBank.Api/Api/InternalApi.cs` |
| 21b | **No silent reuse of an old sign-in, and a real sign-out**: the window accepts a Partner Bank sign-in only if it is at most 2 minutes old (`max_age`), so right after a consent there is no second password but later - perhaps another person at the desk - there is; "Sign out" (POST, anti-forgery while the window still has a session) ends the window's session AND the Partner Bank's - also after the window's session has already expired (the Partner Bank then asks before signing out) | `Program.cs`, `Pages/SignOut.cshtml.cs` |
| 22 | **The session cookie** is `__Host-`, HttpOnly, Secure, SameSite=Lax, 10 minutes sliding; anti-forgery tokens on every form; one message for every link failure | `Program.cs`, `Pages/` |
| 22a | **The staff view page** (`/StaffView?request_uri=...`): redeemed once on the localhost-only API port (the request_uri is the authorisation - no customer signs in); read-only, no cookie, no form, `Cache-Control: no-store`, `Referrer-Policy: no-referrer`; it names the staff member and branch from EWP's signed request; reloading shows "no longer available"; every push and opening is logged | `Pages/StaffView.cshtml(.cs)`, API `Api/InternalApi.cs` |

**Images and CI** ([doc/Containers.md](doc/Containers.md), [doc/CI.md](doc/CI.md))

| # | Control | Where |
|---|---|---|
| 23 | **Images built only by CI, from a commit**: hadolint, Trivy (a HIGH / CRITICAL vulnerability with a fix, or any secret, stops the image), a start check, and only then - on `main` - published with an SBOM and SLSA provenance, tagged with the commit SHA; actions pinned to commit SHAs | `.github/workflows/container-images.yml` |
| 24 | **Nothing secret in an image**: development settings, key material (certificates, passwords, IdentityServer's `keys/`) and the local databases never enter the build context; the image runs as `Production` (no demo customers, no codes on the page); certificates are mounted at run time, the two client secrets come from a secret store | `.dockerignore`, `src/*/Dockerfile` |
| 25 | **A locked-down runtime**: chiseled base pinned by tag and digest (no shell, no package manager), non-root UID 1654, program files read-only, ONE writable folder (`/data`); TLS still ends in the deployable itself, so the client-certificate checks and certificate-bound tokens (1, 4, 14) work unchanged in a container; the API's internal port stays localhost-only in the image. CI starts every image that way (read-only, no capabilities) with a throw-away PKI before publishing it | `src/*/Dockerfile`, `.github/scripts/start-check.sh` |
| 26 | **Keys survive a restart**: IdentityServer's signing keys and the Data Protection keys (sessions; they also encrypt the signing keys) in a configured folder - a restart never mints new signing keys that would make issued tokens unverifiable | `PartnerBank.Security/DataProtectionKeys.cs`, AS `Program.cs` (`SigningKeysPath`) |
| 27 | **CI gates on every pull request**: vulnerable NuGet packages (HIGH / CRITICAL fails), Release build, the check tools compile, unit tests; `main` accepts only a squash-merged pull request with "CI result" green | `.github/workflows/ci.yml`, ruleset `main` |

**Not done in this PoC** (a production partner would): certificate revocation checks (the PoC's root publishes no revocation list), address-based sign-in throttling, a real SMS / push provider for one-time codes, persistent grant and key stores (in memory / local files), a real credit decision, a Duende licence, and consent screens.