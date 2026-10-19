using System.Net;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Authentication;

namespace PartnerBank.Web.Services;

public sealed record ProductView(string Code, string Name, string Summary, string Kind);

public sealed record ApplicationView(string Number, string ProductCode, string ProductName, string Status, DateTimeOffset SubmittedAt);

public sealed record ActivityView(DateTimeOffset At, string Text, string? Reference);

public sealed record HoldingsView(decimal Grams, decimal PricePerGram, decimal ValueAud, DateTimeOffset PricedAt, IReadOnlyList<ActivityView> Activity);

/// <summary>One account, as a distributor's staff member may see it (read-only).</summary>
public sealed record StaffViewPage(
    string AccountNumber, string HolderName, string EwpCustomerNumber, DateTimeOffset OpenedAt, string Distributor,
    string StaffLanId, string StaffBranch, DateTimeOffset ViewedAt, HoldingsView Holding);

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

    /// <summary>The signed-in customer's gold, today's value and activity.</summary>
    public async Task<HoldingsView?> HoldingsAsync(CancellationToken cancellationToken) =>
        await (await SendAsync(HttpMethod.Get, "internal/holdings", null, cancellationToken))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<HoldingsView>(cancellationToken);

    /// <summary>The signed-in customer's applications, newest first.</summary>
    public async Task<IReadOnlyList<ApplicationView>> MyApplicationsAsync(CancellationToken cancellationToken) =>
        await (await SendAsync(HttpMethod.Get, "internal/applications", null, cancellationToken))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<List<ApplicationView>>(cancellationToken) ?? [];

    public async Task<ApplicationView?> ApplicationAsync(string number, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, $"internal/applications/{Uri.EscapeDataString(number)}", null, cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ApplicationView>(cancellationToken);
    }

    /// <summary>
    /// Redeems a distributor's staff-view request_uri - once; null when unknown, used or expired. No
    /// customer is signed in here: the one-time request_uri is the authorisation (internal port only).
    /// </summary>
    public async Task<StaffViewPage?> RedeemStaffViewAsync(string requestUri, CancellationToken cancellationToken)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The ONE call without a customer's token: nobody signs in for a staff view. The
        // │ one-time request_uri is the authorisation, and it is redeemed only on the API's
        // │ localhost-only internal port, server-side - the browser never reaches the API.
        // └──────────────────────────────────────────────────────────────────────────────────────
        using var response = await http.PostAsJsonAsync("internal/staff-views/redeem", new { requestUri }, cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<StaffViewPage>(cancellationToken) : null;
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