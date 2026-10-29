using System.Security.Claims;

using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Auth;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid Id { get; }
    string? UserName { get; }
    string? Email { get; }
    string? FullName { get; }
    Guid? StoreId { get; }
    bool IsInRole(string role);
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public Guid Id => 
        Guid.TryParse(User?.FindFirst(ShopLabClaims.Subject)?.Value, out var id)
            ? id
            : throw new InvalidOperationException("Request chưa đăng nhập hoặc thiếu claim sub.");

    public string? UserName => User?.FindFirst(ShopLabClaims.Username)?.Value;
    public string? Email => User?.FindFirst(ShopLabClaims.Email)?.Value;
    public string? FullName => User?.FindFirst(ShopLabClaims.Name)?.Value;
    public Guid? StoreId => Guid.TryParse(User?.FindFirst(ShopLabClaims.StoreId)?.Value, out var id) ? id : null;

    public bool IsInRole(string role) => User?.IsInRole(role) == true;
}
