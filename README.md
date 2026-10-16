# EWP V3 - Partner Bank

A **separate organisation** for the EnterpriseWebPlatform V3 proof of concept: a partner bank whose products EWP (Four Walls Inc.) offers to its customers. It lives in its own repository and runs on its own machine (the home PC), so the integration crosses a real network, between two hosts with their own certificates.

The journey follows the FAPI 2.0 pattern - mutual TLS and a pushed authorization request first, the Partner's window only after that:

```
1. EWP's server ──mTLS (EWP's client certificate)──► authorization server: PAR ──► request_uri (60 s, once)
   Customer's browser ──request_uri──► Partner Bank sign-in ──► back to EWP with a one-time code
   EWP's server ──mTLS──► token: an access token for THIS customer, BOUND to EWP's certificate

2. EWP's server ──mTLS + token──► Partner API  POST /api/handoffs {ewpReference}  ──► one-time window link (2 min)

3. EWP opens the link: the Partner's products window - the customer is already signed in at the Partner
   (single sign-on, no second password) - shows the products; the customer applies

4. EWP's server ──mTLS + token──► Partner API  GET /api/applications?ewpReference=…  ──► the application's status
```

| Deployable | Ports | What |
|---|---|---|
| `PartnerBank.AuthorizationServer` - Duende IdentityServer 8 | 47401 public (people, the products window), 47411 mutual TLS (EWP's server) | sign-in, PAR, tokens |
| `PartnerBank.Products` - ASP.NET Core, SQLite | 47402 window (the customer's browser), 47403 API (EWP's server, mutual TLS) | the products window and the Partner API |
| `PartnerBank.Security` - class library | - | what both share: TLS, client certificates, trusted back-channel, security headers, port guard |

Host names: `partner-auth`, `partner-products` and `partner-api` `.ewp.home.arpa` (`.home.arpa` is reserved for home networks, RFC 8375). Demo: an application is approved two minutes after it is submitted (`DecisionDelaySeconds`).

## Certificates

The certificates come from EWP's repository - its local certificate authority plays the neutral trust body both organisations accept:

```powershell
# On the laptop, in EnterpriseWebPlatform_V3:
powershell.exe -ExecutionPolicy Bypass -File .\ps\pki\New-EwpLocalPki.ps1
```

It writes to `%USERPROFILE%\.ewp-pki` (outside every repository). This repository needs three of those files:

| File | What |
|---|---|
| `ewp-trust-root.cer` | the root that client certificates (and the Partner's own servers) must chain to |
| `partner-bank-server.pfx` | the Partner Bank's TLS certificate (all three host names) |
| `partner-bank-server.pfx.password` | its password |

On the home PC, copy those three into `%USERPROFILE%\.ewp-pki`. Never copy `ewp-trust-root.pfx` (the root's private key) or `ewp-partner-client.pfx` (EWP's private key) - they belong to the other organisations.

## Running

**On the laptop** (development): start both deployables from Visual Studio (multiple start-up projects), or

```powershell
dotnet run --project src\PartnerBank.AuthorizationServer
dotnet run --project src\PartnerBank.Products
```

They listen on `localhost` only. `PartnerBank.Products` reaches the authorization server at 127.0.0.1 (`ResolvePartnerHostsTo`), so no hosts-file entry is needed for the two to talk.

**On the home PC** (192.168.1.12), to be reachable from the laptop:

```powershell
# Once, in an administrator PowerShell - only from the home network (Private profile):
New-NetFirewallRule -DisplayName "EWP Partner Bank - authorization server" -Direction Inbound -Protocol TCP -LocalPort 47401 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "EWP Partner Bank - authorization server (mTLS)" -Direction Inbound -Protocol TCP -LocalPort 47411 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "EWP Partner Bank - products window" -Direction Inbound -Protocol TCP -LocalPort 47402 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "EWP Partner Bank - API (mTLS)" -Direction Inbound -Protocol TCP -LocalPort 47403 -Action Allow -Profile Private

# Each time, one window each (every port on every interface):
dotnet run --project src\PartnerBank.AuthorizationServer -- --Kestrel:Endpoints:Public:Url=https://*:47401 --Kestrel:Endpoints:Mtls:Url=https://*:47411
dotnet run --project src\PartnerBank.Products -- --Kestrel:Endpoints:Window:Url=https://*:47402 --Kestrel:Endpoints:Api:Url=https://*:47403
```

The first start of each asks Windows Firewall: allow it on **Private networks** only (never Cancel - that adds a block rule).

And on the laptop, in `C:\Windows\System32\drivers\etc\hosts` (administrator) - for the browser:

```
192.168.1.12   partner-auth.ewp.home.arpa partner-products.ewp.home.arpa partner-api.ewp.home.arpa
```

## Checking it

Both tools are single-file .NET 10 apps acting from EWP's side (EWP's client certificate, trusting only the PoC's root); they connect to the given address directly, so they need no hosts-file entry.

```powershell
dotnet run tools\HandshakeCheck\HandshakeCheck.cs -- 192.168.1.12   # the authorization server: 13 checks
dotnet run tools\JourneyCheck\JourneyCheck.cs -- 192.168.1.12       # the whole journey, as EWP AND as the customer's browser: 15 checks
```

Leave out the address for the Partner Bank on the same machine. The handshake check prints a link for a browser test; the browser needs `ewp-trust-root.cer` in "Trusted Root Certification Authorities" to open it without a warning.

Demo customers (Development only): `alex.partner` and `sam.partner` - see the authorization server's `appsettings.Development.json`. Delete `src\PartnerBank.Products\partner-products.db` to start the products' data again.

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

**Products window and API**

| # | Control | Where |
|---|---|---|
| 13 | **The API only on the mutual-TLS port, the window only on the public port** (exclusive port guard); the API port requires a client certificate in the TLS handshake | `Program.cs`, `appsettings.json` |
| 14 | **Bound tokens checked on every API call**: an access token (`at+jwt`, audience `partner-api`) is accepted only over a connection whose client certificate chains to the trust root AND matches the token's `cnf.x5t#S256` - the same token over another certificate is refused | `Security/BoundTokens.cs` |
| 15 | **Only EWP may call the API** (its client ID), each endpoint with its scope; EWP sees only the applications it referred | `Program.cs` (policies), `Api/ProductsApi.cs` |
| 16 | **A window link is for ONE customer**: created only from the customer's own token (a machine token is refused), random 256 bits, valid 2 minutes, opens once, and only for the signed-in customer it names | `Api/ProductsApi.cs`, `Pages/Start.cshtml.cs` |
| 17 | **One application per window**, within 30 minutes of opening it; anti-forgery tokens on every form | `Pages/Products.cshtml.cs` |
| 18 | **The window's session cookie** is `__Host-`, HttpOnly, Secure, SameSite=Lax, 20 minutes sliding | `Program.cs` |
| 19 | **The authorization server is trusted only through the PoC's root** (discovery, keys, token): a server with another certificate is never believed | `PartnerBank.Security/TrustedBackchannel.cs` |

**Not done in this PoC** (a production partner would): certificate revocation checks (the PoC's root publishes no revocation list), sign-in throttling and lockout, real customer accounts (demo users only), persistent grant and key stores (in memory / local files), a real credit decision, a Duende licence, and consent screens.