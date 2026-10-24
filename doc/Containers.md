# Container images

How the Partner Bank's three deployables become container images, and what an image needs from
its environment to run. Every image is listed in
[.github/container-images.json](../.github/container-images.json): `partner-bank-authorization-server`,
`partner-bank-api` and `partner-bank-web`, published as `ghcr.io/<owner>/<image>`.

In short: **CI builds every image from a commit, scans it, starts it locked down with throw-away
certificates, and publishes it only if all of that passes. An image holds no secret, no
certificate and no customer: the environment mounts the certificates and one data folder, and
supplies the one secret each needs.** The same pattern as EWP's images
([EWP doc/Containers.md](https://github.com/dineshmj/EnterpriseWebPlatform_V3/blob/main/doc/Containers.md)),
with one deliberate difference: TLS (decision 4).

## Decisions

| # | Decision | Why |
|---|---|---|
| 1 | Images are built **only in GitHub Actions** ([container-images.yml](../.github/workflows/container-images.yml)) and published to GHCR; nothing is built on a developer's machine | An image that runs somewhere must come from a known commit through the same checks (and neither machine has memory to spare for Docker) |
| 2 | One multi-stage **Dockerfile** per deployable, next to its project; build context = repository root | The three share `src/PartnerBank.Security` |
| 3 | Runtime: `mcr.microsoft.com/dotnet/aspnet:<version>-noble-chiseled-extra`, the same base as EWP's .NET images | No shell, no package manager, non-root user `app` (UID 1654); `-extra` adds ICU and time zones (Sydney time, en-AU amounts) |
| 4 | **TLS ends in Kestrel, inside the container** - unlike EWP, whose containers speak plain HTTP behind the ingress. The ingress passes the TLS connection through (SNI routing), and each port keeps its client-certificate rule (Architecture section 5) | EWP's tokens are bound to the client certificate of the connection (RFC 8705, controls 4 and 14) and the authorization server authenticates EWP by that certificate (control 1). The deployable must see the certificate in the handshake itself; an ingress that ended TLS would have to forward it in a header - one more party the bank would have to trust with EWP's identity |
| 5 | **One writable folder, `/data`** (owned by `app`; in an environment, a volume): the SQLite databases, the authorization server's signing keys, the Data Protection keys. Everything else is read-only (`--read-only` works) | A restart must keep the customers' accounts, the Partner Bank's data, and the keys that verify issued tokens and decrypt sessions |
| 6 | **Certificates are mounted** at `/etc/partner-bank/pki`, with the same file names as `%USERPROFILE%\.ewp-pki` | One convention for the image and the home PC; a missing file stops the start-up with its name (control 8) |
| 7 | Base images pinned by **tag and digest**, actions pinned to commit SHAs; **Dependabot** proposes updates weekly, all base images in one pull request ([dependabot.yml](../.github/dependabot.yml)) | Nothing changes silently, security fixes still arrive - and the bases are the same digests as EWP's, so both organisations move together |

## What CI does per image

0. **Which images** (once per run): an image is built when its project or `PartnerBank.Security`
   changed. A change to the build itself (the workflow, the manifest, the start check,
   `.dockerignore`, `Directory.Build.props`) or a manual run builds all three.
1. **hadolint**: lints the Dockerfile (warnings fail).
2. **Build** with Buildx (layer cache in GitHub Actions).
3. **Trivy**: scans the built image for vulnerabilities that have a fix and for secrets. HIGH or
   CRITICAL fails the job, and nothing is published. The report goes to the run's summary page.
4. **Start check** ([start-check.sh](../.github/scripts/start-check.sh)): a throw-away PKI (root,
   server, client and request-signing certificates, valid one day, made in the runner's temp
   folder) is mounted read-only; the container runs non-root with a read-only file system, no
   capabilities, no privilege escalation and an empty `/data`. When it has started, one request
   over TLS - the server certificate checked against the throw-away root - must answer:

   | Image | Request | Expected | What it proves |
   |---|---|---|---|
   | authorization server | `GET :47401/.well-known/openid-configuration/jwks` | 200 | TLS, the customer database created in `/data`, a signing key created, encrypted with the Data Protection key and stored in `/data` |
   | API | `GET :47403/api/products` **with** a client certificate, no token | 401 | the mutual-TLS port accepts a certificate from the trusted root, the API's database in `/data`, the policy refuses a call without a token |
   | window | `GET :47402/` | 200 | TLS, the Razor pages and static files, the Data Protection keys in `/data` |

5. On `main` only: **push** to GHCR with an **SBOM** and BuildKit's **SLSA provenance**. Tags
   are the full commit SHA (a deployment pins this one) and the branch name.

Pull requests run steps 1-4 and never publish.

## Registry retention

[ghcr-retention.yml](../.github/workflows/ghcr-retention.yml) runs every Monday (and by hand) and
keeps, per image, the newest **10** versions with a commit tag plus the one tagged `main`; older
ones are deleted with their platform and attestation manifests
([ghcr-retention.sh](../.github/scripts/ghcr-retention.sh), the same script as EWP's). A manual
run is a dry run unless `dry_run` is switched off. If listing or deleting answers 403: in each
package's settings, **Manage Actions access** -> this repository with role **Admin**.

## Keeping secrets out of images

- [.dockerignore](../.dockerignore) keeps every `appsettings.Development.json` (the window's
  development secret, the demo customers), all key material (`*.pfx`, `*.cer`, `*.password`,
  `keys/` - IdentityServer's signing keys) and the local SQLite databases out of the build
  context. A `COPY` of a whole project folder therefore cannot carry them into an image.
- `appsettings.json` holds no secret: the window's client secret is empty there, and both hosts
  refuse to start without it.
- The image runs as `Production`: no demo customers, no one-time code on the page.
- Trivy's secret scanner checks the finished image as a second line of defence.

## Run-time contract

Every image:

| Mount / setting | What |
|---|---|
| `/etc/partner-bank/pki/` (read-only) | `partner-bank-server.pfx` + `partner-bank-server.pfx.password` (the TLS certificate for the Partner's host names - in a cluster also the API's internal address, [Kubernetes.md](Kubernetes.md)) and `ewp-trust-root.cer`; the API and the authorization server also `ewp-request-signing.cer` (EWP's public request-signing certificate) |
| `/data` (writable volume, owner UID 1654 or `fsGroup`) | see below; must survive a restart |
| `PartnerBank__ResolvePartnerHostsTo` | set to empty in the image: the Partner's host names resolve by DNS (`partner-auth...`, `partner-api...`) |
| Kestrel endpoints | set in the image on every interface, same port numbers as on the home PC - except the API's internal port, which the image keeps on localhost; a deployment opens it together with a rule for who may reach it (the Helm chart: `Kestrel__Endpoints__Internal__Url` + a NetworkPolicy, [Kubernetes.md](Kubernetes.md) P4) |
| Health | `/health/live` and `/health/ready` (readiness: the SQLite database opens) over https on 47401 (authorization server), 47404 (API - the external port requires a client certificate) and 47402 (window); names and statuses only in the answer |

Per image:

| Image | Ports | In `/data` | Secrets (from a secret store) | Also set by the deployment |
|---|---|---|---|---|
| `partner-bank-authorization-server` | 47401 public, 47411 mutual TLS | `partner-identity.db*`, `signing-keys/`, `data-protection-keys/` | `PartnerBank__ProductsWindowClient__Secret` | `PartnerBank__IssuerUri`, `PartnerBank__MtlsBaseUrl`, `PartnerBank__ApiInternalBaseUrl`, the redirect addresses of both clients (`PartnerBank__EwpClient__RedirectUris__0`, `PartnerBank__ProductsWindowClient__RedirectUris__0`, `...PostLogoutRedirectUris__0`) when the host names differ from the home PC's |
| `partner-bank-api` | 47403 external (mutual TLS); 47404 internal | `partner-bank.db*` | none | `PartnerBank__Authority`, `PartnerBank__ExternalBaseUrl`, `PartnerBank__InternalBaseUrl`, `PartnerBank__WindowBaseUrl` when they differ |
| `partner-bank-web` | 47402 | `data-protection-keys/` | `PartnerBank__WindowClientSecret` (= the authorization server's value) | `PartnerBank__Authority`, `PartnerBank__ApiInternalBaseUrl` when they differ |

Replicas: **one each.** SQLite is a file, the staff-view handoffs (control 18i) and the
authorization server's grants are in memory, and the signing keys are files - two instances would
each have their own. A second instance needs the production stores of residual risk R3 first.

## Known gaps (to close at the deployment step)

| Gap | Effect | Planned fix |
|---|---|---|
| The Data Protection keys are stored unencrypted in `/data` (`DataProtectionKeys.cs`, DEBT) | Whoever can read the volume can decrypt sessions and the stored signing keys | Wrap them with a key from a key vault; until then the volume's access rights protect them |
| No Duende licence (README, "Not done") | IdentityServer logs that it runs unlicensed; allowed for development and testing only | A licence before any real use |
| The Dockerfile front end (`# syntax=docker/dockerfile:1.7`) is pinned by tag only | A re-pushed front-end tag could change how a Dockerfile is read | As in EWP: pin it by digest once Dependabot can move that pin |

## Adding an image

1. Copy the closest Dockerfile and change the project paths. Keep the `FROM` lines exactly as
   copied (tag and digest). Anything the deployable writes goes under `/data`, set by an `ENV`
   in the image.
2. Add the image to [.github/container-images.json](../.github/container-images.json) - its name,
   Dockerfile, EVERY folder it is built from, and a `check` (port, path, whether the port needs a
   client certificate, the expected status, any setting it needs to start). Add its folder to
   [dependabot.yml](../.github/dependabot.yml).