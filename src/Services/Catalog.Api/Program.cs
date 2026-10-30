using BuildingBlocks.Auth;

using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabAuth();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();               // /openapi/v1.json
    app.MapScalarApiReference();    // /scalar
}

app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/ping", (ICurrentUser user) => Results.Ok(new
    {
        service = "catalog",
        user = user.UserName,
        at = DateTimeOffset.UtcNow
    }))
    .RequireAuthorization(Policies.Authenticated);

app.Run();
