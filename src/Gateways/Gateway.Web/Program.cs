using BuildingBlocks.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabAuth();

builder.Configuration.AddJsonFile("yarp.json", optional: false, reloadOnChange: true);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();

app.MapReverseProxy();

app.Run();
