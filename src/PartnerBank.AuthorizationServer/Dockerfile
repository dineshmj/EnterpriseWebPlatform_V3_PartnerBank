# syntax=docker/dockerfile:1.7
# Build from the repository root:
#   docker build -f src/PartnerBank.AuthorizationServer/Dockerfile -t partner-bank/authorization-server .

# ┌─ :: ARCHITECTURE + SECURITY :: Partner Bank authorization server container image ──────────────
# │ Why : Whoever holds the authorization server's signing keys can mint any token for the
# │       Partner Bank API. The image is published to a registry, so no key, secret or customer
# │       account may be in it - not even the development ones.
# │ How : The .NET SDK publishes the server; a chiseled, non-root ASP.NET runtime runs it. TLS
# │       ends HERE, in Kestrel: the mutual-TLS port must see EWP's client certificate itself to
# │       authenticate EWP and bind its tokens (RFC 8705) - the ingress passes TLS through.
# │       At run time, mounted: the PKI folder at /etc/partner-bank/pki (the same file names as
# │       %USERPROFILE%\.ewp-pki) and a writable volume at /data (customer accounts, signing keys,
# │       Data Protection keys); from a secret store: PartnerBank__ProductsWindowClient__Secret.
# │       .dockerignore keeps keys/, *.db and appsettings.Development.json (demo customers) out.
# │ Ref : doc/Containers.md | doc/Architecture.md 5 | OWASP A02, A05, A08 | CIS Docker Benchmark 4.1
# └────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src

# Restore from the project files alone (layer reused until a project file changes).
COPY Directory.Build.props ./
COPY src/PartnerBank.Security/PartnerBank.Security.csproj src/PartnerBank.Security/
COPY src/PartnerBank.AuthorizationServer/PartnerBank.AuthorizationServer.csproj src/PartnerBank.AuthorizationServer/
RUN dotnet restore src/PartnerBank.AuthorizationServer/PartnerBank.AuthorizationServer.csproj --runtime linux-x64

COPY src/PartnerBank.Security/ src/PartnerBank.Security/
COPY src/PartnerBank.AuthorizationServer/ src/PartnerBank.AuthorizationServer/
RUN dotnet publish src/PartnerBank.AuthorizationServer/PartnerBank.AuthorizationServer.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /app -p:UseAppHost=false \
    && mkdir /data

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8 AS runtime
WORKDIR /app

# Program files owned by root (read-only for "app"); /data is the one writable place.
COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /data /data

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────────
# │ Where things are inside the container - no secret, no environment's address. Both ports on
# │ every interface: Public never asks for a client certificate, Mtls requires one. Signing keys
# │ and Data Protection keys in /data, so a restart keeps them (tokens stay verifiable, sessions
# │ stay valid). The Partner's host names resolve by DNS (ResolvePartnerHostsTo off).
# └──────────────────────────────────────────────────────────────────────────────────────────────────
# SEC: DL3064 reacts to the words Password / Keys in these NAMES; every value is a file path, never a secret.
# hadolint ignore=DL3064
ENV Kestrel__Endpoints__Public__Url=https://+:47401 \
    Kestrel__Endpoints__Mtls__Url=https://+:47411 \
    ConnectionStrings__PartnerIdentity="Data Source=/data/partner-identity.db" \
    PartnerBank__SigningKeysPath=/data/signing-keys \
    PartnerBank__DataProtectionKeysPath=/data/data-protection-keys \
    PartnerBank__ResolvePartnerHostsTo="" \
    PartnerBank__Tls__ServerCertificatePath=/etc/partner-bank/pki/partner-bank-server.pfx \
    PartnerBank__Tls__ServerCertificatePasswordPath=/etc/partner-bank/pki/partner-bank-server.pfx.password \
    PartnerBank__Tls__TrustRootPath=/etc/partner-bank/pki/ewp-trust-root.cer \
    PartnerBank__EwpClient__RequestSigningCertificatePath=/etc/partner-bank/pki/ewp-request-signing.cer
EXPOSE 47401 47411
# Numeric (1654), so Kubernetes runAsNonRoot can verify it.
USER $APP_UID

# No HEALTHCHECK: no shell or curl in the chiseled image; the orchestrator probes /health/live and /health/ready on 47401.
ENTRYPOINT ["dotnet", "PartnerBank.AuthorizationServer.dll"]