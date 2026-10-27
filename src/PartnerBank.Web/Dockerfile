# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/PartnerBank.Web/Dockerfile -t partner-bank/web .

# ┌─ :: ARCHITECTURE + SECURITY :: Partner Bank window container image ───────────────────────────
# │ Why : The window is what customers' browsers reach; the image must carry no secret (its
# │       client secret at the authorization server) and give an attacker inside it as little
# │       as possible.
# │ How : The .NET SDK publishes the Razor Pages app; a chiseled, non-root ASP.NET runtime runs
# │       it. TLS ends in Kestrel like the other two Partner Bank deployables (one certificate,
# │       one trust root, the same TLS defaults). At run time, mounted: the PKI folder at
# │       /etc/partner-bank/pki and a writable volume at /data (Data Protection keys); from a
# │       secret store: PartnerBank__WindowClientSecret.
# │ Ref : doc/Containers.md | doc/Architecture.md 5 | OWASP A02, A05, A08 | CIS Docker Benchmark 4.1
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/PartnerBank.Security/PartnerBank.Security.csproj src/PartnerBank.Security/
COPY src/PartnerBank.Web/PartnerBank.Web.csproj src/PartnerBank.Web/
RUN dotnet restore src/PartnerBank.Web/PartnerBank.Web.csproj --runtime linux-x64

COPY src/PartnerBank.Security/ src/PartnerBank.Security/
COPY src/PartnerBank.Web/ src/PartnerBank.Web/
RUN dotnet publish src/PartnerBank.Web/PartnerBank.Web.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false \
    && mkdir /data

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8 AS runtime
WORKDIR /app

# Program files owned by root (read-only for "app"); /data is the one writable place.
COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /data /data

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Where things are inside the container - no secret, no environment's address. The browser
# │ port on every interface (never asks for a client certificate); the session cookie's keys in
# │ /data; the Partner's host names resolve by DNS (ResolvePartnerHostsTo off).
# └──────────────────────────────────────────────────────────────────────────────────────────────────
# SEC: DL3064 reacts to the words Password / Keys in these NAMES; every value is a file path, never a secret.
# hadolint ignore=DL3064
ENV Kestrel__Endpoints__Window__Url=https://+:47402 \
    PartnerBank__DataProtectionKeysPath=/data/data-protection-keys \
    PartnerBank__ResolvePartnerHostsTo="" \
    PartnerBank__Tls__ServerCertificatePath=/etc/partner-bank/pki/partner-bank-server.pfx \
    PartnerBank__Tls__ServerCertificatePasswordPath=/etc/partner-bank/pki/partner-bank-server.pfx.password \
    PartnerBank__Tls__TrustRootPath=/etc/partner-bank/pki/ewp-trust-root.cer
EXPOSE 47402
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# No HEALTHCHECK: no shell or curl in the chiseled image; the orchestrator probes /health/live and /health/ready on 47402.
ENTRYPOINT ["dotnet", "PartnerBank.Web.dll"]