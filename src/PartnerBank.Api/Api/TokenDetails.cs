using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PartnerBank.Api.Api;

/// <summary>The authorization_details the customer approved, as the access token carries them (RFC 9396).</summary>
public static class TokenDetails
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The detail comes from the access token - issued by the Partner's authorization server,
    // │ after the customer's consent, from EWP's SIGNED request - never from a request body. A
    // │ JSON array in a JWT may arrive as one claim per element; they are put back together and
    // │ exactly one detail of the expected type is required.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static JsonObject? Single(ClaimsPrincipal caller, string type)
    {
        var values = caller.FindAll("authorization_details").Select(c => c.Value.Trim()).ToList();
        if (values.Count == 0)
            return null;
        var json = values.Count == 1 && values[0].StartsWith('[') ? values[0] : "[" + string.Join(",", values) + "]";
        try
        {
            return JsonNode.Parse(json) is JsonArray { Count: 1 } array && array[0] is JsonObject detail && (string?)detail["type"] == type
                ? detail
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}