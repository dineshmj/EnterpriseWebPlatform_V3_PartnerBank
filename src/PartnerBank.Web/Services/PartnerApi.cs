using System.Net;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Authentication;

namespace PartnerBank.Web.Services;

public sealed record ProductView(string Code, string Name, string Summary, string Kind);

public sealed record ApplicationView(string Number, string ProductCode, string ProductName, string Status, DateTimeOffset SubmittedAt);

/// <summary>
/// The Partner Bank API's internal port, called with the signed-in customer's OWN access token: the
/// window holds no data and asserts nothing - the API decides everything from the token.
/// </summary>
public sealed class PartnerApi(HttpClient http, IHttpContextAccessor accessor)
{
    public const string HttpClientName = "PartnerApiInternal";

    public async Task<IReadOnlyList<ProductView>> ProductsAsync(CancellationToken cancellationToken) =>
        await (await SendAsync(HttpMethod.Get, "internal/products", null, cancellationToken))
            .Content.ReadFromJsonAsync<List<ProductView>>(cancellationToken) ?? [];

    /// <summary>Opens a window link for the signed-in customer; false when it is not theirs, used, or expired.</summary>
    public async Task<bool> OpenHandoffAsync(string handoff, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Post, $"internal/handoffs/{Uri.EscapeDataString(handoff)}/open", null, cancellationToken)).IsSuccessStatusCode;

    /// <summary>True when the opened link can still be used to apply.</summary>
    public async Task<bool> HandoffUsableAsync(string handoff, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Get, $"internal/handoffs/{Uri.EscapeDataString(handoff)}", null, cancellationToken)).IsSuccessStatusCode;

    public async Task<ApplicationView?> ApplyAsync(string handoff, string productCode, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, "internal/applications", JsonContent.Create(new { handoff, productCode }), cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ApplicationView>(cancellationToken) : null;
    }

    public async Task<ApplicationView?> ApplicationAsync(string number, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, $"internal/applications/{Uri.EscapeDataString(number)}", null, cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ApplicationView>(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The customer's access token from the window's server-side session - never sent to
        // │ the browser; it goes only to the API, over TLS, on this machine.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var token = await accessor.HttpContext!.GetTokenAsync("access_token")
            ?? throw new InvalidOperationException("The customer's access token is missing from the session.");
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"The Partner Bank API refused the window's call ({(int)response.StatusCode}).");
        return response;
    }
}