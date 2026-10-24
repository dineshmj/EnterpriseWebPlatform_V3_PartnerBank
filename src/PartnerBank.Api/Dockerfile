# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/PartnerBank.Api/Dockerfile -t partner-bank/api .

# ┌─ :: ARCHITECTURE + SECURITY :: Partner Bank API container image ──────────────────────────────
# │ Why : The image is what runs in every environment, so it must be the same bytes everywhere,
# │       carry no secret and give an attacker inside it as little as possible.
# │ How : Multi-stage: the SDK image restores and publishes; only the published output goes into
# │       a chiseled ASP.NET runtime (no shell, no package manager, non-root user "app" 1654).
# │       TLS ends HERE, in Kestrel - not at an ingress as in EWP: EWP's tokens are bound to the
# │       client certificate of the connection (RFC 8705), so the API itself must see that
# │       certificate in the handshake (the ingress passes the TLS connection through).
# │       At run time, mounted: the PKI folder at /etc/partner-bank/pki (the same file names as
# │       %USERPROFILE%\.ewp-pki) and a writable volume at /data (the SQLite database).
# │ Ref : doc/Containers.md | doc/Architecture.md 5 | OWASP A02, A05, A08 | CIS Docker Benchmark 4.1
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src

# ┌─ PERF ───────────────────────────────────────────────────────────────────────────────────────────
# │ Restore from the project files alone: the restore layer is reused until a project file
# │ changes, not on every code change.
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY Directory.Build.props ./
COPY src/PartnerBank.Security/PartnerBank.Security.csproj src/PartnerBank.Security/
COPY src/PartnerBank.Api/PartnerBank.Api.csproj src/PartnerBank.Api/
RUN dotnet restore src/PartnerBank.Api/PartnerBank.Api.csproj --runtime linux-x64

COPY src/PartnerBank.Security/ src/PartnerBank.Security/
COPY src/PartnerBank.Api/ src/PartnerBank.Api/
RUN dotnet publish src/PartnerBank.Api/PartnerBank.Api.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false \
    && mkdir /data

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:9e3919aff68057f3e45125c5255a71d3b5f40364e45ede883348688851b9f0df AS runtime
WORKDIR /app

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ The program files stay owned by root: the "app" user can read and run them but not change
# │ them, so the container runs with a read-only root file system. The ONE writable place is
# │ /data (owned by "app"; in an environment a mounted volume).
# └──────────────────────────────────────────────────────────────────────────────────────────────────
COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /data /data

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Where things are inside the container - no secret, no environment's address:
# │  - External (EWP's server, client certificate REQUIRED) on every interface. The internal port
# │    keeps its localhost-only address from appsettings.json: the image never opens it to the
# │    network by itself (doc/Containers.md, known gaps).
# │  - No fixed address for the Partner's host names: each deployable is its own container, so
# │    they are resolved by DNS (an empty value turns ResolvePartnerHostsTo off).
# └──────────────────────────────────────────────────────────────────────────────────────────────────
# SEC: DL3064 reacts to the words Password / Keys in these NAMES; every value is a file path, never a secret.
# hadolint ignore=DL3064
ENV Kestrel__Endpoints__External__Url=https://+:47403 \
    ConnectionStrings__PartnerBank="Data Source=/data/partner-bank.db" \
    PartnerBank__ResolvePartnerHostsTo="" \
    PartnerBank__Tls__ServerCertificatePath=/etc/partner-bank/pki/partner-bank-server.pfx \
    PartnerBank__Tls__ServerCertificatePasswordPath=/etc/partner-bank/pki/partner-bank-server.pfx.password \
    PartnerBank__Tls__TrustRootPath=/etc/partner-bank/pki/ewp-trust-root.cer \
    PartnerBank__EwpRequestSigningCertificatePath=/etc/partner-bank/pki/ewp-request-signing.cer
EXPOSE 47403
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────────────────
# │ No HEALTHCHECK: the chiseled image has no shell or curl, and the Partner Bank has no health
# │ endpoints yet (doc/Containers.md, known gaps).
# └──────────────────────────────────────────────────────────────────────────────────────────────────
ENTRYPOINT ["dotnet", "PartnerBank.Api.dll"]