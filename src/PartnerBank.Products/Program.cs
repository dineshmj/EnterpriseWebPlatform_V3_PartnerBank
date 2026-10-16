using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;

using PartnerBank.Products.Api;
using PartnerBank.Products.Configuration;
using PartnerBank.Products.Data;
using PartnerBank.Products.Security;
using PartnerBank.Security;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(ProductsOptions.SectionName).Get<ProductsOptions>()
    ?? throw new InvalidOperationException($"The '{ProductsOptions.SectionName}' section is missing.");
settings.Validate();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);

var serverCertificate = settings.Tls.LoadServerCertificate();
var trustRoot = settings.Tls.LoadTrustRoot();
var apiPort = new Uri(settings.ApiBaseUrl).Port;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ TWO HTTPS ports (Kestrel:Endpoints in appsettings):
// │  - Window (47402): the customer's browser - never asks for a client certificate.
// │  - Api (47403): EWP's server - a client certificate is REQUIRED in the TLS handshake.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel => kestrel.UsePartnerTls(serverCertificate));

builder.Services.AddDbContext<ProductsDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Products")
        ?? throw new InvalidOperationException("ConnectionStrings:Products is required.")));

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The window's own session: a __Host- cookie (this host only, path /), HttpOnly,
    // │ Secure, SameSite=Lax, 20 minutes sliding.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-PartnerProducts";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
        options.SlidingExpiration = true;
    })
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The window signs the customer in at the Partner's authorization server: code + PKCE,
    // │ the request PUSHED first (PAR - required by the server for every client), the server
    // │ trusted only through the PoC's root. The customer just signed in there, so no second
    // │ password (single sign-on).
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddOpenIdConnect(options =>
    {
        options.Authority = settings.Authority;
        options.ClientId = settings.WindowClientId;
        options.ClientSecret = settings.WindowClientSecret;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Require;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.SaveTokens = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.BackchannelHttpHandler = TrustedBackchannel.Create(trustRoot, settings.ResolvePartnerHostsTo);
    })
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The API accepts only access tokens (typ at+jwt) from the Partner's authorization
    // │ server, for the partner-api audience, BOUND to the certificate of the connection.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddJwtBearer(options =>
    {
        options.Authority = settings.Authority;
        options.Audience = "partner-api";
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidTypes = ["at+jwt"];
        options.BackchannelHttpHandler = TrustedBackchannel.Create(trustRoot, settings.ResolvePartnerHostsTo);
        options.Events = BoundTokens.Events(trustRoot);
    });

builder.Services.AddAuthorizationBuilder()
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Only EWP (its client ID) may call the API, and each endpoint needs its scope.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddPolicy(ProductsApi.EwpPolicy, policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireClaim("client_id", settings.EwpClientId))
    .AddPolicy(ProductsApi.ProductsReadPolicy, policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireClaim("scope", "partner.products.read"))
    .AddPolicy(ProductsApi.ApplicationsWritePolicy, policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireClaim("scope", "partner.applications.write"));

builder.Services.AddRazorPages();

var app = builder.Build();

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ PoC: the SQLite database is created (with its demo products) at start-up; delete the
// │ file to start again. No migrations - the same rule as EWP's full database scripts.
// └──────────────────────────────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<ProductsDbContext>().Database.EnsureCreated();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseSecurityHeaders();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ /api only on the mutual-TLS port, the window only on the public port (exclusive).
// └──────────────────────────────────────────────────────────────────────────────────────
app.UsePortGuard(apiPort, path => path.StartsWithSegments("/api"), exclusive: true);

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapProductsApi();

app.Logger.LogInformation("Partner Bank products: window {Window}, API {Api} (mutual TLS), authorization server {Authority}.",
    settings.WindowBaseUrl, settings.ApiBaseUrl, settings.Authority);

app.Run();