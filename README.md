# EWP V3 - Partner Bank

A **separate organisation** for the EnterpriseWebPlatform V3 proof of concept: a partner bank whose products EWP (Four Walls Inc.) offers to its customers. It lives in its own repository and runs on its own machine (the home PC), so the integration crosses a real network, between two hosts with their own certificates.

The integration follows the FAPI 2.0 pattern:

```
EWP's server  ──mutual TLS (EWP's client certificate)──►  Partner Bank authorization server
              ── PAR: "we are EWP; this customer wants to see your products" ──►
              ◄── request_uri (one-time, valid 60 s)
Customer's browser ──opens the Partner Bank window with the request_uri──►  sign-in, then products
              ◄── back to EWP with a one-time code
EWP's server  ──mutual TLS──►  token request: an access token BOUND to EWP's certificate
              ──mutual TLS + that token──►  Partner Bank API
```

| Part | Port | Status |
|---|---|---|
| `PartnerBank.AuthorizationServer` - Duende IdentityServer 8 | 47401 | built |
| Products window (the partner's web app) | 47402 | to come |
| Partner Bank API | 47403 | to come |

Host names: `partner-auth`, `partner-products` and `partner-api` `.ewp.home.arpa` (`.home.arpa` is reserved for home networks, RFC 8375).

## Certificates

The certificates come from EWP's repository - its local certificate authority plays the neutral trust body both organisations accept:

```powershell
# On the laptop, in EnterpriseWebPlatform_V3:
powershell.exe -ExecutionPolicy Bypass -File .\ps\pki\New-EwpLocalPki.ps1
```

It writes to `%USERPROFILE%\.ewp-pki` (outside every repository). This repository needs three of those files:

| File | What |
|---|---|
| `ewp-trust-root.cer` | the root that client certificates must chain to |
| `partner-bank-server.pfx` | the Partner Bank's TLS certificate (all three host names) |
| `partner-bank-server.pfx.password` | its password |

On the home PC, copy those three into `%USERPROFILE%\.ewp-pki`. Never copy `ewp-trust-root.pfx` (the root's private key) or `ewp-partner-client.pfx` (EWP's private key) - they belong to the other organisations.

## Running

**On the laptop** (development): start `PartnerBank.AuthorizationServer` from Visual Studio, or

```powershell
dotnet run --project src\PartnerBank.AuthorizationServer
```

It listens on `https://localhost:47401` only.

**On the home PC** (192.168.1.12), to be reachable from the laptop:

```powershell
# Once, in an administrator PowerShell - only from the home network (Private profile):
New-NetFirewallRule -DisplayName "EWP Partner Bank - authorization server" -Direction Inbound -Protocol TCP -LocalPort 47401 -Action Allow -Profile Private

# Each time:
dotnet run --project src\PartnerBank.AuthorizationServer --urls https://*:47401
```

And on the laptop, one line in `C:\Windows\System32\drivers\etc\hosts` (administrator):

```
192.168.1.12   partner-auth.ewp.home.arpa
```

## Checking the handshake

From EWP's side (EWP's client certificate, trusting only the PoC's root):

```powershell
dotnet run tools\HandshakeCheck\HandshakeCheck.cs                    # the server on this machine
dotnet run tools\HandshakeCheck\HandshakeCheck.cs -- 192.168.1.12    # the server on the home PC
```

It checks discovery, PAR with and without EWP's certificate, that an authorization request that was not pushed is refused, that a pushed one leads to the sign-in, and that EWP's token is bound to its certificate. It prints a link for a browser test; the browser needs `ewp-trust-root.cer` in "Trusted Root Certification Authorities" to open it without a warning.

Demo customers (Development only): `alex.partner` and `sam.partner` - see `appsettings.Development.json`.

## Security controls

| # | Control | Where |
|---|---|---|
| 1 | **Mutual TLS client authentication (RFC 8705 `tls_client_auth`)**: EWP has no shared secret; its client certificate must chain to the PoC's trust root ONLY, be valid now, carry the client-authentication purpose, and have exactly the configured subject | `Program.cs` (certificate handler), `Configuration/Config.cs` |
| 2 | **Pushed Authorization Requests required** for every client: an authorization request that was not pushed first is refused; a pushed one is valid for 60 s and used once | `Program.cs` (`PushedAuthorization`), `Config.cs` |
| 3 | **PKCE (S256) required**, plain PKCE refused | `Config.cs` |
| 4 | **Certificate-bound access tokens** (`cnf.x5t#S256`): a stolen token is useless without EWP's private key | `Program.cs` (`MutualTls`) |
| 5 | **Short lifetimes**: authorization code 60 s, access token 5 min, no refresh tokens | `Config.cs` |
| 6 | **TLS 1.2 / 1.3 only**; a certificate is asked for but only the mTLS endpoints require it, so customers' browsers need none | `Program.cs` (Kestrel) |
| 7 | **Fixed issuer**, never taken from the request's Host header | `Program.cs` |
| 8 | **Fail closed at start-up**: no server certificate, password or trust root - no server | `Configuration/PartnerBankOptions.cs` |
| 9 | **Browser security headers**: CSP without inline code, never framed, no MIME sniffing, no referrer | `Security/SecurityHeaders.cs` |
| 10 | **No open redirect** after sign-in; one message for an unknown user and a wrong password | `Pages/Account/Login.cshtml.cs` |
| 11 | **Error details only in Development** | `Pages/Error.cshtml.cs` |

**Not done in this PoC** (a production partner would): certificate revocation checks (the PoC's root publishes no revocation list), sign-in throttling and lockout, real customer accounts (demo users only), persistent grant and key stores (in memory / local files), a Duende licence, and consent screens.