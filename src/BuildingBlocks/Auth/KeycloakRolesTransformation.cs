using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;

namespace BuildingBlocks.Auth;

public class KeycloakRolesTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            return Task.FromResult(principal);

        if (identity.HasClaim(c => c.Type == ShopLabClaims.Role))
            return Task.FromResult(principal);

        var realmAccess = identity.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(realmAccess))
            return Task.FromResult(principal);

        using var doc = JsonDocument.Parse(realmAccess);
        if (doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
        {
            foreach(var role in roles.EnumerateArray())
            {
                var value = role.GetString();
                if (!string.IsNullOrEmpty(value))
                    identity.AddClaim(new Claim(ShopLabClaims.Role, value));
            }
        }

        return Task.FromResult(principal);
    }
}
