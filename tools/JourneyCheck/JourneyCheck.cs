// The whole partner journey, checked end to end - as EWP's server AND as the customer's browser
// (a single-file .NET 10 app):
//
//   dotnet run tools/JourneyCheck/JourneyCheck.cs                  (the Partner Bank on this machine)
//   dotnet run tools/JourneyCheck/JourneyCheck.cs -- 192.168.1.12  (the Partner Bank on the home PC)
//
// 1. EWP pushes a signed authorization request (mutual TLS), the customer signs in at the Partner
//    Bank (demo customer alex.partner) and approves exactly what EWP asks; EWP exchanges the code for
//    a token bound to its certificate. A declined request returns no code.
// 2. EWP asks the Partner API for a one-time window link for that customer.
// 3. The customer opens the window (single sign-on), chooses a product and applies.
// 4. EWP reads the application's status through the API.
//
// Needs %USERPROFILE%\.ewp-pki from EWP's ps\pki\New-EwpLocalPki.ps1. Every *.ewp.home.arpa name is
// connected to the given address directly (no hosts-file entry needed); only the PoC's root is trusted.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const string Authority = "https://partner-auth.ewp.home.arpa:47401";
const string Api = "https://partner-api.ewp.home.arpa:47403";
const string WindowOrigin = "https://partner-products.ewp.home.arpa:47402";
const string ClientId = "FourWalls.Ewp.PartnerProducts";
const string RedirectUri = "https://ewp-partner.dev.localhost:46384/partner/callback";
const string DemoUsername = "alex.partner";          // DEVELOPMENT-ONLY demo customer (appsettings.Development.json)
const string DemoPassword = "Partner-Demo-2026!";

var address = args.Length > 0 ? IPAddress.Parse(args[0]) : IPAddress.Loopback;
var pki = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ewp-pki");
X509Certificate2 Pfx(string name) => X509CertificateLoader.LoadPkcs12FromFile(
    Path.Combine(pki, $"{name}.pfx"), File.ReadAllText(Path.Combine(pki, $"{name}.pfx.password")).Trim());
var trustRoot = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(pki, "ewp-trust-root.cer"));
var ewpCertificate = Pfx("ewp-partner-client");
var otherCertificate = Pfx("partner-bank-server");   // a real certificate from the same root - but not the one EWP's token is bound to
var signingCertificate = Pfx("ewp-request-signing");  // EWP's request-signing key (JAR)

var failures = 0;
void Check(string name, bool passed, string detail = "")
{
    Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(passed ? "  PASS  " : "  FAIL  ");
    Console.ResetColor();
    Console.WriteLine(detail.Length == 0 ? name : $"{name} - {detail}");
    if (!passed) failures++;
}

HttpClient Client(X509Certificate2? clientCertificate, CookieContainer? cookies = null) => new(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = cookies is not null,
    CookieContainer = cookies ?? new CookieContainer(),
    ConnectCallback = async (context, cancellationToken) =>
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    },
    SslOptions = new SslClientAuthenticationOptions
    {
        RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
                return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustRoot);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(certificate));
        },
        LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate!
    }
});

var cookies = new CookieContainer();
using var browser = Client(null, cookies);
using var ewp = Client(ewpCertificate);
using var impostor = Client(otherCertificate);

// The browser follows redirects itself - but never into EWP's callback (it does not exist yet).
async Task<(HttpResponseMessage Response, string Url)> Navigate(HttpResponseMessage response, string url)
{
    for (var hops = 0; hops < 10 && (int)response.StatusCode is >= 300 and < 400; hops++)
    {
        var next = new Uri(new Uri(url), response.Headers.Location!).ToString();
        if (next.StartsWith(RedirectUri, StringComparison.Ordinal))
            return (response, next);
        url = next;
        response = await browser.GetAsync(url);
    }
    return (response, url);
}

static string Hidden(string html, string name) =>
    WebUtility.HtmlDecode(Regex.Match(html, $"name=[\"']{Regex.Escape(name)}[\"'][^>]*value=[\"']([^\"']*)[\"']").Groups[1].Value);

static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static byte[] Base64UrlDecode(string text)
{
    var padded = text.Replace('-', '+').Replace('_', '/');
    return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
}

static string? Text(string json, string name) =>
    JsonDocument.Parse(json).RootElement.TryGetProperty(name, out var value) ? value.ToString() : null;

Console.WriteLine($"Partner Bank at {address}");
Console.WriteLine();
Console.WriteLine("1. The customer signs in at the Partner Bank, through EWP's pushed request");

var discovery = await browser.GetStringAsync($"{Authority}/.well-known/openid-configuration");
var mtls = JsonDocument.Parse(discovery).RootElement.GetProperty("mtls_endpoint_aliases");
var parEndpoint = mtls.GetProperty("pushed_authorization_request_endpoint").GetString()!;
var tokenEndpoint = mtls.GetProperty("token_endpoint").GetString()!;

var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ What EWP asks the customer to authorise, in a request object SIGNED with EWP's key
// │ (JAR, ES256, its key ID in "kid"): an application for the 12-month term deposit, for
// │ the EWP-verified customer, offered by the acting staff member.
// └──────────────────────────────────────────────────────────────────────────────────────
var authorizationDetails = new JsonArray(new JsonObject
{
    ["type"] = "product_application",
    ["productCode"] = "PB-TD-12",
    ["customer"] = new JsonObject { ["ewpCustomerNumber"] = "CUST-100009", ["name"] = "Camilla Parker", ["dateOfBirth"] = "1988-07-14", ["kycVerified"] = true },
    ["actor"] = new JsonObject { ["lanId"] = "somit", ["branch"] = "SYD001" }
});
async Task<(HttpResponseMessage Response, string? RequestUri, string Body)> PushSignedAsync()
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var requestObject = new JsonObject
    {
        ["iss"] = ClientId, ["aud"] = Authority, ["client_id"] = ClientId, ["jti"] = Guid.NewGuid().ToString(),
        ["iat"] = now, ["nbf"] = now, ["exp"] = now + 300,
        ["response_type"] = "code", ["redirect_uri"] = RedirectUri,
        ["scope"] = "openid profile partner.products.read partner.applications.write",
        ["state"] = Base64Url(RandomNumberGenerator.GetBytes(16)),
        ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256",
        ["authorization_details"] = authorizationDetails.DeepClone()
    };
    var jwtHeader = Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["alg"] = "ES256", ["typ"] = "oauth-authz-req+jwt", ["kid"] = signingCertificate.Thumbprint }.ToJsonString()));
    var jwtBody = Base64Url(Encoding.UTF8.GetBytes(requestObject.ToJsonString()));
    var jwtSignature = Base64Url(signingCertificate.GetECDsaPrivateKey()!.SignData(Encoding.ASCII.GetBytes($"{jwtHeader}.{jwtBody}"), HashAlgorithmName.SHA256));

    var par = await ewp.PostAsync(parEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["client_id"] = ClientId,
        ["request"] = $"{jwtHeader}.{jwtBody}.{jwtSignature}"
    }));
    var parBody = await par.Content.ReadAsStringAsync();
    return (par, Text(parBody, "request_uri"), parBody);
}

var (par, requestUri, parBody) = await PushSignedAsync();
Check("EWP pushes a SIGNED request (product_application) over mutual TLS", par.StatusCode == HttpStatusCode.Created && requestUri is not null, requestUri is null ? parBody : "");

var authorizeUrl = $"{Authority}/connect/authorize?client_id={ClientId}&request_uri={Uri.EscapeDataString(requestUri!)}";
var (loginPage, loginUrl) = await Navigate(await browser.GetAsync(authorizeUrl), authorizeUrl);
var loginHtml = await loginPage.Content.ReadAsStringAsync();
Check("The Partner Bank asks the customer to sign in", loginUrl.Contains("/Account/Login") && loginHtml.Contains("Four Walls Inc. (EWP)"), loginUrl);

var signIn = await browser.PostAsync(loginUrl, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["Input.Username"] = DemoUsername,
    ["Input.Password"] = DemoPassword,
    ["Input.ReturnUrl"] = Hidden(loginHtml, "Input.ReturnUrl"),
    ["__RequestVerificationToken"] = Hidden(loginHtml, "__RequestVerificationToken")
}));
var (consentPage, consentUrl) = await Navigate(signIn, loginUrl);
var consentHtml = WebUtility.HtmlDecode(await consentPage.Content.ReadAsStringAsync());
Check("Consent: the customer sees exactly what EWP asks, who offered it, and what was shared",
    consentUrl.Contains("/Consent") && consentHtml.Contains("Apply for Partner Term Deposit, 12 months") &&
    consentHtml.Contains("staff member somit") && consentHtml.Contains("CUST-100009") && consentHtml.Contains("Camilla Parker"),
    consentUrl.Split('?')[0]);

// The customer's decision on the consent page (the form's anti-forgery token included).
async Task<string> DecideAsync(string html, string url, string decision)
{
    var answer = await browser.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["ReturnUrl"] = Hidden(html, "ReturnUrl"),
        ["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken"),
        ["decision"] = decision
    }));
    return (await Navigate(answer, url)).Url;
}

var callbackUrl = await DecideAsync(consentHtml, consentUrl, "approve");
var code = Regex.Match(callbackUrl, "[?&]code=([^&]+)").Groups[1].Value;
Check("Approved: the browser returns to EWP with a code", code.Length > 0, callbackUrl.Split('?')[0]);

var tokenResponse = await ewp.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "authorization_code",
    ["client_id"] = ClientId,
    ["code"] = WebUtility.UrlDecode(code),
    ["redirect_uri"] = RedirectUri,
    ["code_verifier"] = verifier
}));
var customerToken = Text(await tokenResponse.Content.ReadAsStringAsync(), "access_token");
Check("EWP exchanges the code over mutual TLS for the customer's token", customerToken is not null, $"HTTP {(int)tokenResponse.StatusCode}");

var tokenPayload = JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlDecode(customerToken!.Split('.')[1])))!;
var grantedDetails = tokenPayload["authorization_details"];
Check("The token states exactly what the customer authorised (authorization_details)",
    grantedDetails is not null && JsonNode.DeepEquals(grantedDetails, authorizationDetails), grantedDetails?.ToJsonString() ?? "missing");

// A second request, declined: no code - EWP learns only that the customer said no.
var (_, declinedRequestUri, _) = await PushSignedAsync();
var declinedAuthorize = $"{Authority}/connect/authorize?client_id={ClientId}&request_uri={Uri.EscapeDataString(declinedRequestUri!)}";
var (declinedConsent, declinedConsentUrl) = await Navigate(await browser.GetAsync(declinedAuthorize), declinedAuthorize);
var declinedCallback = await DecideAsync(await declinedConsent.Content.ReadAsStringAsync(), declinedConsentUrl, "decline");
Check("Declined: the browser returns to EWP with access_denied and NO code",
    declinedCallback.Contains("error=access_denied") && !Regex.IsMatch(declinedCallback, "[?&]code="), declinedCallback.Split('?').Last());

Console.WriteLine();
Console.WriteLine("2. The Partner API: only EWP's certificate, only a token bound to it");

HttpRequestMessage ApiRequest(HttpMethod method, string path, string? token, JsonObject? body = null)
{
    var request = new HttpRequestMessage(method, $"{Api}{path}");
    if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    return request;
}

var products = await ewp.SendAsync(ApiRequest(HttpMethod.Get, "/api/products", customerToken));
var productsJson = await products.Content.ReadAsStringAsync();
Check("Products, with the customer's token over EWP's connection", products.IsSuccessStatusCode && productsJson.Contains("PB-TD-12"), $"HTTP {(int)products.StatusCode}");

var noToken = await ewp.SendAsync(ApiRequest(HttpMethod.Get, "/api/products", null));
Check("No token: refused", noToken.StatusCode == HttpStatusCode.Unauthorized, $"HTTP {(int)noToken.StatusCode}");

var otherConnection = await impostor.SendAsync(ApiRequest(HttpMethod.Get, "/api/products", customerToken));
Check("The same token over a connection with ANOTHER certificate: refused", otherConnection.StatusCode == HttpStatusCode.Unauthorized, $"HTTP {(int)otherConnection.StatusCode}");

var publicPort = await browser.GetAsync($"{WindowOrigin}/api/products");
Check("The API is not served on the window's public port", publicPort.StatusCode == HttpStatusCode.NotFound, $"HTTP {(int)publicPort.StatusCode}");

// The window's internal API: localhost only (unreachable from another machine) and only for the
// window's own client - EWP's customer token must not work there either.
var internalRequest = new HttpRequestMessage(HttpMethod.Get, "https://partner-api.ewp.home.arpa:47404/internal/products");
internalRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
string internalResult;
try { internalResult = $"HTTP {(int)(await browser.SendAsync(internalRequest)).StatusCode}"; }
catch (HttpRequestException) { internalResult = "not reachable"; }
Check("EWP's token on the window's internal API: refused (or not reachable)", internalResult is "HTTP 403" or "not reachable", internalResult);

var machineTokenResponse = await ewp.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials",
    ["client_id"] = ClientId,
    ["scope"] = "partner.products.read partner.applications.write"
}));
var machineToken = Text(await machineTokenResponse.Content.ReadAsStringAsync(), "access_token");
var ewpReference = $"EWP-CHECK-{DateTime.UtcNow:HHmmss}";
var machineHandoff = await ewp.SendAsync(ApiRequest(HttpMethod.Post, "/api/handoffs", machineToken, new JsonObject { ["ewpReference"] = ewpReference }));
Check("A window link with a MACHINE token (no customer): refused", machineHandoff.StatusCode == HttpStatusCode.Forbidden, $"HTTP {(int)machineHandoff.StatusCode}");

var handoff = await ewp.SendAsync(ApiRequest(HttpMethod.Post, "/api/handoffs", customerToken, new JsonObject { ["ewpReference"] = ewpReference }));
var handoffUrl = Text(await handoff.Content.ReadAsStringAsync(), "handoffUrl");
Check("A one-time window link for the customer", handoff.StatusCode == HttpStatusCode.Created && handoffUrl is not null, handoffUrl ?? $"HTTP {(int)handoff.StatusCode}");

Console.WriteLine();
Console.WriteLine("3. The customer opens the Partner Bank window and applies");

var (formPost, formPostUrl) = await Navigate(await browser.GetAsync(handoffUrl!), handoffUrl!);
var formHtml = await formPost.Content.ReadAsStringAsync();
var action = WebUtility.HtmlDecode(Regex.Match(formHtml, "<form[^>]*action=[\"']([^\"']+)[\"']").Groups[1].Value);
var fields = Regex.Matches(formHtml, "<input[^>]*name=[\"']([^\"']+)[\"'][^>]*value=[\"']([^\"']*)[\"']")
    .ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
Check("Single sign-on: the window signs the customer in without a password", action.EndsWith("/signin-oidc") && fields.ContainsKey("code"), formPostUrl.Split('?')[0]);

var (productsPage, productsUrl) = await Navigate(await browser.PostAsync(action, new FormUrlEncodedContent(fields)), action);
var productsHtml = await productsPage.Content.ReadAsStringAsync();
Check("The window shows the Partner Bank's products", productsUrl.Contains("/Products") && productsHtml.Contains("Partner Term Deposit"), productsUrl.Split('?')[0]);

var apply = await browser.PostAsync(productsUrl, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["handoff"] = Hidden(productsHtml, "handoff"),
    ["productCode"] = "PB-TD-12",
    ["__RequestVerificationToken"] = Hidden(productsHtml, "__RequestVerificationToken")
}));
var (applied, appliedUrl) = await Navigate(apply, productsUrl);
var appliedHtml = await applied.Content.ReadAsStringAsync();
var applicationNumber = Regex.Match(appliedHtml, "PB-\\d{6}-[2-9A-HJ-NP-Z]{6}").Value;
Check("The customer applies and gets an application number", applicationNumber.Length > 0, applicationNumber);

var (reused, _) = await Navigate(await browser.GetAsync(handoffUrl!), handoffUrl!);
Check("The same link cannot be used again", (await reused.Content.ReadAsStringAsync()).Contains("This link cannot be used"));

Console.WriteLine();
Console.WriteLine("4. EWP follows the application");

var status = await ewp.SendAsync(ApiRequest(HttpMethod.Get, $"/api/applications?ewpReference={ewpReference}", machineToken));
var statusJson = await status.Content.ReadAsStringAsync();
Check("EWP sees the application under its reference", status.IsSuccessStatusCode && statusJson.Contains(applicationNumber) && statusJson.Contains("SUBMITTED"), statusJson);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;