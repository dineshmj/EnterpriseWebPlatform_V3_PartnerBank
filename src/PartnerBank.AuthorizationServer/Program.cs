using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Server.Kestrel.Https;

using PartnerBank.AuthorizationServer.Configuration;
using PartnerBank.AuthorizationServer.Security;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(PartnerBankOptions.SectionName).Get<PartnerBankOptions>()
    ?? throw new InvalidOperationException($"The '{PartnerBankOptions.SectionName}' section is missing.");
if (string.IsNullOrWhiteSpace(settings.IssuerUri))
    throw new InvalidOperationException("PartnerBank:IssuerUri is required.");
if (!Uri.TryCreate(settings.MtlsBaseUrl, UriKind.Absolute, out var mtlsBaseUri) || mtlsBaseUri.Scheme != Uri.UriSchemeHttps)
    throw new InvalidOperationException("PartnerBank:MtlsBaseUrl must be an https address, e.g. https://partner-auth.ewp.home.arpa:47411.");

var serverCertificate = settings.Tls.LoadServerCertificate();
var trustRoot = settings.Tls.LoadTrustRoot();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ TWO HTTPS ports (Kestrel:Endpoints in appsettings), TLS 1.2 / 1.3 only:
// │  - Public (47401): people - sign-in, authorize, discovery. NEVER asks for a client
// │    certificate, so no browser shows a certificate picker to a customer.
// │  - Mtls (47411): EWP's server only - PAR and token. REQUIRES a client certificate in the
// │    TLS handshake: without one there is no connection at all.
// │ Kestrel accepts any certificate at the TLS layer only so that the authentication handler
// │ below decides (and the client gets an OAuth error) - it is never trusted there.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https =>
{
    https.ServerCertificate = serverCertificate;
    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
    https.AllowAnyClientCertificate();
}));

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The client-certificate check: it must chain to the PoC's trust root ONLY (not to any root
// │ Windows trusts), be within its validity period, and carry the client-authentication
// │ purpose. Revocation is not checked: the PoC's root publishes no revocation list (a
// │ production trust body does - see README).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddAuthentication()
    .AddCertificate(MutualTls.Scheme, options =>
    {
        options.AllowedCertificateTypes = CertificateTypes.Chained;
        options.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
        options.CustomTrustStore.Add(trustRoot);
        options.RevocationMode = X509RevocationMode.NoCheck;
        options.ValidateCertificateUse = true;
        options.ValidateValidityPeriod = true;
    });

builder.Services
    .AddIdentityServer(options =>
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The issuer is configured, never derived from the request's Host header.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.IssuerUri = settings.IssuerUri;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Mutual TLS (RFC 8705): the mTLS endpoints live on their own address (the Mtls port),
        // │ advertised in discovery as mtls_endpoint_aliases; every token issued to a client that
        // │ authenticated with its certificate is BOUND to that certificate (cnf.x5t#S256): a
        // │ stolen token is useless without the private key.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.MutualTls.Enabled = true;
        options.MutualTls.DomainName = mtlsBaseUri.Authority;
        options.MutualTls.ClientCertificateAuthenticationScheme = MutualTls.Scheme;
        options.MutualTls.AlwaysEmitConfirmationClaim = true;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ PAR for every client (not only the one configured so): an authorization request
        // │ that was not pushed first is refused. A pushed request is valid for one minute.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.PushedAuthorization.Required = true;
        options.PushedAuthorization.Lifetime = 60;

        options.UserInteraction.LoginUrl = "/Account/Login";
        options.UserInteraction.ErrorUrl = "/Error";

        options.Events.RaiseErrorEvents = true;
        options.Events.RaiseFailureEvents = true;
        options.Events.RaiseSuccessEvents = true;
    })
    .AddInMemoryIdentityResources(Config.IdentityResources)
    .AddInMemoryApiScopes(Config.ApiScopes)
    .AddInMemoryApiResources(Config.ApiResources)
    .AddInMemoryClients(Config.Clients(settings.EwpClient).ToList())
    .AddTestUsers(Config.DemoCustomers(settings.DemoCustomers))
    .AddMutualTlsSecretValidators();

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseSecurityHeaders();
app.UseMtlsPortGuard(mtlsBaseUri.Port);
app.UseStaticFiles();
app.UseRouting();
app.UseIdentityServer();
app.UseAuthorization();
app.MapRazorPages();

app.Logger.LogInformation("Partner Bank authorization server: issuer {Issuer}, TLS certificate {Subject}, trusting client certificates from {Root}.",
    settings.IssuerUri, serverCertificate.Subject, trustRoot.Subject);

app.Run();