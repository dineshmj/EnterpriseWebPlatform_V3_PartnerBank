using PartnerBank.AuthorizationServer.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using PartnerBank.AuthorizationServer.Configuration;
using PartnerBank.AuthorizationServer.Customers;
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

// SEC: the sign-in cookie's keys - which also encrypt the stored signing keys - survive a container restart.
builder.Services.AddPartnerDataProtection(settings.DataProtectionKeysPath, "PartnerBank.AuthorizationServer");

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The Partner Bank's customer accounts (sign-in, registration) in their own SQLite
// │ database, apart from the API's business data. Passwords: ASP.NET Core Identity's
// │ PasswordHasher (PBKDF2).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<CustomerDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("PartnerIdentity")
        ?? throw new InvalidOperationException("ConnectionStrings:PartnerIdentity is required.")));
builder.Services.AddSingleton<IPasswordHasher<PartnerCustomer>, PasswordHasher<PartnerCustomer>>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CustomerStore>();

builder.Services
    .AddIdentityServer(options =>
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The issuer is configured, never derived from the request's Host header.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.IssuerUri = settings.IssuerUri;

        // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
        // │ The automatically managed signing keys in a configured folder (the image: a mounted
        // │ volume) - a restart must not mint new keys and make every issued token unverifiable.
        // │ Not configured (local runs): IdentityServer's default "keys" folder. The files are
        // │ encrypted with the Data Protection keys above.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(settings.SigningKeysPath))
            options.KeyManagement.KeyPath = Environment.ExpandEnvironmentVariables(settings.SigningKeysPath);

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
        options.UserInteraction.LogoutUrl = "/Account/Logout";
        options.UserInteraction.ConsentUrl = "/Consent";
        options.UserInteraction.ErrorUrl = "/Error";

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ A Partner Bank session ends after a short idle time (default 10 minutes): customers
        // │ often sit at a bank's desk, on a browser that is not theirs.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.Authentication.CookieLifetime = TimeSpan.FromMinutes(settings.SessionIdleMinutes);
        options.Authentication.CookieSlidingExpiration = true;

        options.Events.RaiseErrorEvents = true;
        options.Events.RaiseFailureEvents = true;
        options.Events.RaiseSuccessEvents = true;
    })
    .AddInMemoryIdentityResources(Config.IdentityResources)
    .AddInMemoryApiScopes(Config.ApiScopes)
    .AddInMemoryApiResources(Config.ApiResources)
    .AddInMemoryClients(Config.Clients(settings.EwpClient, ewpRequestSigning, settings.ProductsWindowClient).ToList())
    .AddProfileService<CustomerProfileService>()
    .AddMutualTlsSecretValidators()
    .AddCustomAuthorizeRequestValidator<AuthorizationDetailsRequestValidator>()
    .AddCustomTokenRequestValidator<AuthorizationDetailsTokenValidator>();

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ IdentityServer's own code generator, extended (a documented override point) to keep the
// │ approved authorization detail with the authorization code.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddTransient<Duende.IdentityServer.ResponseHandling.IAuthorizeResponseGenerator, AuthorizationDetailsResponseGenerator>();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The consent page reads the public gold price from the Partner Bank API's internal port
// │ (localhost), trusting the API only through the PoC's root.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services
    .AddHttpClient("PartnerApiInternal", client => client.BaseAddress = new Uri(settings.ApiInternalBaseUrl.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => TrustedBackchannel.Create(trustRoot, settings.ResolvePartnerHostsTo));

builder.Services.AddRazorPages();

// OBS: /health/live and /health/ready (the customer database opens), on the public port only (port guard).
builder.Services.AddPartnerHealthChecks()
    .AddDbContextCheck<CustomerDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CustomerDbContext>();
    db.Database.EnsureCreated();

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ EnsureCreated never changes an EXISTING database: one from an older version is named
    // │ at start-up, with the fix. Then the demo customers (Development) become accounts.
    // └──────────────────────────────────────────────────────────────────────────────────────
    string[] required = ["Customers", "PendingRegistrations"];
    var existing = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToHashSet(StringComparer.Ordinal);
    var missing = required.Where(table => !existing.Contains(table)).ToList();
    if (missing.Count > 0)
        throw new InvalidOperationException(
            $"The Partner Bank identity database was created by an older version (missing: {string.Join(", ", missing)}). Stop the server, delete partner-identity.db* in {app.Environment.ContentRootPath}, and start again.");
    await scope.ServiceProvider.GetRequiredService<CustomerStore>().SeedAsync(settings.DemoCustomers, CancellationToken.None);
}

if (!app.Environment.IsDevelopment())
    app.UseHsts();

// Forms here (sign-in, registration, consent, sign-out) lead on to the clients' REGISTERED addresses.
app.UseSecurityHeaders(settings.EwpClient.RedirectUris
    .Concat(settings.ProductsWindowClient.RedirectUris)
    .Concat(settings.ProductsWindowClient.PostLogoutRedirectUris));

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
app.MapPartnerHealthEndpoints();

app.Logger.LogInformation("Partner Bank authorization server: issuer {Issuer}, TLS certificate {Subject}, trusting client certificates from {Root}.",
    settings.IssuerUri, serverCertificate.Subject, trustRoot.Subject);

app.Run();