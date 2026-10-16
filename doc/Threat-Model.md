# Partner Bank - threat model

Scope: the Partner Bank's three deployables and their two boundaries - EWP's server (another organisation) and the customer's browser. The controls are numbered as in the README's "Security controls" tables.

## 1. Assets

| Asset | Why it matters |
|---|---|
| EWP's request-signing key (EWP's asset; its public part registered here) | anyone holding it could sign authorization requests as EWP |
| Customers' Partner Bank sessions and tokens | a stolen session or token acts as the customer |
| EWP's access to the Partner API | an impostor "EWP" could create window links, read applications - and, next, order gold |
| Window links | a link opens the Partner's window for one customer |
| Applications, gold orders, holdings and the activity ledger | the Partner Bank's own records of what its customers own |
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
| T13 | EWP's request is changed on the way, or EWP later denies what it asked for (e.g. the amount) | every request is a signed request object (JAR); the signature, issuer, audience, lifetime and type are checked; the Partner Bank holds signed evidence | 12a |
| T14 | A request authorises more, or something else, than intended | exactly one `authorization_details` of a known type, inside the signed request, strictly validated; the approved detail travels into the token through the stored code | 12b, 12c |
| T15 | A stolen mutual-TLS key is used to sign requests (or the reverse) | two separate keys: the TLS key authenticates connections, the ES256 key signs requests; neither can do the other's job | 12a |
| T16 | A customer approves something other than they think (vague or hidden terms), or one approval is reused for later requests | the consent page shows the SIGNED detail in plain words, who offered it and what was shared; consent is per request and never remembered; only an explicit Approve grants anything | 12d |
| T17 | EWP (or a stolen token) places more orders, or different ones, than the customer approved | an order takes everything from the token's consented detail; one order per authorisation (unique token ID); a token approved for another purpose is refused | 18a |
| T18 | A payment advice for the wrong amount, a replayed advice, or a cancel after allocation | exact amount only; idempotent by network reference; allocated gold cannot be cancelled; only the referring organisation's own orders | 18b |
| T19 | Holdings read without the customer's agreement | only with a consented `holdings_view` token, only the customer's own | 18c |
| T20 | One customer sees another's gold or activity in the Partner's window | the internal API reads the customer from the window's token (`sub`), never from the request | 16, 21a |
| T21 | At a bank's desk, the next person uses the previous customer's Partner Bank session | every EWP request requires a fresh sign-in (`prompt=login`, inside the signed request, enforced); the window accepts only a sign-in of the last 2 minutes; "Sign out" ends both sessions; 10-minute idle sessions | 10c, 21b |
| T22 | Someone registers as another person, registers twice, or guesses the one-time code | registration only from a signed request for a verified customer, identity read-only; once per referred customer; the code is 6 digits, 5 minutes, 5 tries, stored as a salted hash | 10b |
| T23 | Password guessing at the Partner Bank's sign-in | hashed passwords; account lockout after 5 wrong passwords (15 minutes) | 10a |

## 4. Residual risks (accepted for the PoC)

| # | Risk | Production answer |
|---|---|---|
| R1 | No certificate revocation: a stolen EWP key stays valid until the certificate expires (one year) | a CA with revocation (OCSP / CRL) and shorter certificate lifetimes |
| R2 | Per-account lockout is in place (10a); there is no per-address throttling | per-address throttling (as EWP's IDP does) |
| R3 | Grants and keys in memory or local files; the window's client secret in development configuration | persistent stores, keys in a secret store or HSM, `private_key_jwt` for the window too |
| R4 | The trust root's private key lives on the laptop with EWP's own keys | a real CA run by a third party |
| R5 | Demo customers with known passwords (Development seed); one-time codes shown on the page in Development instead of an SMS | no seeded accounts outside Development; an SMS / push provider; MFA at every sign-in |
| R6 | Windows 10 on the home PC no longer receives security updates | supported operating systems; the PoC's ports are open to the home network (Private profile) only |
| R7 | ~~EWP's pushed requests are not signed~~ - **closed 2026-10-16** by message signing (decision D4, controls 12a-12c) | - |
| R8 | EWP's request-signing key is in a file on the laptop | an HSM or a key-management service; signing done by the service, the key never exported |