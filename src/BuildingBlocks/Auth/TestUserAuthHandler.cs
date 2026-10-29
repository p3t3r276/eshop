using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth;

public sealed class TestUserAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestUser";
    public const string HeaderName = "X-Test-User";
    public const string RolesHeaderName = "X-Test-Roles";

    public static bool IsEnabled(IHostEnvironment env) => env.IsEnvironment("LoadTest");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || string.IsNullOrEmpty(values))
            return Task.FromResult(AuthenticateResult.NoResult());

        var username = values.ToString();
        var roles = Request.Headers.TryGetValue(RolesHeaderName, out var r) && !string.IsNullOrWhiteSpace(r)
            ? r.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [Roles.Customer];

        var claims = new List<Claim>
        {
            new(ShopLabClaims.Subject, DeterministricId(username).ToString()),
            new(ShopLabClaims.Username, username),
            new(ShopLabClaims.Email, $"{username}@loadtest.local"),
            new(ShopLabClaims.Name, username)
        };
        claims.AddRange(roles.Select(role => new Claim(ShopLabClaims.Role, role)));

        var identity = new ClaimsIdentity(claims, SchemeName, ShopLabClaims.Username, ShopLabClaims.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    public static Guid DeterministricId(string username)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("shoplab-loadtest:" + username));
        return new Guid(hash);
    }
}
