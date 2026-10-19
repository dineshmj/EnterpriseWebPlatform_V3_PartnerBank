using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

using PartnerBank.Api.Api;
using PartnerBank.Api.Configuration;
using PartnerBank.Api.Data;
using PartnerBank.Api.Distribution;
using PartnerBank.Api.Gold;
using PartnerBank.Api.Security;
using PartnerBank.Security;

const string EwpScheme = "EwpBearer";
const string WindowScheme = "WindowBearer";

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>()
    ?? throw new InvalidOperationException($"The '{ApiOptions.SectionName}' section is missing.");
settings.Validate();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);

var serverCertificate = settings.Tls.LoadServerCertificate();
var trustRoot = settings.Tls.LoadTrustRoot();
var externalPort = new Uri(settings.ExternalBaseUrl).Port;
var internalPort = new Uri(settings.InternalBaseUrl).Port;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ TWO HTTPS ports (Kestrel:Endpoints in appsettings):
// │  - External (47403): EWP's server - a client certificate is REQUIRED in the TLS handshake.
// │  - Internal (47404): the Partner's own window - bound to localhost ONLY, never reachable
// │    from the network (the start script never changes it).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel => kestrel.UsePartnerTls(serverCertificate));

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The Partner Bank's data lives HERE only - this is its domain microservice. The window
// │ holds no data; it reads and changes it through the internal API.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<PartnerDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("PartnerBank")
        ?? throw new InvalidOperationException("ConnectionStrings:PartnerBank is required.")));

builder.Services.AddScoped<GoldDesk>();
// EWP's signed distributor instructions: checked with its request-signing certificate (loaded once, fails at start-up if missing).
builder.Services.AddSingleton<SignedInstructions>();
// Staff-view handoffs (PAR-style request_uri, 60 s, one use): in memory, one instance.
builder.Services.AddSingleton<StaffViews>();

var backchannel = TrustedBackchannel.Create(trustRoot, settings.ResolvePartnerHostsTo);

builder.Services
    .AddAuthentication()
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ EWP's calls: an access token (typ at+jwt) from the Partner's authorization server,
    // │ for the partner-api audience, BOUND to the client certificate of the connection.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddJwtBearer(EwpScheme, options =>
    {
        options.Authority = settings.Authority;
        options.Audience = "partner-api";
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidTypes = ["at+jwt"];
        options.BackchannelHttpHandler = backchannel;
        options.Events = BoundTokens.Events(trustRoot);
    })
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The window's calls: the CUSTOMER'S access token, issued to the window's client, on
    // │ the localhost-only port. No certificate binding here (the window is a browser-facing
    // │ app on the same machine); the customer's identity comes from the token - never from
    // │ the request.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddJwtBearer(WindowScheme, options =>
    {
        options.Authority = settings.Authority;
        options.Audience = "partner-api";
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidTypes = ["at+jwt"];
        options.BackchannelHttpHandler = backchannel;
    });

builder.Services.AddAuthorizationBuilder()
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ External: only EWP (its client ID), each endpoint with its scope. Internal: only the
    // │ Partner's window (its client ID), with the customer scope and a person (sub).
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddPolicy(ExternalApi.EwpPolicy, policy => policy
        .AddAuthenticationSchemes(EwpScheme)
        .RequireAuthenticatedUser()
        .RequireClaim("client_id", settings.EwpClientId))
    .AddPolicy(ExternalApi.ProductsReadPolicy, policy => policy
        .AddAuthenticationSchemes(EwpScheme)
        .RequireClaim("scope", "partner.products.read"))
    .AddPolicy(ExternalApi.ApplicationsWritePolicy, policy => policy
        .AddAuthenticationSchemes(EwpScheme)
        .RequireClaim("scope", "partner.applications.write"))
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Distribution: EWP's OWN token (client credentials - no customer in it), with the
    // │ distribution scope. What it may do for a customer comes from the signed instruction
    // │ in the body, checked on every call - never from the token alone.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddPolicy(DistributionApi.DistributionPolicy, policy => policy
        .AddAuthenticationSchemes(EwpScheme)
        .RequireClaim("scope", "partner.distribution"))
    .AddPolicy(ExternalApi.OrdersWritePolicy, policy => policy
        .AddAuthenticationSchemes(EwpScheme)
        .RequireClaim("scope", "partner.applications.write", "partner.distribution"))
    .AddPolicy(InternalApi.WindowPolicy, policy => policy
        .AddAuthenticationSchemes(WindowScheme)
        .RequireAuthenticatedUser()
        .RequireClaim("client_id", settings.WindowClientId)
        .RequireClaim("scope", "partner.customer")
        .RequireClaim("sub"));

var app = builder.Build();

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ PoC: the SQLite database is created (with its demo products) at start-up; delete the
// │ file to start again. No migrations - the same rule as EWP's full database scripts.
// └──────────────────────────────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PartnerDbContext>();
    db.Database.EnsureCreated();

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ EnsureCreated never changes an EXISTING database: a file from an older version lacks
    // │ the newer tables. Fail at start-up with the fix, instead of on the first request.
    // └──────────────────────────────────────────────────────────────────────────────────────
    // The tables this version needs, checked in SQLite's own catalogue - no failing query, no error noise in the log.
    string[] required = ["Products", "Handoffs", "Applications", "GoldAccounts", "GoldOrders", "GoldHoldings", "Activity"];
    var existing = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToHashSet(StringComparer.Ordinal);
    var missing = required.Where(table => !existing.Contains(table)).ToList();
    var orderColumns = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('GoldOrders')").ToHashSet(StringComparer.Ordinal);
    if (existing.Contains("GoldOrders") && !orderColumns.Contains("SlipNumber"))
        missing.Add("GoldOrders.SlipNumber");
    if (missing.Count > 0)
    {
        throw new InvalidOperationException(
            $"The Partner Bank database was created by an older version (missing: {string.Join(", ", missing)}). Stop the API, delete partner-bank.db* in {app.Environment.ContentRootPath}, and start again.");
    }
}

if (!app.Environment.IsDevelopment())
    app.UseHsts();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ /api only on the external (mutual-TLS) port, /internal only on the localhost port.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UsePathPrefixPerPort(new Dictionary<int, PathString> { [externalPort] = "/api", [internalPort] = "/internal" });

app.UseAuthentication();
app.UseAuthorization();
app.MapExternalApi();
app.MapInternalApi();
app.MapGoldApi();
app.MapDistributionApi();

app.Logger.LogInformation("Partner Bank API: external {External} (mutual TLS), internal {Internal} (localhost), authorization server {Authority}.",
    settings.ExternalBaseUrl, settings.InternalBaseUrl, settings.Authority);

app.Run();