using PartnerBank.AuthorizationServer.Authorization;
using PartnerBank.AuthorizationServer.Configuration;
using PartnerBank.Security;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(PartnerBankOptions.SectionName).Get<PartnerBankOptions>()
    ?? throw new InvalidOperationException($"The '{PartnerBankOptions.SectionName}' section is missing.");
if (string.IsNullOrWhiteSpace(settings.IssuerUri))
    throw new InvalidOperationException("PartnerBank:IssuerUri is required.");
if (!Uri.TryCreate(settings.MtlsBaseUrl, UriKind.Absolute, out var mtlsBaseUri) || mtlsBaseUri.Scheme != Uri.UriSchemeHttps)
    throw new InvalidOperationException("PartnerBank:MtlsBaseUrl must be an https address, e.g. https://partner-auth.ewp.home.arpa:47411.");

var serverCertificate = settings.Tls.LoadServerCertificate();
var trustRoot = settings.Tls.LoadTrustRoot();
var ewpRequestSigning = settings.EwpClient.LoadRequestSigningCertificate();
builder.Services.AddSingleton(settings);

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ TWO HTTPS ports (Kestrel:Endpoints in appsettings):
// │  - Public (47401): people and the Partner's own products window - sign-in, authorize,
// │    discovery, and PAR / token for clients with a secret. NEVER asks for a client
// │    certificate, so no browser shows a certificate picker to a customer.
// │  - Mtls (47411): EWP's server - PAR and token. REQUIRES a client certificate in the TLS
// │    handshake: without one there is no connection at all.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel => kestrel.UsePartnerTls(serverCertificate));
builder.Services.AddAuthentication().AddPartnerClientCertificates(trustRoot);

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
        options.MutualTls.ClientCertificateAuthenticationScheme = ClientCertificates.Scheme;
        options.MutualTls.AlwaysEmitConfirmationClaim = true;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ PAR for every client (not only the one configured so): an authorization request
        // │ that was not pushed first is refused. A pushed request is valid for one minute.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.PushedAuthorization.Required = true;
        options.PushedAuthorization.Lifetime = 60;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Strict JAR (RFC 9101): a request object must be typed oauth-authz-req+jwt, so a
        // │ signed token of another kind can never be replayed as an authorization request.
        // │ Discovery advertises the authorization_details types this server understands.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.StrictJarValidation = true;
        options.Discovery.CustomEntries["authorization_details_types_supported"] = AuthorizationDetails.SupportedTypes.ToArray();

        options.UserInteraction.LoginUrl = "/Account/Login";
        options.UserInteraction.ErrorUrl = "/Error";

        options.Events.RaiseErrorEvents = true;
        options.Events.RaiseFailureEvents = true;
        options.Events.RaiseSuccessEvents = true;
    })
    .AddInMemoryIdentityResources(Config.IdentityResources)
    .AddInMemoryApiScopes(Config.ApiScopes)
    .AddInMemoryApiResources(Config.ApiResources)
    .AddInMemoryClients(Config.Clients(settings.EwpClient, ewpRequestSigning, settings.ProductsWindowClient).ToList())
    .AddTestUsers(Config.DemoCustomers(settings.DemoCustomers))
    .AddMutualTlsSecretValidators()
    .AddCustomAuthorizeRequestValidator<AuthorizationDetailsRequestValidator>()
    .AddCustomTokenRequestValidator<AuthorizationDetailsTokenValidator>();

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ IdentityServer's own code generator, extended (a documented override point) to keep the
// │ approved authorization detail with the authorization code.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddTransient<Duende.IdentityServer.ResponseHandling.IAuthorizeResponseGenerator, AuthorizationDetailsResponseGenerator>();

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseSecurityHeaders();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The mTLS port serves only PAR, token, revocation and introspection; pages for people
// │ answer 404 there. Not exclusive: the public port keeps PAR and token for the Partner's
// │ own products window (a client with a secret).
// └──────────────────────────────────────────────────────────────────────────────────────
string[] serverToServer = ["/connect/par", "/connect/token", "/connect/revocation", "/connect/introspect"];
app.UsePortGuard(mtlsBaseUri.Port, path => serverToServer.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)), exclusive: false);

app.UseStaticFiles();
app.UseRouting();
app.UseIdentityServer();
app.UseAuthorization();
app.MapRazorPages();

app.Logger.LogInformation("Partner Bank authorization server: issuer {Issuer}, TLS certificate {Subject}, trusting client certificates from {Root}.",
    settings.IssuerUri, serverCertificate.Subject, trustRoot.Subject);

app.Run();