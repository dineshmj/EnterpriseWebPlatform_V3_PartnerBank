# Partner Bank - architecture

The Partner Bank is a **second organisation** in the EWP V3 proof of concept. EWP (Four Walls Inc.) offers the Partner Bank's products to EWP's customers; the Partner Bank owns those products, its own customers' accounts at the partner, and its own security. The two organisations share nothing but a trust root and the standards between them.

## 1. Organisations and trust

| | EWP (Four Walls Inc.) | Partner Bank |
|---|---|---|
| Repository | `EnterpriseWebPlatform_V3` | `EnterpriseWebPlatform_V3_PartnerBank` |
| Machine | the laptop | the home PC (192.168.1.12) |
| Proves who it is with | its client certificate (`CN=EWP BSS Partner Products Client, O=Four Walls Inc.`) | its TLS server certificate (`partner-auth`, `partner-products`, `partner-api` `.ewp.home.arpa`) |
| Holds the private key of | its client certificate, its request-signing key (and, in this PoC, the trust root) | its server certificate |
| Signs its requests with | `CN=EWP BSS Request Signing, O=Four Walls Inc.` (ECDSA P-256, ES256) - registered at the Partner Bank as a JWK | - |

Both trust **one root** (`EWP Local Trust Root (DEVELOPMENT ONLY)`), created by EWP's `ps\pki\New-EwpLocalPki.ps1`. It plays the part of a neutral trust body - in production a commercial CA or a scheme registry (for example Australia's Consumer Data Right register). Each side trusts that root **only through its own configuration**, never through the operating system's store, so a certificate from any other authority is refused even when Windows would accept it.

## 2. Deployables

```
                         ┌─────────────────────────── home PC ──────────────────────────────┐
 Customer's browser ───► │ PartnerBank.Web          :47402   window (Razor Pages, no data)    │
                         │   │ customer's token                                              │
                         │   ▼                                                               │
                         │ PartnerBank.Api          :47404   internal (localhost only)        │
 EWP's server ──mTLS───► │ PartnerBank.Api          :47403   external  ──► SQLite partner-bank.db
                         │                                                                   │
 EWP's server ──mTLS───► │ PartnerBank.AuthorizationServer :47411  PAR, token (mTLS)          │
 Customer's browser ───► │ PartnerBank.AuthorizationServer :47401  sign-in, authorize, discovery
                         └───────────────────────────────────────────────────────────────────┘
```

| Deployable | Responsibility | Data |
|---|---|---|
| `PartnerBank.AuthorizationServer` | Duende IdentityServer 8: client authentication (mutual TLS for EWP, a secret for the Partner's own window), PAR, sign-in, tokens bound to certificates | in memory (PoC) |
| `PartnerBank.Api` | the domain microservice: products, window links, applications, **Digital Gold** (price, orders, payment advice, allocation, holdings) and the customers' activity ledger | **SQLite, owned by this deployable alone** |
| `PartnerBank.Web` | the customer's window: pages only; every read and change goes through the internal API with the customer's own token | none |
| `PartnerBank.Security` | shared library: TLS defaults, client-certificate check, trusted back-channel, security headers, port guards | - |

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| D1 | **A separate repository and a separate machine** for the Partner Bank | it is another organisation: nothing may be shared but standards and the trust root; mutual TLS then crosses a real network |
| D2 | **Two ports per deployable that serves both people and EWP's server** - a public port that never asks for a client certificate, and a mutual-TLS port that requires one | no certificate picker for customers; without EWP's certificate there is not even a TLS connection to the server-to-server endpoints |
| D3 | **FAPI 2.0, the open-banking way**: EWP authenticates with mutual TLS, pushes every authorization request (PAR), uses PKCE, receives certificate-bound tokens; the **customer authenticates and consents at the Partner Bank** | this is how FAPI-based open banking works worldwide (UK, Brazil, Australia CDR): the person authenticates with the organisation that holds the relationship. Federation with EWP's identity provider (enterprise single sign-on) and pure reliance on EWP's word are outside FAPI's model |
| D4 | **Message signing** (done, 2026-10-16): EWP signs every pushed request (JAR, RFC 9101) with a separate ES256 key, registered at the Partner Bank as a JWK with its key ID, and states exactly what is authorised in `authorization_details` (RAR, RFC 9396) - `digital_gold_purchase`, `product_application`, `holdings_view`; the approved detail is carried into the access token | the Partner Bank keeps signed evidence of what EWP asked for; the amount of a purchase cannot change on the way; the API sees exactly what a token allows, not only a broad scope |
| D5 | **EWP's staff member is recorded, never authenticated, by the Partner Bank** | EWP has authenticated Sophie (its IDP, MFA); she appears as the acting person in EWP's signed request, for the Partner's audit - like the `act` claim of EWP's own token exchange |
| D6 | **The Partner's data in one domain microservice** (`PartnerBank.Api`), the window a pure front end | the same layering as EWP; the window can never read or change data the API does not allow for that customer |
| D7 | **Payment advice (remittance advice)**: after EWP's payment completes, EWP's server tells the Partner API the payment's network reference over mutual TLS | a real bank would see the money on its own statement; the advice is the common, real pattern that keeps the two organisations' systems separate |
| D8 | **SQLite created at start-up, no migrations** | PoC: delete the file to start again - the same rule as EWP's full database scripts |
| D9 | **Catalogue**: Term Deposit and Bonus Saver (simple applications) and Digital Gold (the full flow); the home loan was removed | lending is a bounded context of its own and deserves proper treatment later |
| D10 | **Consent for every request, never remembered** (2026-10-16) | open banking's rule: the customer approves at the organisation that holds the relationship, each specific request on its own - approving one purchase is not approving the next |
| D11 | **Approve the amount, lock the grams at the order** (2026-10-16): the consent page shows an indicative quote; the order placed right after the approval locks the grams for 5 minutes; a payment after the lock is allocated at the price of the moment | how digital-gold products work: the customer commits to the money, the metal follows the market; never more gold than was paid for |

## 4. The journey

**Today** (verified end to end by `tools\JourneyCheck`):

1. EWP's server pushes a signed authorization request (JAR, with `authorization_details`) over mutual TLS; the customer signs in at the Partner Bank and **consents** on a page that shows exactly what EWP asks, who offered it and what EWP shared; EWP exchanges the code for the customer's token, bound to EWP's certificate and carrying the approved `authorization_details`. A declined request returns `access_denied` and no code.
2. EWP asks the external API for a one-time window link for that customer.
3. The customer opens the window (single sign-on), chooses a product and applies; the window does this through the internal API.
4. EWP reads the application's status through the external API.

**Digital Gold - the Partner Bank's side** (verified end to end by `tools\JourneyCheck`, section 5):

1. EWP signs and pushes a `digital_gold_purchase` (AUD amount, paying account); the customer signs in and consents, seeing the amount, the masked account and an indicative quote in grams.
2. EWP places the order with the customer's token: the grams are locked for 5 minutes at the price of that moment (one order per authorisation).
3. EWP pays the Partner Bank, then sends the payment advice (network reference, exact amount): the grams are allocated; the activity ledger records every step.
4. With a consented `holdings_view`, EWP reads the customer's grams, today's value and activity.

**Next**: the customer's own window, "My Partner Bank" (holdings and activity); a first-time customer's registration (pre-filled from EWP's verified data, with a one-time code); then EWP's side - "Partner Products → Digital Gold", its Temporal workflow and the payment through EWP's Payments saga.

## 5. Ports

| Port | Deployable | Who | Client certificate | Listens on (with `-Lan`) |
|---|---|---|---|---|
| 47401 | authorization server | browsers, the Partner's window | never asked | every interface |
| 47411 | authorization server | EWP's server | **required** | every interface |
| 47402 | window | browsers | never asked | every interface |
| 47403 | API (external) | EWP's server | **required** | every interface |
| 47404 | API (internal) | the Partner's window | never asked | **localhost only** |