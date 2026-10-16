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

var serverCertificate = settings.Tls.LoadServerCertificate();
var trustRoot = settings.Tls.LoadTrustRoot();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ One HTTPS port for people AND for EWP's server. TLS 1.2 / 1.3 only. Kestrel ASKS every
// │ client for a certificate but does not require one: browsers (our customers) have none
// │ and still reach the sign-in pages; EWP's server presents its client certificate, which
// │ the mTLS endpoints (/connect/mtls/*) then require and check below. Kestrel accepts any
// │ certificate at the TLS layer only so that the authentication handler can give a proper
// │ OAuth error - it is never trusted there.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https =>
{
    https.ServerCertificate = serverCertificate;
    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
    https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
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
        // │ Mutual TLS (RFC 8705): the mTLS endpoints are /connect/mtls/* on the same host, and
        // │ every token issued to a client that authenticated with its certificate is BOUND to
        // │ that certificate (cnf.x5t#S256): a stolen token is useless without the private key.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.MutualTls.Enabled = true;
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
app.UseStaticFiles();
app.UseRouting();
app.UseIdentityServer();
app.UseAuthorization();
app.MapRazorPages();

app.Logger.LogInformation("Partner Bank authorization server: issuer {Issuer}, TLS certificate {Subject}, trusting client certificates from {Root}.",
    settings.IssuerUri, serverCertificate.Subject, trustRoot.Subject);

app.Run();