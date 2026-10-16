// The mutual-TLS + PAR handshake, checked from EWP's side - a single-file .NET 10 app:
//
//   dotnet run tools/HandshakeCheck/HandshakeCheck.cs                 (the server on this machine)
//   dotnet run tools/HandshakeCheck/HandshakeCheck.cs -- 192.168.1.12 (the server on the home PC)
//
// Needs %USERPROFILE%\.ewp-pki from EWP's ps\pki\New-EwpLocalPki.ps1 (EWP's client certificate, its
// request-signing key and the trust root). It connects to partner-auth.ewp.home.arpa at the given address itself, so no
// hosts-file entry is needed, and it trusts ONLY the PoC's root - never a root Windows trusts.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string Authority = "https://partner-auth.ewp.home.arpa:47401";
const string ClientId = "FourWalls.Ewp.PartnerProducts";
const string RedirectUri = "https://ewp-partner.dev.localhost:46384/partner/callback";

var address = args.Length > 0 ? IPAddress.Parse(args[0]) : IPAddress.Loopback;
var pki = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ewp-pki");
var trustRoot = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(pki, "ewp-trust-root.cer"));
var clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(
    Path.Combine(pki, "ewp-partner-client.pfx"), File.ReadAllText(Path.Combine(pki, "ewp-partner-client.pfx.password")).Trim());
var signingCertificate = X509CertificateLoader.LoadPkcs12FromFile(
    Path.Combine(pki, "ewp-request-signing.pfx"), File.ReadAllText(Path.Combine(pki, "ewp-request-signing.pfx.password")).Trim());
var signingKey = signingCertificate.GetECDsaPrivateKey()!;

var failures = 0;
void Check(string name, bool passed, string detail = "")
{
    Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(passed ? "  PASS  " : "  FAIL  ");
    Console.ResetColor();
    Console.WriteLine(detail.Length == 0 ? name : $"{name} - {detail}");
    if (!passed) failures++;
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The server certificate must chain to the PoC's root (custom trust, nothing else) and
// │ match the host name; with a client certificate, EWP's is offered - and only it.
// └──────────────────────────────────────────────────────────────────────────────────────
HttpClient Client(bool withCertificate) => new(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
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
        LocalCertificateSelectionCallback = (_, _, _, _, _) => withCertificate ? clientCertificate : null!
    }
});

using var browser = Client(withCertificate: false);
using var ewp = Client(withCertificate: true);

Console.WriteLine($"Partner Bank authorization server at {address} ({Authority})");
Console.WriteLine();

// 1. Discovery
var discovery = JsonDocument.Parse(await browser.GetStringAsync($"{Authority}/.well-known/openid-configuration")).RootElement;
string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ToString() : null;
var mtls = discovery.GetProperty("mtls_endpoint_aliases");
var parEndpoint = Text(mtls, "pushed_authorization_request_endpoint") ?? Text(discovery, "pushed_authorization_request_endpoint");
var tokenEndpoint = Text(mtls, "token_endpoint");
Check("Discovery names the configured issuer", Text(discovery, "issuer") == Authority, Text(discovery, "issuer") ?? "none");
Check("PAR is required", Text(discovery, "require_pushed_authorization_requests") == "True");
Check("Certificate-bound access tokens are advertised", Text(discovery, "tls_client_certificate_bound_access_tokens") == "True");
Check("tls_client_auth is a supported client authentication", Text(discovery, "token_endpoint_auth_methods_supported")?.Contains("tls_client_auth") == true);
Check("mTLS endpoints are advertised", tokenEndpoint is not null && parEndpoint is not null, $"token {tokenEndpoint}, PAR {parEndpoint}");
Check("The authorization_details types are advertised", Text(discovery, "authorization_details_types_supported")?.Contains("digital_gold_purchase") == true,
    Text(discovery, "authorization_details_types_supported") ?? "none");

// 2. PAR - a request object signed by EWP (JAR), stating exactly what is authorised (authorization_details)
var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

JsonArray Details(string type, JsonObject? extra = null)
{
    var detail = new JsonObject
    {
        ["type"] = type,
        ["customer"] = new JsonObject { ["ewpCustomerNumber"] = "CUST-100009", ["name"] = "Camilla Parker", ["dateOfBirth"] = "1988-07-14", ["kycVerified"] = true },
        ["actor"] = new JsonObject { ["lanId"] = "somit", ["branch"] = "SYD001" }
    };
    foreach (var (name, value) in extra ?? new JsonObject()) detail[name] = value?.DeepClone();
    return new JsonArray(detail);
}

var holdings = Details("holdings_view");
var purchase = Details("digital_gold_purchase", new JsonObject
{
    ["instructedAmount"] = new JsonObject { ["currency"] = "AUD", ["amount"] = "10.00" },
    ["debtorAccount"] = new JsonObject { ["bsb"] = "062-000", ["accountNumber"] = "72947086" }
});

FormUrlEncodedContent Signed(JsonArray? details, ECDsa? key = null, bool tamper = false) =>
    new(new Dictionary<string, string> { ["client_id"] = ClientId, ["request"] = RequestObject(details, key ?? signingKey, tamper) });

FormUrlEncodedContent Unsigned() => new(new Dictionary<string, string>
{
    ["client_id"] = ClientId, ["response_type"] = "code", ["redirect_uri"] = RedirectUri, ["scope"] = "openid profile partner.products.read",
    ["state"] = "s", ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["authorization_details"] = holdings.ToJsonString()
});

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A JAR request object (RFC 9101) as FAPI 2.0 Message Signing describes it: typed
// │ oauth-authz-req+jwt, ES256 with EWP's request-signing key (its key ID in "kid"), iss = EWP's client ID,
// │ aud = the Partner's issuer, short-lived (nbf / exp), a unique jti.
// └──────────────────────────────────────────────────────────────────────────────────────
string RequestObject(JsonArray? details, ECDsa key, bool tamper)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var payload = new JsonObject
    {
        ["iss"] = ClientId, ["aud"] = Authority, ["client_id"] = ClientId, ["jti"] = Guid.NewGuid().ToString(),
        ["iat"] = now, ["nbf"] = now, ["exp"] = now + 300,
        ["response_type"] = "code", ["redirect_uri"] = RedirectUri, ["scope"] = "openid profile partner.products.read",
        ["state"] = Base64Url(RandomNumberGenerator.GetBytes(16)), ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
    };
    if (details is not null) payload["authorization_details"] = details.DeepClone();
    var header = Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["alg"] = "ES256", ["typ"] = "oauth-authz-req+jwt", ["kid"] = signingCertificate.Thumbprint }.ToJsonString()));
    var body = Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
    var signature = Base64Url(key.SignData(Encoding.ASCII.GetBytes($"{header}.{body}"), HashAlgorithmName.SHA256));
    if (tamper)   // the same signature over a CHANGED payload: the amount, raised on the way
        body = Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString().Replace("\"10.00\"", "\"9999.00\"")));
    return $"{header}.{body}.{signature}";
}

async Task<(HttpResponseMessage? Response, string Body)> Push(HttpClient client, FormUrlEncodedContent content)
{
    var response = await TryPost(client, parEndpoint!, content);
    return (response, response is null ? string.Empty : await response.Content.ReadAsStringAsync());
}

var (noCertificatePar, _) = await Push(browser, Signed(holdings));
Check("PAR without EWP's certificate is refused", noCertificatePar is not { StatusCode: HttpStatusCode.Created }, Describe(noCertificatePar));

var (unsigned, unsignedBody) = await Push(ewp, Unsigned());
Check("An UNSIGNED request (plain parameters) is refused", unsigned is not { StatusCode: HttpStatusCode.Created }, $"{Describe(unsigned)} {Error(unsignedBody)}");

var (tampered, tamperedBody) = await Push(ewp, Signed(purchase, tamper: true));
Check("A signed request ALTERED on the way (amount raised) is refused", tampered is not { StatusCode: HttpStatusCode.Created }, $"{Describe(tampered)} {Error(tamperedBody)}");

using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var (otherSigner, otherSignerBody) = await Push(ewp, Signed(purchase, otherKey));
Check("A request signed with ANOTHER key is refused", otherSigner is not { StatusCode: HttpStatusCode.Created }, $"{Describe(otherSigner)} {Error(otherSignerBody)}");

var (noDetails, noDetailsBody) = await Push(ewp, Signed(null));
Check("A signed request WITHOUT authorization_details is refused", noDetails is not { StatusCode: HttpStatusCode.Created }, $"{Describe(noDetails)} {Error(noDetailsBody)}");

var (unknownType, unknownTypeBody) = await Push(ewp, Signed(Details("home_loan_application")));
Check("An UNKNOWN authorization_details type is refused", unknownType is not { StatusCode: HttpStatusCode.Created }, $"{Describe(unknownType)} {Error(unknownTypeBody)}");

var zero = Details("digital_gold_purchase", new JsonObject
{
    ["instructedAmount"] = new JsonObject { ["currency"] = "AUD", ["amount"] = "0.00" },
    ["debtorAccount"] = new JsonObject { ["bsb"] = "062-000", ["accountNumber"] = "72947086" }
});
var (zeroAmount, zeroAmountBody) = await Push(ewp, Signed(zero));
Check("A purchase of AUD 0.00 is refused", zeroAmount is not { StatusCode: HttpStatusCode.Created }, $"{Describe(zeroAmount)} {Error(zeroAmountBody)}");

var (signedPurchase, signedPurchaseBody) = await Push(ewp, Signed(purchase));
Check("A signed Digital Gold purchase (AUD 10.00) is accepted", signedPurchase is { StatusCode: HttpStatusCode.Created }, $"{Describe(signedPurchase)} {Error(signedPurchaseBody)}");

var (par, parBody) = await Push(ewp, Signed(holdings));
var requestUri = par is { StatusCode: HttpStatusCode.Created } ? Text(JsonDocument.Parse(parBody).RootElement, "request_uri") : null;
Check("PAR with EWP's certificate and a signed request returns a request_uri", requestUri is not null, requestUri ?? $"{Describe(par)} {parBody}");

// 3. An authorization request that was not pushed first
var direct = await browser.GetAsync($"{Authority}/connect/authorize?client_id={ClientId}&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
    $"&scope=openid&code_challenge={challenge}&code_challenge_method=S256");
var directLocation = direct.Headers.Location?.ToString() ?? string.Empty;
Check("An authorization request that was not pushed is refused", directLocation.Contains("/Error", StringComparison.OrdinalIgnoreCase), $"{(int)direct.StatusCode} -> {directLocation}");

// 4. The pushed request opens the sign-in page
if (requestUri is not null)
{
    var pushed = await browser.GetAsync($"{Authority}/connect/authorize?client_id={ClientId}&request_uri={Uri.EscapeDataString(requestUri)}");
    var location = pushed.Headers.Location?.ToString() ?? string.Empty;
    Check("The pushed request leads to the Partner Bank's sign-in", location.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase), $"{(int)pushed.StatusCode} -> {location}");
}

// 5. The mTLS port serves only server-to-server endpoints
var mtlsOrigin = new Uri(tokenEndpoint!).GetLeftPart(UriPartial.Authority);
var signInOnMtls = await ewp.GetAsync($"{mtlsOrigin}/Account/Login");
Check("The mTLS port does not serve the sign-in page", signInOnMtls.StatusCode == HttpStatusCode.NotFound, $"{mtlsOrigin} -> HTTP {(int)signInOnMtls.StatusCode}");

// 6. A token for EWP's own calls, bound to its certificate
var tokenResponse = await TryPost(ewp, tokenEndpoint!, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials",
    ["client_id"] = ClientId,
    ["scope"] = "partner.products.read"
}));
var tokenBody = tokenResponse is null ? "{}" : await tokenResponse.Content.ReadAsStringAsync();
var accessToken = Text(JsonDocument.Parse(tokenBody).RootElement, "access_token");
Check("A token over mutual TLS", accessToken is not null, accessToken is null ? $"{Describe(tokenResponse)} {tokenBody}" : "");
if (accessToken is not null)
{
    var payload = JsonDocument.Parse(Base64UrlDecode(accessToken.Split('.')[1])).RootElement;
    var bound = payload.TryGetProperty("cnf", out var cnf) ? Text(cnf, "x5t#S256") : null;
    var expected = Base64Url(SHA256.HashData(clientCertificate.RawData));
    Check("The token is bound to EWP's certificate (cnf.x5t#S256)", bound == expected, $"cnf {bound ?? "missing"}");
}

var noCertificateToken = await TryPost(browser, tokenEndpoint!, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials",
    ["client_id"] = ClientId
}));
Check("A token request without EWP's certificate is refused", noCertificateToken is not { IsSuccessStatusCode: true }, Describe(noCertificateToken));

Console.WriteLine();
if (requestUri is not null)
    Console.WriteLine($"Browser test (within 60 s of the PAR): {Authority}/connect/authorize?client_id={ClientId}&request_uri={Uri.EscapeDataString(requestUri)}");
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

static async Task<HttpResponseMessage?> TryPost(HttpClient client, string url, HttpContent content)
{
    try { return await client.PostAsync(url, content); }
    catch (HttpRequestException) { return null; }   // the server may close the connection outright
}

static string Describe(HttpResponseMessage? response) => response is null ? "connection refused" : $"HTTP {(int)response.StatusCode}";

static string Error(string body)
{
    try { return JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var error) ? $"({error})" : string.Empty; }
    catch (JsonException) { return string.Empty; }
}

static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static byte[] Base64UrlDecode(string text)
{
    var padded = text.Replace('-', '+').Replace('_', '/');
    return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
}