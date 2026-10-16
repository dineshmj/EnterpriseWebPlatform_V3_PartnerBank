# Partner Bank - threat model

Scope: the Partner Bank's three deployables and their two boundaries - EWP's server (another organisation) and the customer's browser. The controls are numbered as in the README's "Security controls" tables.

## 1. Assets

| Asset | Why it matters |
|---|---|
| Customers' Partner Bank sessions and tokens | a stolen session or token acts as the customer |
| EWP's access to the Partner API | an impostor "EWP" could create window links, read applications - and, next, order gold |
| Window links | a link opens the Partner's window for one customer |
| Applications, and next gold orders, holdings and the activity ledger | the Partner Bank's own records of what its customers own |
| The Partner Bank's TLS private key | anyone holding it can pose as the Partner Bank |

## 2. Trust boundaries

| Boundary | Crossed by | Protected by |
|---|---|---|
| **TB1** EWP's server → Partner Bank (another organisation, over the network) | PAR, token, external API | mutual TLS on dedicated ports (6, 13), `tls_client_auth` with the PoC's root only (1), certificate-bound tokens checked on every call (4, 14), only EWP's client ID (15) |
| **TB2** Customer's browser → Partner Bank (the network) | sign-in, authorize, window | public ports that never ask for a certificate (6), PAR-only authorization (2), PKCE (3), `__Host-` cookies (22), CSP and security headers (9), anti-forgery tokens (22) |
| **TB3** Window → API (same machine) | internal API | localhost-only port (13), the customer's own token, only the window's client ID and the customer scope (16) |

## 3. Threats and mitigations

| # | Threat | Mitigation | Controls |
|---|---|---|---|
| T1 | Someone poses as EWP | without EWP's private key there is no TLS connection to the mTLS ports; the certificate must chain to the PoC's root only and carry the exact subject | 1, 6 |
| T2 | A token leaks (logs, a proxy, another system) | tokens are bound to EWP's certificate; the API refuses the same token over any other certificate; 5-minute lifetime, no refresh tokens | 4, 5, 14 |
| T3 | The authorization request is read or changed in the browser | it is pushed server to server first; the browser carries only a one-time `request_uri`, valid 60 s; a request that was not pushed is refused | 2 |
| T4 | An authorization code is intercepted | PKCE S256; the code is valid 60 s and only EWP's server, over mutual TLS, can exchange it | 3, 5 |
| T5 | EWP opens the window for another customer | a window link is created only from the customer's own token; the API checks the signed-in subject when it opens | 17 |
| T6 | A window link is reused or forwarded | random 256 bits, 2 minutes, opens once, only for the customer it names; one application per link | 17 |
| T7 | EWP (or anyone with EWP's token) uses the window's internal API | localhost only; only the window's client ID with the customer scope - EWP's token is refused | 13, 16 |
| T8 | One organisation reads another's applications | EWP sees only the applications it referred (its client ID) | 15 |
| T9 | Clickjacking, cross-site scripting, cross-site request forgery on the window | never framed, CSP without inline code, `__Host-` SameSite=Lax cookies, anti-forgery tokens | 9, 22 |
| T10 | A fake authorization server (DNS or network tricks on the PC) | the API and the window trust the authorization server only through the PoC's root and the host name | 19 |
| T11 | Username enumeration at sign-in | one message for an unknown user and a wrong password | 10 |
| T12 | The mTLS ports expose pages meant for people | the mTLS ports serve only their server-to-server paths; everything else answers 404 | 6a, 13 |

## 4. Residual risks (accepted for the PoC)

| # | Risk | Production answer |
|---|---|---|
| R1 | No certificate revocation: a stolen EWP key stays valid until the certificate expires (one year) | a CA with revocation (OCSP / CRL) and shorter certificate lifetimes |
| R2 | No sign-in throttling or lockout at the Partner's sign-in | per-account lockout and per-address throttling (as EWP's IDP does) |
| R3 | Grants and keys in memory or local files; the window's client secret in development configuration | persistent stores, keys in a secret store or HSM, `private_key_jwt` for the window too |
| R4 | The trust root's private key lives on the laptop with EWP's own keys | a real CA run by a third party |
| R5 | Demo customers with known passwords | real customer accounts with MFA |
| R6 | Windows 10 on the home PC no longer receives security updates | supported operating systems; the PoC's ports are open to the home network (Private profile) only |
| R7 | EWP's pushed requests are not yet signed: the Partner has no signed evidence of what EWP asked for | message signing (JAR + `authorization_details`) - the next step (decision D4) |