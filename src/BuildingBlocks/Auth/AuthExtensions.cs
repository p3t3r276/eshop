using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BuildingBlocks.Auth;

public static class Policies
{
    public const string Authenticated = "authenticated";
    public const string Customer = "customer";
    public const string Admin = "admin";
    public const string Cashier = "cashier";
    public const string StoreManager = "strore_manager";
    public const string ScoreStaff = "store_staff";
}

public static class Roles
{
    public const string Customer = "customer";
    public const string Admin = "admin";
    public const string Cashier = "cashier";
    public const string StoreManager = "store_manager";
}

public static class AuthExtensions
{
    /// <summary>
    /// JWT Bearer trỏ vào realm Keycloak. Authority lấy từ cấu hình Keycloak:Authority
    /// (AppHost truyền vào qua biến môi trường Keycloak__Authority).
    /// </summary>
    public static IHostApplicationBuilder AddShopLabAuth(this IHostApplicationBuilder builder)
    {
        var authority = builder.Configuration["Keycloak:Authority"] ?? "http://localhost:8080/realms/shoplab";
        var audience = builder.Configuration["Keycloak:Audience"] ?? "gateway";

        var auth = builder.Services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = AuthSchemes.Smart;
                    options.DefaultChallengeScheme = AuthSchemes.Smart;
                })
                .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = authority;
                    options.Audience = audience;
                    options.RequireHttpsMetadata = false;
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters.NameClaimType = ShopLabClaims.Username;
                    options.TokenValidationParameters.RoleClaimType = ShopLabClaims.Role;
                    // Keycloak chạy local qua http; issuer khớp vì mọi thứ đều dùng localhost:8080.
                    options.TokenValidationParameters.ValidateIssuer = true;
                })
                // Chọn scheme theo request: header X-Test-User chỉ được dùng ở môi trường LoadTest.
                .AddPolicyScheme(AuthSchemes.Smart, AuthSchemes.Smart, options =>
                {
                    options.ForwardDefaultSelector = ctx => TestUserAuthHandler.IsEnabled(builder.Environment) && ctx. Request.Headers.ContainsKey(TestUserAuthHandler.HeaderName)
                        ? TestUserAuthHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme;
                });

        if (TestUserAuthHandler.IsEnabled(builder.Environment))
        {
            auth.AddScheme<AuthenticationSchemeOptions, TestUserAuthHandler>(TestUserAuthHandler.SchemeName, _ => {});
        }
        
        builder.Services.AddTransient<IClaimsTransformation, KeycloakRolesTransformation>();

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Authenticated, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.Customer, p => p.RequireRole(Roles.Customer))
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Policies.Cashier, p => p.RequireRole(Roles.Cashier, Roles.StoreManager))
            .AddPolicy(Policies.StoreManager, p => p.RequireRole(Roles.StoreManager))
            .AddPolicy(Policies.StoreManager, p => p.RequireRole(Roles.Cashier, Roles.StoreManager));

        builder.Services.AddHttpContextAccessor();
        return builder;
    }
}

public static class AuthSchemes
{
    public const string Smart = "smart";
}

public static class ShopLabClaims
{
    public const string Role = "role";
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Username = "preferred_username";
    public const string StoreId = "store_id";
}
