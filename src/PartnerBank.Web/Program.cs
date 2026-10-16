using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

using PartnerBank.Security;
using PartnerBank.Web.Configuration;
using PartnerBank.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(WebOptions.SectionName).Get<WebOptions>()
    ?? throw new InvalidOperationException($"The '{WebOptions.SectionName}' section is missing.");
settings.Validate();
builder.Services.AddSingleton(settings);

var serverCertificate = settings.Tls.LoadServerCertificate();
var trustRoot = settings.Tls.LoadTrustRoot();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ One HTTPS port for the customer's browser (Kestrel:Endpoints:Window) - it never asks for
// │ a client certificate.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel => kestrel.UsePartnerTls(serverCertificate));

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The window's own session: a __Host- cookie (this host only, path /), HttpOnly,
    // │ Secure, SameSite=Lax, 10 minutes sliding. It also holds the customer's access token
    // │ (encrypted, server-issued cookie) - the browser's script can never read it.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-PartnerProducts";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        options.SlidingExpiration = true;
    })
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The window signs the customer in at the Partner's authorization server: code + PKCE,
    // │ the request PUSHED first (PAR - required by the server for every client), the server
    // │ trusted only through the PoC's root. It asks for the customer scope, so the API can
    // │ act for this customer and nobody else.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddOpenIdConnect(options =>
    {
        options.Authority = settings.Authority;
        options.ClientId = settings.WindowClientId;
        options.ClientSecret = settings.WindowClientSecret;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Require;
        options.Scope.Add("partner.customer");
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.SaveTokens = true;
        options.TokenValidationParameters.NameClaimType = "name";
        options.BackchannelHttpHandler = TrustedBackchannel.Create(trustRoot, settings.ResolvePartnerHostsTo);

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ max_age: a Partner Bank sign-in counts only if it is at most 2 minutes old. Right
        // │ after the customer approved a request from their bank, the window opens without a
        // │ second password; later - perhaps another person at the same desk - they must sign in
        // │ again. After sign-out the window asks for a sign-in again (its "My Partner Bank").
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.MaxAge = TimeSpan.FromMinutes(2);
        options.SignedOutRedirectUri = "/My";
    });

builder.Services.AddHttpContextAccessor();
builder.Services
    .AddHttpClient<PartnerApi>(PartnerApi.HttpClientName, client => client.BaseAddress = new Uri(settings.ApiInternalBaseUrl.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => TrustedBackchannel.Create(trustRoot, settings.ResolvePartnerHostsTo));

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// "Sign out" leads on to the authorization server (OpenID Connect end-session).
app.UseSecurityHeaders([settings.Authority]);
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Logger.LogInformation("Partner Bank window: API (internal) {Api}, authorization server {Authority}.", settings.ApiInternalBaseUrl, settings.Authority);

app.Run();