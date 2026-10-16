using System.Text.Json.Nodes;

using Duende.IdentityServer.Models;

using PartnerBank.AuthorizationServer.Authorization;

namespace PartnerBank.AuthorizationServer.Customers;

/// <summary>The customer a referring organisation sent, as its SIGNED request describes them.</summary>
public sealed record ReferredCustomer(string ReferrerClientId, string ReferrerName, string CustomerNumber, string Name, string DateOfBirth)
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Only from the signed request object (validated again), and only for a customer the
    // │ organisation states it has verified (kycVerified) - never from anything the person
    // │ typed. Used to recognise or pre-fill; the person still authenticates here.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static ReferredCustomer? From(AuthorizationRequest? context)
    {
        if (context is null || !AuthorizationDetails.TryValidate(AuthorizationDetails.FromRequestObject(context.RequestObjectValues), out var normalized, out _))
            return null;
        var customer = JsonNode.Parse(normalized!)!.AsArray()[0]!["customer"]!;
        return customer["kycVerified"]?.GetValue<bool>() == true
            ? new ReferredCustomer(
                context.Client.ClientId, context.Client.ClientName ?? context.Client.ClientId,
                (string)customer["ewpCustomerNumber"]!, (string)customer["name"]!, (string)customer["dateOfBirth"]!)
            : null;
    }
}