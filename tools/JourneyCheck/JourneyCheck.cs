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
// 5. Digital Gold: the customer approves AUD 10.00; EWP places the order (grams locked), sends the
//    payment advice; the grams are allocated; the customer's holdings show them - to EWP (with the
//    customer's approval) and in the customer's own window, "My Partner Bank".
// 6. A shared desk: every request from EWP asks for a fresh sign-in; signing out of the window ends
//    the Partner Bank session too - the next person sees nothing.
// 7. A first-time customer (a new EWP customer, a fresh browser) registers with the details EWP
//    verified, confirms with a one-time code, consents; next time: "Welcome back"; never twice.
// 8. EWP as the Partner Bank's distributor (assisted channel: the customer signs paper forms at
//    EWP's counter, never signs in here): EWP's own token, SIGNED instructions - Form VII opens a
//    Digital Gold account, a transfer authorization slip buys gold; refusals for unsigned, altered,
//    expired, other-key, unknown-field, reused-slip, wrong-mobile and old-slip instructions.
// 9. EWP's staff member views the account at the Partner Bank: a signed staff view pushed over mutual
//    TLS returns a one-time request_uri; the browser opens the read-only page once; replayed, other-
//    customer, asking-for-more and made-up requests are refused.
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
var current = browser;   // the browser the helpers act in - section 7 switches to a fresh one

// The browser follows redirects itself - but never into EWP's callback (it does not exist yet).
async Task<(HttpResponseMessage Response, string Url)> Navigate(HttpResponseMessage response, string url)
{
    for (var hops = 0; hops < 10 && (int)response.StatusCode is >= 300 and < 400; hops++)
    {
        var next = new Uri(new Uri(url), response.Headers.Location!).ToString();
        if (next.StartsWith(RedirectUri, StringComparison.Ordinal))
            return (response, next);
        url = next;
        response = await current.GetAsync(url);
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
async Task<(HttpResponseMessage Response, string? RequestUri, string Body)> PushSignedAsync(JsonArray details, string codeVerifier)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var requestObject = new JsonObject
    {
        ["iss"] = ClientId, ["aud"] = Authority, ["client_id"] = ClientId, ["jti"] = Guid.NewGuid().ToString(),
        ["iat"] = now, ["nbf"] = now, ["exp"] = now + 300,
        ["response_type"] = "code", ["redirect_uri"] = RedirectUri, ["prompt"] = "login",
        ["scope"] = "openid profile partner.products.read partner.applications.write",
        ["state"] = Base64Url(RandomNumberGenerator.GetBytes(16)),
        ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))), ["code_challenge_method"] = "S256",
        ["authorization_details"] = details.DeepClone()
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

var (par, requestUri, parBody) = await PushSignedAsync(authorizationDetails, verifier);
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
    var answer = await current.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["ReturnUrl"] = Hidden(html, "ReturnUrl"),
        ["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken"),
        ["decision"] = decision
    }));
    return (await Navigate(answer, url)).Url;
}

// Every request from EWP asks for a fresh sign-in (prompt=login): sign in when asked, then go on.
async Task<(HttpResponseMessage Response, string Url)> SignInIfAskedAsync(HttpResponseMessage response, string url, string username = DemoUsername, string password = DemoPassword)
{
    var (page, pageUrl) = await Navigate(response, url);
    if (!pageUrl.Contains("/Account/Login"))
        return (page, pageUrl);
    var html = await page.Content.ReadAsStringAsync();
    var answer = await current.PostAsync(pageUrl, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["Input.Username"] = username,
        ["Input.Password"] = password,
        ["Input.ReturnUrl"] = Hidden(html, "Input.ReturnUrl"),
        ["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken")
    }));
    return await Navigate(answer, pageUrl);
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
var (_, declinedRequestUri, _) = await PushSignedAsync(authorizationDetails, verifier);
var declinedAuthorize = $"{Authority}/connect/authorize?client_id={ClientId}&request_uri={Uri.EscapeDataString(declinedRequestUri!)}";
var (firstStop, firstStopUrl) = await Navigate(await browser.GetAsync(declinedAuthorize), declinedAuthorize);
Check("Even with a Partner Bank session, a new EWP request asks for a fresh sign-in (prompt=login)", firstStopUrl.Contains("/Account/Login"), firstStopUrl.Split('?')[0]);
var (declinedConsent, declinedConsentUrl) = await SignInIfAskedAsync(firstStop, firstStopUrl);
var declinedCallback = await DecideAsync(await declinedConsent.Content.ReadAsStringAsync(), declinedConsentUrl, "decline");
Check("Declined: the browser returns to EWP with access_denied and NO code",
    declinedCallback.Contains("error=access_denied") && !Regex.IsMatch(declinedCallback, "[?&]code="), declinedCallback.Split('?').Last());

// Another approval by the same (signed-in) customer: push, consent (approve), exchange the code.
async Task<(string? Token, string ConsentHtml)> ApprovedTokenAsync(JsonArray details)
{
    var codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(32));
    var (_, pushed, _) = await PushSignedAsync(details, codeVerifier);
    var url = $"{Authority}/connect/authorize?client_id={ClientId}&request_uri={Uri.EscapeDataString(pushed!)}";
    var (page, pageUrl) = await SignInIfAskedAsync(await current.GetAsync(url), url);
    var html = await page.Content.ReadAsStringAsync();
    var approved = Regex.Match(await DecideAsync(html, pageUrl, "approve"), "[?&]code=([^&]+)").Groups[1].Value;
    var exchange = await ewp.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["code"] = WebUtility.UrlDecode(approved),
        ["redirect_uri"] = RedirectUri, ["code_verifier"] = codeVerifier
    }));
    return (Text(await exchange.Content.ReadAsStringAsync(), "access_token"), WebUtility.HtmlDecode(html));
}

JsonArray Detail(string type, JsonObject extra)
{
    var detail = new JsonObject
    {
        ["type"] = type,
        ["customer"] = new JsonObject { ["ewpCustomerNumber"] = "CUST-100009", ["name"] = "Camilla Parker", ["dateOfBirth"] = "1988-07-14", ["kycVerified"] = true },
        ["actor"] = new JsonObject { ["lanId"] = "somit", ["branch"] = "SYD001" }
    };
    foreach (var (name, value) in extra) detail[name] = value?.DeepClone();
    return new JsonArray(detail);
}

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
Console.WriteLine("5. Digital Gold: approved, ordered, paid, allocated");

var purchase = Detail("digital_gold_purchase", new JsonObject
{
    ["instructedAmount"] = new JsonObject { ["currency"] = "AUD", ["amount"] = "10.00" },
    ["debtorAccount"] = new JsonObject { ["bsb"] = "062-000", ["accountNumber"] = "72947086" }
});
var (goldToken, goldConsent) = await ApprovedTokenAsync(purchase);
Check("Consent shows the purchase, the masked paying account and indicative grams",
    goldConsent.Contains("Buy Digital Gold for AUD 10.00") && goldConsent.Contains("062-000 ••••7086") && goldConsent.Contains(" g at today's AUD"),
    Regex.Match(goldConsent, "about [0-9.]+ g at today's AUD [0-9.,]+/g").Value);
Check("The customer's purchase token is issued", goldToken is not null);

var placed = await ewp.SendAsync(ApiRequest(HttpMethod.Post, "/api/gold-orders", goldToken));
var placedJson = await placed.Content.ReadAsStringAsync();
var orderNumber = Text(placedJson, "number");
var orderGrams = decimal.Parse(Text(placedJson, "grams") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
Check("EWP places the order from the APPROVED purchase - grams locked, awaiting payment",
    placed.StatusCode == HttpStatusCode.Created && Text(placedJson, "status") == "AWAITING_PAYMENT" && orderGrams > 0,
    $"{orderNumber}: {orderGrams} g at AUD {Text(placedJson, "pricePerGram")}/g, locked until {Text(placedJson, "quoteExpiresAt")}");

var replaced = await ewp.SendAsync(ApiRequest(HttpMethod.Post, "/api/gold-orders", goldToken));
Check("The same authorisation cannot place a second order", replaced.StatusCode == HttpStatusCode.OK && Text(await replaced.Content.ReadAsStringAsync(), "number") == orderNumber,
    $"HTTP {(int)replaced.StatusCode}");

var otherPurpose = await ewp.SendAsync(ApiRequest(HttpMethod.Post, "/api/gold-orders", customerToken));
Check("A token approved for something else (a term deposit application) cannot buy gold", otherPurpose.StatusCode == HttpStatusCode.Forbidden, $"HTTP {(int)otherPurpose.StatusCode}");

var wrongAmount = await ewp.SendAsync(ApiRequest(HttpMethod.Post, $"/api/gold-orders/{orderNumber}/payment-advice", machineToken,
    new JsonObject { ["networkReference"] = "NPP-CHECK-0001", ["amount"] = 9.99m }));
Check("A payment advice for the WRONG amount is refused", wrongAmount.StatusCode == HttpStatusCode.Conflict, $"HTTP {(int)wrongAmount.StatusCode}");

var advice = await ewp.SendAsync(ApiRequest(HttpMethod.Post, $"/api/gold-orders/{orderNumber}/payment-advice", machineToken,
    new JsonObject { ["networkReference"] = "NPP-CHECK-0001", ["amount"] = 10.00m }));
var adviceJson = await advice.Content.ReadAsStringAsync();
Check("The payment advice allocates the grams", advice.IsSuccessStatusCode && Text(adviceJson, "status") == "ALLOCATED", Text(adviceJson, "status") ?? $"HTTP {(int)advice.StatusCode}");

var again = await ewp.SendAsync(ApiRequest(HttpMethod.Post, $"/api/gold-orders/{orderNumber}/payment-advice", machineToken,
    new JsonObject { ["networkReference"] = "NPP-CHECK-0001", ["amount"] = 10.00m }));
Check("The same payment advice again changes nothing", again.IsSuccessStatusCode && Text(await again.Content.ReadAsStringAsync(), "status") == "ALLOCATED", $"HTTP {(int)again.StatusCode}");

var cancelAllocated = await ewp.SendAsync(ApiRequest(HttpMethod.Post, $"/api/gold-orders/{orderNumber}/cancel", machineToken,
    new JsonObject { ["reason"] = "Check: too late to cancel." }));
Check("Allocated gold cannot be cancelled", cancelAllocated.StatusCode == HttpStatusCode.Conflict, $"HTTP {(int)cancelAllocated.StatusCode}");

var (secondToken, _) = await ApprovedTokenAsync(purchase);
var second = await ewp.SendAsync(ApiRequest(HttpMethod.Post, "/api/gold-orders", secondToken));
var secondNumber = Text(await second.Content.ReadAsStringAsync(), "number");
var cancelled = await ewp.SendAsync(ApiRequest(HttpMethod.Post, $"/api/gold-orders/{secondNumber}/cancel", machineToken,
    new JsonObject { ["reason"] = "The payment from Four Walls failed." }));
Check("An order awaiting payment can be cancelled (e.g. the payment failed)", cancelled.IsSuccessStatusCode && Text(await cancelled.Content.ReadAsStringAsync(), "status") == "CANCELLED",
    $"{secondNumber}: HTTP {(int)cancelled.StatusCode}");

var (holdingsToken, holdingsConsent) = await ApprovedTokenAsync(Detail("holdings_view", new JsonObject()));
var holdings = await ewp.SendAsync(ApiRequest(HttpMethod.Get, "/api/holdings", holdingsToken));
var holdingsJson = await holdings.Content.ReadAsStringAsync();
var heldGrams = decimal.Parse(Text(holdingsJson, "grams") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
Check("With an approved holdings view: the grams and the activity", holdingsConsent.Contains("Show your Partner Bank holdings and activity") &&
    holdings.IsSuccessStatusCode && heldGrams >= orderGrams && holdingsJson.Contains(orderNumber!) && holdingsJson.Contains("allocated"),
    $"{heldGrams} g, worth AUD {Text(holdingsJson, "valueAud")}");

var holdingsWithPurchase = await ewp.SendAsync(ApiRequest(HttpMethod.Get, "/api/holdings", goldToken));
Check("A purchase token cannot read holdings", holdingsWithPurchase.StatusCode == HttpStatusCode.Forbidden, $"HTTP {(int)holdingsWithPurchase.StatusCode}");

// The customer's own window: "My Partner Bank" - the same grams and activity, no EWP involved.
var myUrl = $"{WindowOrigin}/My";
var (myPage, _) = await Navigate(await browser.GetAsync(myUrl), myUrl);
var myRaw = await myPage.Content.ReadAsStringAsync();
var myHtml = WebUtility.HtmlDecode(myRaw);
Check("My Partner Bank shows the customer's own grams, applications and activity",
    myPage.IsSuccessStatusCode && myHtml.Contains($"{heldGrams:0.0000} g") && myHtml.Contains(orderNumber!) && myHtml.Contains("Partner Term Deposit, 12 months"),
    $"{heldGrams:0.0000} g, order {orderNumber}");

Console.WriteLine();
Console.WriteLine("6. A shared desk: signing out leaves nothing for the next person");

// A desk where the window's session has ALREADY ended (10 idle minutes): signing out must still work.
using (var expiredDesk = Client(null, new CookieContainer()))
{
    var late = await expiredDesk.PostAsync($"{WindowOrigin}/SignOut", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = Hidden(myRaw, "__RequestVerificationToken")
    }));
    var lateLocation = late.Headers.Location?.ToString() ?? string.Empty;
    Check("Sign out after the window's session has expired goes on to the Partner Bank's sign-out (no error page)",
        (int)late.StatusCode is >= 300 and < 400 && lateLocation.Contains("/connect/endsession"), $"HTTP {(int)late.StatusCode}");
}

var signOut = await browser.PostAsync($"{WindowOrigin}/SignOut", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["__RequestVerificationToken"] = Hidden(myRaw, "__RequestVerificationToken")
}));
var (_, signedOutUrl) = await Navigate(signOut, $"{WindowOrigin}/SignOut");
Check("Sign out in the window ends its session AND the Partner Bank's - and shows the Partner Bank's sign-in page", signedOutUrl.Contains("/Account/Login"), signedOutUrl.Split('?')[0]);

var (_, afterSignOutUrl) = await Navigate(await browser.GetAsync(myUrl), myUrl);
Check("After sign-out, My Partner Bank asks for a password again", afterSignOutUrl.Contains("/Account/Login"), afterSignOutUrl.Split('?')[0]);

Console.WriteLine();
Console.WriteLine("7. A first-time customer registers with the details Four Walls verified");

using var newcomer = Client(null, new CookieContainer());
current = newcomer;
var runId = RandomNumberGenerator.GetInt32(100_000, 1_000_000);
var newCustomerNumber = $"CUST-9{runId}";
var newUsername = $"priya{runId}";
const string NewPassword = "Partner-New-2026!x";
JsonArray NewcomerDetail() => new(new JsonObject
{
    ["type"] = "holdings_view",
    ["customer"] = new JsonObject { ["ewpCustomerNumber"] = newCustomerNumber, ["name"] = "Priya Raman", ["dateOfBirth"] = "1991-03-22", ["kycVerified"] = true },
    ["actor"] = new JsonObject { ["lanId"] = "somit", ["branch"] = "SYD001" }
});

async Task<(string LoginHtml, string LoginUrl, string CodeVerifier)> NewcomerArrivesAsync()
{
    var codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(32));
    var (_, pushed, _) = await PushSignedAsync(NewcomerDetail(), codeVerifier);
    var url = $"{Authority}/connect/authorize?client_id={ClientId}&request_uri={Uri.EscapeDataString(pushed!)}";
    var (page, pageUrl) = await Navigate(await newcomer.GetAsync(url), url);
    return (await page.Content.ReadAsStringAsync(), pageUrl, codeVerifier);
}

var (newcomerLogin, newcomerLoginUrl, newcomerVerifier) = await NewcomerArrivesAsync();
Check("The sign-in page offers a verified customer without an account to register",
    newcomerLoginUrl.Contains("/Account/Login") && newcomerLogin.Contains("New to Partner Bank?"), newcomerLoginUrl.Split('?')[0]);

var registerUrl = $"{Authority}/Account/Register?returnUrl={Uri.EscapeDataString(Hidden(newcomerLogin, "Input.ReturnUrl"))}";
var registerRaw = await (await newcomer.GetAsync(registerUrl)).Content.ReadAsStringAsync();
var registerHtml = WebUtility.HtmlDecode(registerRaw);
Check("Registration shows the details Four Walls verified - read-only, not typed by the person",
    registerHtml.Contains("Priya Raman") && registerHtml.Contains("22 March 1991") && registerHtml.Contains(newCustomerNumber) &&
    !registerRaw.Contains("name=\"Input.Name\""), newCustomerNumber);

var registered = await newcomer.PostAsync(registerUrl, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["Input.Username"] = newUsername,
    ["Input.Password"] = NewPassword,
    ["Input.Confirm"] = NewPassword,
    ["Input.ReturnUrl"] = Hidden(registerRaw, "Input.ReturnUrl"),
    ["__RequestVerificationToken"] = Hidden(registerRaw, "__RequestVerificationToken")
}));
var (verifyPage, verifyUrl) = await Navigate(registered, registerUrl);
var verifyRaw = await verifyPage.Content.ReadAsStringAsync();
var oneTimeCode = Regex.Match(verifyRaw, @"your code is <strong>(\d{6})</strong>").Groups[1].Value;
Check("A one-time code is sent (in Development: shown in place of an SMS)", verifyUrl.Contains("/Account/Verify") && oneTimeCode.Length == 6, verifyUrl.Split('?')[0]);

async Task<HttpResponseMessage> SendCodeAsync(string html, string code) =>
    await newcomer.PostAsync(verifyUrl, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["Id"] = Hidden(html, "Id"),
        ["Code"] = code,
        ["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken")
    }));

var wrongCode = ((int.Parse(oneTimeCode) + 1) % 1_000_000).ToString("D6");
var wrongAnswer = await (await SendCodeAsync(verifyRaw, wrongCode)).Content.ReadAsStringAsync();
Check("A wrong code is refused", wrongAnswer.Contains("That code is not right"));

var (newcomerConsent, newcomerConsentUrl) = await Navigate(await SendCodeAsync(wrongAnswer, oneTimeCode), verifyUrl);
var newcomerConsentRaw = await newcomerConsent.Content.ReadAsStringAsync();
Check("The right code creates the account and goes on to consent",
    newcomerConsentUrl.Contains("/Consent") && WebUtility.HtmlDecode(newcomerConsentRaw).Contains("Show your Partner Bank holdings and activity"), newcomerConsentUrl.Split('?')[0]);

var newcomerCallback = await DecideAsync(newcomerConsentRaw, newcomerConsentUrl, "approve");
var newcomerExchange = await ewp.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["redirect_uri"] = RedirectUri, ["code_verifier"] = newcomerVerifier,
    ["code"] = WebUtility.UrlDecode(Regex.Match(newcomerCallback, "[?&]code=([^&]+)").Groups[1].Value)
}));
var newcomerToken = Text(await newcomerExchange.Content.ReadAsStringAsync(), "access_token");
var newcomerSubject = newcomerToken is null ? null : (string?)JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlDecode(newcomerToken.Split('.')[1])))!["sub"];
Check("The new customer has their own Partner Bank identity", newcomerSubject is not null && newcomerSubject.StartsWith("pb-") && newcomerSubject != "pb-customer-001", newcomerSubject ?? "no token");

var (againLogin, _, _) = await NewcomerArrivesAsync();
var againHtml = WebUtility.HtmlDecode(againLogin);
Check("Next time: \"Welcome back\", the username filled in, no registration offered",
    againHtml.Contains("Welcome back, Priya Raman") && againLogin.Contains($"value=\"{newUsername}\"") && !againHtml.Contains("New to Partner Bank?"));

var registerAgain = WebUtility.HtmlDecode(await (await newcomer.GetAsync(
    $"{Authority}/Account/Register?returnUrl={Uri.EscapeDataString(Hidden(againLogin, "Input.ReturnUrl"))}")).Content.ReadAsStringAsync());
Check("The same Four Walls customer cannot register twice", registerAgain.Contains("You already have a Partner Bank account"));

Console.WriteLine();
Console.WriteLine("8. EWP as the Partner Bank's distributor: Form VII opens the account, a transfer slip buys gold");

// EWP's OWN token (client credentials over mutual TLS): no customer, bound to EWP's certificate.
var distributionResponse = await ewp.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials", ["client_id"] = ClientId, ["scope"] = "partner.distribution"
}));
var distributionToken = Text(await distributionResponse.Content.ReadAsStringAsync(), "access_token");
var distributionPayload = distributionToken is null ? null : JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlDecode(distributionToken.Split('.')[1])));
Check("EWP gets its own token (client credentials, no customer), bound to its certificate",
    distributionPayload?["cnf"]?["x5t#S256"] is not null && distributionPayload["sub"] is null, $"HTTP {(int)distributionResponse.StatusCode}");

var sydneyToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")).DateTime);
var distRun = RandomNumberGenerator.GetInt32(100_000, 1_000_000);
var distCustomer = $"CUST-8{distRun}";
var distMobile = $"+61400{distRun}";
static string ScanHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

string SignInstruction(JsonObject instruction, ECDsa? key = null, string? jti = null, long? issuedAt = null, string typ = "partner-instruction+jwt")
{
    var iat = issuedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var header = Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["alg"] = "ES256", ["typ"] = typ, ["kid"] = signingCertificate.Thumbprint }.ToJsonString()));
    var body = Base64Url(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["iss"] = ClientId, ["aud"] = "partner-api", ["jti"] = jti ?? Guid.NewGuid().ToString(), ["iat"] = iat, ["exp"] = iat + 300,
        ["instruction"] = instruction.DeepClone()
    }.ToJsonString()));
    var signature = Base64Url((key ?? signingCertificate.GetECDsaPrivateKey()!).SignData(Encoding.ASCII.GetBytes($"{header}.{body}"), HashAlgorithmName.SHA256));
    return $"{header}.{body}.{signature}";
}

async Task<(HttpStatusCode Status, string Body)> InstructAsync(string path, string compact, string? token = null, HttpClient? connection = null)
{
    var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}{path}") { Content = new StringContent(compact, Encoding.ASCII, "application/jwt") };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? distributionToken);
    var response = await (connection ?? ewp).SendAsync(request);
    return (response.StatusCode, await response.Content.ReadAsStringAsync());
}

JsonObject Actor() => new() { ["lanId"] = "somit", ["branch"] = "SYD001" };
JsonObject FormVii(string formNumber) => new()
{
    ["type"] = "digital_gold_account_opening",
    ["customer"] = new JsonObject { ["ewpCustomerNumber"] = distCustomer, ["name"] = "Camilla Parker", ["dateOfBirth"] = "1988-07-14", ["mobile"] = distMobile, ["kycVerified"] = true },
    ["form"] = new JsonObject { ["number"] = formNumber, ["signedOn"] = sydneyToday.ToString("yyyy-MM-dd"), ["scanSha256"] = ScanHash(formNumber) },
    ["actor"] = Actor()
};

var formNumber = $"VII-{distRun}";
var unsigned = $"{Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"partner-instruction+jwt\"}"))}.{SignInstruction(FormVii(formNumber)).Split('.')[1]}.";
var (unsignedStatus, _) = await InstructAsync("/api/distribution/gold-accounts", unsigned);
Check("An UNSIGNED instruction is refused", unsignedStatus == HttpStatusCode.BadRequest, $"HTTP {(int)unsignedStatus}");

using var strangerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var (strangerStatus, _) = await InstructAsync("/api/distribution/gold-accounts", SignInstruction(FormVii(formNumber), strangerKey));
Check("An instruction signed with ANOTHER key is refused", strangerStatus == HttpStatusCode.BadRequest, $"HTTP {(int)strangerStatus}");

var genuine = SignInstruction(FormVii(formNumber)).Split('.');
var tamperedBody = Base64Url(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Base64UrlDecode(genuine[1])).Replace("Camilla Parker", "Mallory Parker")));
var (tamperedStatus, _) = await InstructAsync("/api/distribution/gold-accounts", $"{genuine[0]}.{tamperedBody}.{genuine[2]}");
Check("An ALTERED instruction (another name after signing) is refused", tamperedStatus == HttpStatusCode.BadRequest, $"HTTP {(int)tamperedStatus}");

var (expiredStatus, _) = await InstructAsync("/api/distribution/gold-accounts", SignInstruction(FormVii(formNumber), issuedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 900));
Check("An EXPIRED instruction is refused", expiredStatus == HttpStatusCode.BadRequest, $"HTTP {(int)expiredStatus}");

var extraField = FormVii(formNumber);
extraField["customer"]!["creditLimit"] = "1000000";
var (extraStatus, _) = await InstructAsync("/api/distribution/gold-accounts", SignInstruction(extraField));
Check("A signed instruction with an unknown field is refused (allow-list)", extraStatus == HttpStatusCode.BadRequest, $"HTTP {(int)extraStatus}");

var (noScopeStatus, _) = await InstructAsync("/api/distribution/gold-accounts", SignInstruction(FormVii(formNumber)), token: customerToken);
Check("A token without the distribution scope is refused", noScopeStatus == HttpStatusCode.Forbidden, $"HTTP {(int)noScopeStatus}");

var openingJti = Guid.NewGuid().ToString();
var opening = SignInstruction(FormVii(formNumber), jti: openingJti);
var (openStatus, openBody) = await InstructAsync("/api/distribution/gold-accounts", opening);
var goldAccount = Text(openBody, "number");
Check("Form VII, signed by EWP: the Partner Bank opens a Digital Gold account", openStatus == HttpStatusCode.Created && goldAccount?.StartsWith("PB-DGA-") == true, $"HTTP {(int)openStatus} {goldAccount}");

var (againStatus, againBody) = await InstructAsync("/api/distribution/gold-accounts", opening);
Check("The same instruction again: the same account, not a second one", againStatus == HttpStatusCode.OK && Text(againBody, "number") == goldAccount, $"HTTP {(int)againStatus}");

var (secondStatus, secondBody) = await InstructAsync("/api/distribution/gold-accounts", SignInstruction(FormVii($"VII-{distRun}-2")));
Check("A second Form VII for the same customer is refused (one account per customer)",
    secondStatus == HttpStatusCode.Conflict && secondBody.Contains(goldAccount ?? "?"), $"HTTP {(int)secondStatus}");

var lookup = await ewp.SendAsync(ApiRequest(HttpMethod.Get, $"/api/distribution/gold-accounts?ewpCustomerNumber={distCustomer}", distributionToken));
Check("EWP finds the account by its customer number", lookup.IsSuccessStatusCode && Text(await lookup.Content.ReadAsStringAsync(), "number") == goldAccount, $"HTTP {(int)lookup.StatusCode}");

JsonObject Slip(string slipNumber, string? mobile = null, DateOnly? signedOn = null, string? account = null) => new()
{
    ["type"] = "digital_gold_purchase",
    ["goldAccountNumber"] = account ?? goldAccount,
    ["customer"] = new JsonObject { ["ewpCustomerNumber"] = distCustomer },
    ["instructedAmount"] = new JsonObject { ["currency"] = "AUD", ["amount"] = "20.00" },
    ["debtorAccount"] = new JsonObject { ["bsb"] = "062-000", ["accountNumber"] = "12345678" },
    ["slip"] = new JsonObject { ["number"] = slipNumber, ["signedOn"] = (signedOn ?? sydneyToday).ToString("yyyy-MM-dd"), ["mobile"] = mobile ?? distMobile, ["scanSha256"] = ScanHash(slipNumber) },
    ["actor"] = Actor()
};

var slipNumber = $"TAS-{distRun}";
var (buyStatus, buyBody) = await InstructAsync("/api/distribution/gold-orders", SignInstruction(Slip(slipNumber)));
var distOrder = Text(buyBody, "number");
var buyJson = JsonNode.Parse(buyBody);
Check("A transfer slip, signed by EWP: AUD 20.00 of gold, grams locked, EWP's commission recorded",
    buyStatus == HttpStatusCode.Created && (string?)buyJson?["goldAccountNumber"] == goldAccount && (decimal?)buyJson?["commissionAud"] == 0.10m && (decimal?)buyJson?["grams"] > 0,
    $"HTTP {(int)buyStatus} {distOrder}");

var (slipAgainStatus, _) = await InstructAsync("/api/distribution/gold-orders", SignInstruction(Slip(slipNumber)));
Check("The same slip in a new instruction is refused (one slip, one purchase)", slipAgainStatus == HttpStatusCode.Conflict, $"HTTP {(int)slipAgainStatus}");

var (mobileStatus, _) = await InstructAsync("/api/distribution/gold-orders", SignInstruction(Slip($"TAS-{distRun}-M", mobile: "+61499999999")));
Check("A slip with a mobile number that is not the account's is refused", mobileStatus == HttpStatusCode.Conflict, $"HTTP {(int)mobileStatus}");

var (oldSlipStatus, _) = await InstructAsync("/api/distribution/gold-orders", SignInstruction(Slip($"TAS-{distRun}-Y", signedOn: sydneyToday.AddDays(-1))));
Check("A slip not signed today is refused (it buys at today's price)", oldSlipStatus == HttpStatusCode.BadRequest, $"HTTP {(int)oldSlipStatus}");

var (unknownStatus, _) = await InstructAsync("/api/distribution/gold-orders", SignInstruction(Slip($"TAS-{distRun}-U", account: "PB-DGA-ZZZZZZ")));
Check("A purchase for an account EWP did not introduce: 404", unknownStatus == HttpStatusCode.NotFound, $"HTTP {(int)unknownStatus}");

var distAdvice = await ewp.SendAsync(ApiRequest(HttpMethod.Post, $"/api/gold-orders/{distOrder}/payment-advice", distributionToken,
    new JsonObject { ["networkReference"] = $"NPP-D{distRun}", ["amount"] = 20.00m }));
Check("Payment advice with EWP's own token: the grams are allocated", distAdvice.IsSuccessStatusCode && Text(await distAdvice.Content.ReadAsStringAsync(), "status") == "ALLOCATED",
    $"HTTP {(int)distAdvice.StatusCode}");

var holdingResponse = await ewp.SendAsync(ApiRequest(HttpMethod.Get, $"/api/distribution/gold-accounts/{goldAccount}", distributionToken));
var holdingJson = JsonNode.Parse(await holdingResponse.Content.ReadAsStringAsync());
var holdingText = holdingJson?.ToJsonString() ?? "";
Check("EWP reads the holding from the Partner Bank: grams, today's value, and the SMS notices the Partner Bank sent the customer",
    holdingResponse.IsSuccessStatusCode && (decimal?)holdingJson?["holding"]?["grams"] == (decimal?)buyJson?["grams"] && (decimal?)holdingJson?["holding"]?["valueAud"] > 0 &&
    holdingText.Contains("Notice sent by SMS"), $"HTTP {(int)holdingResponse.StatusCode}");

Console.WriteLine();
Console.WriteLine("9. EWP's staff member views the account at the Partner Bank (PAR-style handoff)");
JsonObject StaffView(string? customer = null, bool askForMore = false)
{
    var view = new JsonObject
    {
        ["type"] = "staff_account_view",
        ["goldAccountNumber"] = goldAccount,
        ["customer"] = new JsonObject { ["ewpCustomerNumber"] = customer ?? distCustomer },
        ["actor"] = Actor()
    };
    if (askForMore)
        view["allAccounts"] = true;
    return view;
}

var viewInstruction = SignInstruction(StaffView());
var (viewStatus, viewBody) = await InstructAsync("/api/distribution/staff-views", viewInstruction);
var staffRequestUri = Text(viewBody, "request_uri");
Check("EWP pushes a SIGNED staff view over mutual TLS: a one-time request_uri, nothing about the account",
    viewStatus == HttpStatusCode.Created && staffRequestUri?.StartsWith("urn:partner-bank:staff-view:") == true && !viewBody.Contains(goldAccount ?? "?"),
    $"HTTP {(int)viewStatus}");

var (replayStatus, _) = await InstructAsync("/api/distribution/staff-views", viewInstruction);
Check("The same signed staff view pushed again: refused", replayStatus == HttpStatusCode.Conflict, $"HTTP {(int)replayStatus}");

var (otherCustomerStatus, _) = await InstructAsync("/api/distribution/staff-views", SignInstruction(StaffView(customer: "CUST-1")));
Check("A staff view naming another customer for the account: 404", otherCustomerStatus == HttpStatusCode.NotFound, $"HTTP {(int)otherCustomerStatus}");

var (extraFieldStatus, _) = await InstructAsync("/api/distribution/staff-views", SignInstruction(StaffView(askForMore: true)));
Check("A staff view asking for more (an unknown field): refused", extraFieldStatus == HttpStatusCode.BadRequest, $"HTTP {(int)extraFieldStatus}");

using var staffBrowser = Client(null);
var staffAddress = $"{WindowOrigin}/StaffView?request_uri={Uri.EscapeDataString(staffRequestUri ?? "")}";
var staffPage = await staffBrowser.GetAsync(staffAddress);
var staffHtml = await staffPage.Content.ReadAsStringAsync();
Check("The browser opens the Partner Bank's read-only page: the account, its grams, who is looking",
    staffPage.IsSuccessStatusCode && staffHtml.Contains(goldAccount ?? "?") && staffHtml.Contains("Read-only view for") && staffHtml.Contains("somit") &&
    staffPage.Headers.CacheControl?.NoStore == true, $"HTTP {(int)staffPage.StatusCode}");

var staffAgain = await staffBrowser.GetAsync(staffAddress);
Check("The same request_uri again: not shown (one use)", staffAgain.StatusCode == HttpStatusCode.NotFound, $"HTTP {(int)staffAgain.StatusCode}");

var guessed = await staffBrowser.GetAsync($"{WindowOrigin}/StaffView?request_uri={Uri.EscapeDataString("urn:partner-bank:staff-view:" + Base64Url(RandomNumberGenerator.GetBytes(32)))}");
Check("A made-up request_uri: not shown", guessed.StatusCode == HttpStatusCode.NotFound, $"HTTP {(int)guessed.StatusCode}");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;