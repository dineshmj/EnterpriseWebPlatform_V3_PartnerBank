using System.Security.Claims;

using Duende.IdentityModel;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

namespace PartnerBank.AuthorizationServer.Customers;

/// <summary>What a token says about a Partner Bank customer: their name - and whether the account still exists.</summary>
public sealed class CustomerProfileService(CustomerStore customers) : IProfileService
{
    public async Task GetProfileDataAsync(ProfileDataRequestContext context, CancellationToken ct)
    {
        var customer = await customers.FindAsync(context.Subject.GetSubjectId(), ct);
        if (customer is not null)
            context.AddRequestedClaims([new Claim(JwtClaimTypes.Name, customer.Name)]);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A session or token for an account that no longer exists stops working at once.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public async Task IsActiveAsync(IsActiveContext context, CancellationToken ct) =>
        context.IsActive = await customers.FindAsync(context.Subject.GetSubjectId(), ct) is not null;
}