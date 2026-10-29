# ShopLab — S0: Nền móng

Mục tiêu của S0: **một lệnh `aspire run` dựng toàn bộ môi trường dev** — Postgres, Keycloak (import realm sẵn user/role),
Catalog.Api (một endpoint `/ping` cần đăng nhập), Gateway.Web (YARP), storefront và admin (Angular trong Nx), cùng bộ test
chạy bằng Testcontainers. Chưa có nghiệp vụ; mọi thứ ở đây là khung mà S1–S8 dựa vào.

Cách đọc file này: làm lần lượt từng buổi. Mỗi buổi có việc cần làm, lệnh, **nội dung đầy đủ của từng file** cần tạo, và cách kiểm tra cuối buổi.

**Thời lượng:** 2 tuần · 9 buổi (mỗi buổi ~2–3 giờ ngoài giờ làm). **File:** 77 tạo mới, 0 sửa.

## Lịch

| Tuần | Mục tiêu | Buổi |
| --- | --- | --- |
| Tuần 1 | Backend chạy bằng một lệnh: AppHost, Keycloak, Catalog ping, Gateway, test | Buổi 1: Tạo repo và cấu hình build chung<br>Buổi 2: ServiceDefaults, Contracts, BuildingBlocks/Auth<br>Buổi 3: Keycloak realm và AppHost<br>Buổi 4: Catalog.Api ping và Gateway.Web (YARP)<br>Buổi 5: Hạ tầng test: xUnit v3 + Testcontainers |
| Tuần 2 | Frontend: Nx workspace, storefront + admin đăng nhập Keycloak | Buổi 6: Nx workspace và lib ui<br>Buổi 7: Lib auth và api-client<br>Buổi 8: App storefront<br>Buổi 9: App admin + chạy cả hệ thống qua AppHost |

## Trước khi bắt đầu

```bash
dotnet --version            # 10.0.x
node --version              # v22.x
docker version              # Docker Desktop đang chạy
dotnet tool install -g Aspire.Cli
dotnet tool install -g dotnet-ef
npm i -g nx@21              # tuỳ chọn, có thể dùng npx nx
```
Cài thêm extension REST Client (VS Code) hoặc dùng HTTP client của Rider để chạy file `http/*.http`.

**Tóm tắt — các lệnh tạo project của S0 theo thứ tự** (chi tiết và nội dung file ở từng buổi):

```bash
# Buổi 1 — repo + cấu hình gốc
dotnet new gitignore && dotnet new editorconfig && dotnet new buildprops && dotnet new packagesprops
dotnet new sln -n shoplab --format slnx
# Buổi 2 — project dùng chung
dotnet new install Aspire.ProjectTemplates
dotnet new aspire-servicedefaults -n ServiceDefaults -o src/ServiceDefaults
dotnet new classlib -n Contracts -o src/Contracts
dotnet new classlib -n BuildingBlocks -o src/BuildingBlocks
dotnet sln add src/ServiceDefaults src/Contracts src/BuildingBlocks
# Buổi 3 — AppHost
dotnet new aspire-apphost -n AppHost -o src/AppHost
dotnet sln add src/AppHost
# Buổi 4 — service + gateway
dotnet new web -n Catalog.Api -o src/Services/Catalog.Api
dotnet new web -n Gateway.Web -o src/Gateways/Gateway.Web
dotnet add src/AppHost reference src/Services/Catalog.Api src/Gateways/Gateway.Web
dotnet sln add src/Services/Catalog.Api src/Gateways/Gateway.Web
# Buổi 5 — test
dotnet new install xunit.v3.templates
dotnet new xunit3 -n Catalog.Tests -o tests/Catalog.Tests
dotnet sln add tests/Catalog.Tests
# Buổi 6–9 — frontend
(cd src && npx create-nx-workspace@21.5.0 Web --preset=apps --pm=npm --nxCloud=skip --interactive=false)
(cd src/Web && npx nx g @nx/angular:library --directory=libs/ui --name=ui --importPath=@shoplab/ui --standalone --skipTests)
(cd src/Web && npx nx g @nx/angular:library --directory=libs/auth --name=auth --importPath=@shoplab/auth --standalone --skipTests)
(cd src/Web && npx nx g @nx/angular:library --directory=libs/api-client --name=api-client --importPath=@shoplab/api-client --standalone --skipTests)
./tools/scaffold-angular-app.sh storefront 4200
./tools/scaffold-angular-app.sh admin 4300
```

Quy tắc chung: template chỉ tạo **khung**; sau mỗi lệnh `dotnet new`/`nx g`, thay nội dung file bằng bản đầy đủ trong buổi đó.
Repo dùng Central Package Management nên csproj không ghi version — `dotnet add package` tự ghi version vào `Directory.Packages.props`.
`tools/sln-sync.py` là cách thay thế `dotnet sln add`: quét mọi `*.csproj` và sinh lại `shoplab.slnx`.

---

## Tuần 1 — Backend chạy bằng một lệnh: AppHost, Keycloak, Catalog ping, Gateway, test

**Mục tiêu tuần**

- Repo có cấu hình build chung (Central Package Management, nullable, net10.0).
- AppHost dựng Postgres + Keycloak (import realm `shoplab`) + Catalog.Api + Gateway.Web.
- Lấy được token bằng `tools/get-token.sh`, gọi `/api/ping` qua gateway: không token 401, có token 200.
- `dotnet test` chạy smoke test với Postgres trong Testcontainers.

### Buổi 1 — Tạo repo và cấu hình build chung

**Việc cần làm**

1. Tạo thư mục, `git init`, các file cấu hình gốc.
2. `Directory.Build.props`: net10.0, nullable, implicit usings cho mọi project.
3. `Directory.Packages.props`: khai báo version mọi package một chỗ (csproj chỉ ghi tên package).
4. `tools/sln-sync.py`: sinh `shoplab.slnx` từ mọi `*.csproj` (thay cho `dotnet sln add` từng project).

**Lệnh**

```bash
mkdir shoplab && cd shoplab
git init

# File cấu hình gốc (tạo khung bằng template, rồi thay nội dung theo file bên dưới)
dotnet new gitignore
dotnet new editorconfig
dotnet new buildprops          # Directory.Build.props
dotnet new packagesprops       # Directory.Packages.props (Central Package Management)
dotnet new sln -n shoplab --format slnx

# Thư mục
mkdir -p src/{AppHost,ServiceDefaults,BuildingBlocks,Contracts,Web} src/Gateways/Gateway.Web src/Services/Catalog.Api          tests/Catalog.Tests docker/keycloak tools seed http docs/notes contracts/openapi
touch contracts/openapi/.gitkeep

# Tạo tools/sln-sync.py, README.md, docs/notes/README.md theo nội dung bên dưới
chmod +x tools/sln-sync.py
git add -A && git commit -m "s0: cấu hình gốc"
```

**File**

- `.gitignore`
- `.editorconfig`
- `Directory.Build.props`
- `Directory.Packages.props`
- `README.md`
- `contracts/openapi/.gitkeep`
- `docs/notes/README.md`
- `tools/sln-sync.py`

#### `Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Bật lên true khi repo đã build sạch cảnh báo trên máy bạn -->
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <InvariantGlobalization>false</InvariantGlobalization>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
</Project>
```

#### `Directory.Packages.props`

```xml
<Project>
  <!--
    Central Package Management cho toàn repo.
    Nếu restore báo không tìm thấy một version, đổi sang bản mới nhất cùng major trên nuget.org.
  -->
  <PropertyGroup>
    <AspireVersion>13.5.0</AspireVersion>
    <AspNetVersion>10.0.0</AspNetVersion>
    <EfVersion>10.0.0</EfVersion>
    <TestcontainersVersion>4.6.0</TestcontainersVersion>
  </PropertyGroup>
  <ItemGroup>
    <!-- Aspire hosting (AppHost) -->
    <PackageVersion Include="Aspire.Hosting.PostgreSQL" Version="$(AspireVersion)" />
    <PackageVersion Include="Aspire.Hosting.Redis" Version="$(AspireVersion)" />
    <PackageVersion Include="Aspire.Hosting.RabbitMQ" Version="$(AspireVersion)" />
    <PackageVersion Include="Aspire.Hosting.Keycloak" Version="$(AspireVersion)" />
    <PackageVersion Include="Aspire.Hosting.JavaScript" Version="$(AspireVersion)" />
    <PackageVersion Include="Aspire.Hosting.Testing" Version="$(AspireVersion)" />

    <!-- Aspire client integrations -->
    <PackageVersion Include="Aspire.Npgsql.EntityFrameworkCore.PostgreSQL" Version="$(AspireVersion)" />
    <PackageVersion Include="Aspire.StackExchange.Redis" Version="$(AspireVersion)" />
    <PackageVersion Include="Aspire.RabbitMQ.Client" Version="$(AspireVersion)" />

    <!-- ServiceDefaults -->
    <PackageVersion Include="Microsoft.Extensions.Http.Resilience" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.ServiceDiscovery" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.ServiceDiscovery.Yarp" Version="10.0.0" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.12.0" />
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.12.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.12.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.12.0" />

    <!-- ASP.NET Core -->
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="$(AspNetVersion)" />
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="$(AspNetVersion)" />
    <PackageVersion Include="Microsoft.Extensions.ApiDescription.Server" Version="$(AspNetVersion)" />
    <PackageVersion Include="Microsoft.AspNetCore.SignalR.StackExchangeRedis" Version="$(AspNetVersion)" />
    <PackageVersion Include="Scalar.AspNetCore" Version="2.6.0" />
    <PackageVersion Include="Yarp.ReverseProxy" Version="2.3.0" />

    <!-- Data -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="$(EfVersion)" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="$(EfVersion)" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="$(EfVersion)" />
    <PackageVersion Include="Npgsql" Version="10.0.0" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
    <PackageVersion Include="EFCore.NamingConventions" Version="10.0.0" />
    <PackageVersion Include="MongoDB.Driver" Version="3.4.0" />
    <PackageVersion Include="StackExchange.Redis" Version="2.8.58" />
    <PackageVersion Include="RabbitMQ.Client" Version="7.1.2" />

    <!-- Jobs, mail -->
    <PackageVersion Include="Hangfire.AspNetCore" Version="1.8.21" />
    <PackageVersion Include="Hangfire.PostgreSql" Version="1.20.12" />
    <PackageVersion Include="MailKit" Version="4.13.0" />
    <PackageVersion Include="Fluid.Core" Version="2.25.0" />

    <!-- Test -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit.v3" Version="3.0.1" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="$(AspNetVersion)" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.9.0" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="$(TestcontainersVersion)" />
    <PackageVersion Include="Testcontainers.Redis" Version="$(TestcontainersVersion)" />
    <PackageVersion Include="Testcontainers.RabbitMq" Version="$(TestcontainersVersion)" />
    <PackageVersion Include="Testcontainers.MongoDb" Version="$(TestcontainersVersion)" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
  </ItemGroup>
</Project>
```

#### `README.md`

````markdown
# ShopLab

Lab e-commerce microservices trên .NET 10 + Aspire 13 + Angular, đi từ một web bán hàng đơn giản (S0–S2)
tới event-driven (S3), khuyến mãi (S4), read model MongoDB (S5), flash sale (S6), POS offline (S7) và hardening (S8).
Mỗi bước là một tag `sN-done`; xem `git log --oneline` để đi theo đúng thứ tự.

## Cần cài

- .NET 10 SDK, Docker Desktop (hoặc Podman), Node 22 LTS, k6
- `dotnet tool install -g Aspire.Cli`, `dotnet tool install -g dotnet-ef`

## Chạy

```bash
cd src/Web && npm install && cd ../..
aspire run --project src/AppHost        # mở Aspire dashboard, mọi resource xanh là được
dotnet test                             # Testcontainers tự dựng hạ tầng cho test
```

| Địa chỉ | Dùng để |
| --- | --- |
| http://localhost:4200 | Storefront |
| http://localhost:4300 | Admin |
| http://localhost:5000 | Gateway.Web (mọi API đi qua đây) |
| http://localhost:8080 | Keycloak (admin / admin) |

Tài khoản mẫu (mật khẩu `Passw0rd!`): `admin@shoplab.local`, `an.nguyen`, `binh.tran`, `chi.le`, `manager.q1`, `cashier.q1`.

Lấy token để gọi API bằng curl: `TOKEN=$(./tools/get-token.sh an.nguyen)`.

## Thêm project mới

Sau `dotnet new …`, chạy `python3 tools/sln-sync.py` để cập nhật `shoplab.slnx`.
````

#### `contracts/openapi/.gitkeep`

```

```

#### `docs/notes/README.md`

```markdown
# Ghi chú theo bước

Cuối mỗi bước viết `sN.md` theo khung:

1. Đã làm gì (gạch đầu dòng ngắn)
2. Quyết định đã chọn và lý do
3. Chỗ đã vấp, mất bao lâu, cách gỡ
4. Nếu làm lại sẽ làm khác ở đâu
5. Trong dự án thật sẽ làm khác ở đâu
```

#### `tools/sln-sync.py`

```python
#!/usr/bin/env python3
"""Sinh lại shoplab.slnx từ mọi *.csproj trong src/ và tests/.
Tương đương chạy `dotnet sln add` cho từng project, dùng khi thêm service mới.
Chạy: python3 tools/sln-sync.py
"""
from pathlib import Path

root = Path(__file__).resolve().parent.parent
folders = {}
for proj in sorted(root.glob("src/**/*.csproj")) + sorted(root.glob("tests/**/*.csproj")):
    rel = proj.relative_to(root).as_posix()
    if "/node_modules/" in rel:
        continue
    parts = rel.split("/")
    if parts[0] == "tests":
        folder = "/tests/"
    elif parts[1] in ("Services", "Gateways"):
        folder = f"/src/{parts[1]}/"
    else:
        folder = "/src/"
    folders.setdefault(folder, []).append(rel)

lines = ["<Solution>"]
for folder in sorted(folders):
    lines.append(f'  <Folder Name="{folder}">')
    for rel in folders[folder]:
        lines.append(f'    <Project Path="{rel}" />')
    lines.append("  </Folder>")
lines.append("</Solution>")
(root / "shoplab.slnx").write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"Đã ghi {sum(len(v) for v in folders.values())} project vào shoplab.slnx")
```

**Kiểm tra cuối buổi**

```bash
git log --oneline   # 1 commit; dotnet sln list → chưa có project
```

### Buổi 2 — ServiceDefaults, Contracts, BuildingBlocks/Auth

**Việc cần làm**

1. `ServiceDefaults`: OpenTelemetry (trace/metric/log), health check `/health` `/alive`, service discovery, resilience cho HttpClient.
2. `BuildingBlocks/Auth`: JWT bearer trỏ Keycloak, đổi `realm_access.roles` thành role claim, policy dùng chung, `ICurrentUser`.
3. `TestUserAuthHandler`: chỉ bật ở môi trường `LoadTest` — header `X-Test-User` thay token (dùng cho k6 từ S2).
4. `Contracts`: project rỗng, chứa integration event từ S3.

**Lệnh**

```bash
# Template Aspire (cài một lần)
dotnet new install Aspire.ProjectTemplates

# 1. ServiceDefaults — OTel, health check, service discovery, resilience
dotnet new aspire-servicedefaults -n ServiceDefaults -o src/ServiceDefaults

# 2. Contracts — chỉ chứa record event (dùng từ S3)
dotnet new classlib -n Contracts -o src/Contracts
rm src/Contracts/Class1.cs

# 3. BuildingBlocks — code ứng dụng dùng chung (S0: Auth)
dotnet new classlib -n BuildingBlocks -o src/BuildingBlocks
rm src/BuildingBlocks/Class1.cs
mkdir -p src/BuildingBlocks/Auth
dotnet add src/BuildingBlocks package Microsoft.AspNetCore.Authentication.JwtBearer

# 4. Thêm vào solution
dotnet sln add src/ServiceDefaults src/Contracts src/BuildingBlocks

# 5. Thay nội dung 3 file .csproj và viết các file .cs theo phần "File" bên dưới
#    (template ghi Version trên PackageReference — với Central Package Management phải xoá,
#     version nằm ở Directory.Packages.props; TargetFramework/Nullable đã có ở Directory.Build.props)
dotnet build src/BuildingBlocks
```

**File**

- `src/ServiceDefaults/Extensions.cs`
- `src/ServiceDefaults/ServiceDefaults.csproj`
- `src/Contracts/Contracts.csproj`
- `src/BuildingBlocks/BuildingBlocks.csproj`
- `src/BuildingBlocks/Auth/AuthExtensions.cs`
- `src/BuildingBlocks/Auth/CurrentUser.cs`
- `src/BuildingBlocks/Auth/KeycloakRolesTransformation.cs`
- `src/BuildingBlocks/Auth/TestUserAuthHandler.cs`

#### `src/ServiceDefaults/Extensions.cs`

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Cấu hình dùng chung cho mọi service: OpenTelemetry, health check, service discovery, resilience.
/// Mọi Program.cs gọi builder.AddServiceDefaults() và app.MapDefaultEndpoints().
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>Tên meter/activity source của ShopLab, để OTel thu thập metric tự định nghĩa.</summary>
    public const string ShopLabTelemetryName = "ShopLab";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(ShopLabTelemetryName);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource(ShopLabTelemetryName)
                    .AddAspNetCoreInstrumentation(o =>
                        // không ghi trace cho health check
                        o.Filter = ctx =>
                            !ctx.Request.Path.StartsWithSegments(HealthEndpointPath) &&
                            !ctx.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();
        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Lab chạy local nên mở health endpoint ở mọi môi trường.
        app.MapHealthChecks(HealthEndpointPath);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });
        return app;
    }
}
```

#### `src/ServiceDefaults/ServiceDefaults.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsAspireSharedProject>true</IsAspireSharedProject>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" />
    <PackageReference Include="Microsoft.Extensions.ServiceDiscovery" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
  </ItemGroup>
</Project>
```

#### `src/Contracts/Contracts.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Chỉ chứa record event dùng chung. Không reference gì ngoài BCL. -->
</Project>
```

#### `src/BuildingBlocks/BuildingBlocks.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
  </ItemGroup>
</Project>
```

#### `src/BuildingBlocks/Auth/AuthExtensions.cs`

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BuildingBlocks.Auth;

public static class Policies
{
    public const string Authenticated = "authenticated";
    public const string Customer = "customer";
    public const string Admin = "admin";
    public const string Cashier = "cashier";
    public const string StoreManager = "store_manager";
    public const string StoreStaff = "store_staff";
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
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.RoleClaimType = ShopLabClaims.Role;
                // Keycloak chạy local qua http; issuer khớp vì mọi thứ đều dùng localhost:8080.
                options.TokenValidationParameters.ValidateIssuer = true;
            })
            // Chọn scheme theo request: header X-Test-User chỉ được dùng ở môi trường LoadTest (từ S2).
            .AddPolicyScheme(AuthSchemes.Smart, AuthSchemes.Smart, options =>
            {
                options.ForwardDefaultSelector = ctx =>
                    TestUserAuthHandler.IsEnabled(builder.Environment) &&
                    ctx.Request.Headers.ContainsKey(TestUserAuthHandler.HeaderName)
                        ? TestUserAuthHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme;
            });

        if (TestUserAuthHandler.IsEnabled(builder.Environment))
        {
            auth.AddScheme<AuthenticationSchemeOptions, TestUserAuthHandler>(TestUserAuthHandler.SchemeName, _ => { });
        }

        builder.Services.AddTransient<IClaimsTransformation, KeycloakRolesTransformation>();

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Authenticated, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.Customer, p => p.RequireRole(Roles.Customer))
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Policies.Cashier, p => p.RequireRole(Roles.Cashier, Roles.StoreManager))
            .AddPolicy(Policies.StoreManager, p => p.RequireRole(Roles.StoreManager))
            .AddPolicy(Policies.StoreStaff, p => p.RequireRole(Roles.Cashier, Roles.StoreManager));

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
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
```

#### `src/BuildingBlocks/Auth/CurrentUser.cs`

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Auth;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    /// <summary>Claim "sub" của Keycloak. Dùng làm CustomerId trên toàn hệ thống.</summary>
    Guid Id { get; }
    string? Username { get; }
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

    public string? Username => User?.FindFirst(ShopLabClaims.Username)?.Value;
    public string? Email => User?.FindFirst(ShopLabClaims.Email)?.Value;
    public string? FullName => User?.FindFirst(ShopLabClaims.Name)?.Value;

    public Guid? StoreId =>
        Guid.TryParse(User?.FindFirst(ShopLabClaims.StoreId)?.Value, out var id) ? id : null;

    public bool IsInRole(string role) => User?.IsInRole(role) == true;
}
```

#### `src/BuildingBlocks/Auth/KeycloakRolesTransformation.cs`

```csharp
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace BuildingBlocks.Auth;

/// <summary>
/// Keycloak đặt role của realm trong claim JSON "realm_access": {"roles": ["customer", ...]}.
/// Lớp này tách từng role ra thành claim "role" để RequireRole hoạt động.
/// </summary>
public sealed class KeycloakRolesTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            return Task.FromResult(principal);

        // Chạy một lần cho mỗi request; tránh thêm trùng nếu đã có.
        if (identity.HasClaim(c => c.Type == ShopLabClaims.Role))
            return Task.FromResult(principal);

        var realmAccess = identity.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(realmAccess))
            return Task.FromResult(principal);

        using var doc = JsonDocument.Parse(realmAccess);
        if (doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
        {
            foreach (var role in roles.EnumerateArray())
            {
                var value = role.GetString();
                if (!string.IsNullOrEmpty(value))
                    identity.AddClaim(new Claim(ShopLabClaims.Role, value));
            }
        }

        return Task.FromResult(principal);
    }
}
```

#### `src/BuildingBlocks/Auth/TestUserAuthHandler.cs`

```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth;

/// <summary>
/// Chỉ dùng cho môi trường LoadTest: header "X-Test-User: loadtest-42" thay cho token Keycloak,
/// để k6 không phải xin 5.000 token. Ở môi trường khác scheme này không được đăng ký.
/// </summary>
public sealed class TestUserAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestUser";
    public const string HeaderName = "X-Test-User";
    public const string RolesHeaderName = "X-Test-Roles";

    public static bool IsEnabled(IHostEnvironment env) => env.IsEnvironment("LoadTest");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || string.IsNullOrWhiteSpace(values))
            return Task.FromResult(AuthenticateResult.NoResult());

        var username = values.ToString();
        var roles = Request.Headers.TryGetValue(RolesHeaderName, out var r) && !string.IsNullOrWhiteSpace(r)
            ? r.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [Roles.Customer];

        var claims = new List<Claim>
        {
            new(ShopLabClaims.Subject, DeterministicId(username).ToString()),
            new(ShopLabClaims.Username, username),
            new(ShopLabClaims.Email, $"{username}@loadtest.local"),
            new(ShopLabClaims.Name, username),
        };
        claims.AddRange(roles.Select(role => new Claim(ShopLabClaims.Role, role)));

        var identity = new ClaimsIdentity(claims, SchemeName, ShopLabClaims.Username, ShopLabClaims.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>Cùng username luôn ra cùng Guid, để giỏ hàng và giới hạn mua của từng user ảo ổn định.</summary>
    public static Guid DeterministicId(string username)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("shoplab-loadtest:" + username));
        return new Guid(hash);
    }
}
```

**Kiểm tra cuối buổi**

```bash
dotnet build src/BuildingBlocks   # 0 error
```

### Buổi 3 — Keycloak realm và AppHost

**Việc cần làm**

1. Realm `shoplab`: role customer/admin/cashier/store_manager; client `gateway` (bearer-only), `storefront`/`admin`/`pos` (public + PKCE), `shoplab-cli` (direct grant, chỉ dev).
2. User mẫu có ID cố định (để seed dữ liệu ở S2 trỏ đúng): admin@shoplab.local, manager.q1, cashier.q1, an.nguyen, binh.tran, chi.le — mật khẩu `Passw0rd!`.
3. AppHost: Postgres + pgAdmin, Keycloak cổng cố định 8080 + import realm, các project.
4. `tools/get-token.sh` lấy token bằng client `shoplab-cli`.

**Lệnh**

```bash
# 1. AppHost (Aspire 13)
dotnet new aspire-apphost -n AppHost -o src/AppHost
dotnet add src/AppHost package Aspire.Hosting.PostgreSQL
dotnet add src/AppHost package Aspire.Hosting.Keycloak --prerelease
dotnet add src/AppHost package Aspire.Hosting.JavaScript
dotnet sln add src/AppHost

# 2. Realm Keycloak + script lấy token: tạo docker/keycloak/shoplab-realm.json, tools/get-token.sh
chmod +x tools/get-token.sh

# 3. Thay AppHost.csproj, AppHost.cs, ShopLabResourceExtensions.cs theo phần "File"
#    (AppHost.cs tham chiếu Catalog/Gateway/Web — các project đó tạo ở Buổi 4, Buổi 8–9;
#     tới lúc đó hãy comment các dòng chưa có project để build được)
dotnet build src/AppHost
```

**File**

- `docker/keycloak/shoplab-realm.json`
- `src/AppHost/AppHost.cs`
- `src/AppHost/AppHost.csproj`
- `src/AppHost/ShopLabResourceExtensions.cs`
- `tools/get-token.sh`

#### `docker/keycloak/shoplab-realm.json`

```json
{
  "realm": "shoplab",
  "enabled": true,
  "sslRequired": "none",
  "registrationAllowed": true,
  "loginWithEmailAllowed": true,
  "accessTokenLifespan": 1800,
  "roles": {
    "realm": [
      { "name": "customer", "description": "Khách mua hàng" },
      { "name": "admin", "description": "Quản trị hệ thống" },
      { "name": "cashier", "description": "Thu ngân tại cửa hàng" },
      { "name": "store_manager", "description": "Quản lý cửa hàng" }
    ]
  },
  "defaultRoles": ["customer"],
  "clientScopes": [
    {
      "name": "shoplab-api",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "true" },
      "protocolMappers": [
        {
          "name": "audience-gateway",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-audience-mapper",
          "config": {
            "included.client.audience": "gateway",
            "id.token.claim": "false",
            "access.token.claim": "true"
          }
        },
        {
          "name": "store-id",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-usermodel-attribute-mapper",
          "config": {
            "user.attribute": "store_id",
            "claim.name": "store_id",
            "jsonType.label": "String",
            "id.token.claim": "true",
            "access.token.claim": "true",
            "userinfo.token.claim": "true"
          }
        }
      ]
    }
  ],
  "clients": [
    {
      "clientId": "gateway",
      "name": "Audience cho API",
      "enabled": true,
      "bearerOnly": true,
      "publicClient": false
    },
    {
      "clientId": "storefront",
      "enabled": true,
      "publicClient": true,
      "standardFlowEnabled": true,
      "directAccessGrantsEnabled": false,
      "redirectUris": ["http://localhost:4200/*"],
      "webOrigins": ["http://localhost:4200"],
      "attributes": { "pkce.code.challenge.method": "S256", "post.logout.redirect.uris": "http://localhost:4200/*" },
      "defaultClientScopes": ["profile", "email", "roles", "shoplab-api"]
    },
    {
      "clientId": "admin",
      "enabled": true,
      "publicClient": true,
      "standardFlowEnabled": true,
      "directAccessGrantsEnabled": false,
      "redirectUris": ["http://localhost:4300/*"],
      "webOrigins": ["http://localhost:4300"],
      "attributes": { "pkce.code.challenge.method": "S256", "post.logout.redirect.uris": "http://localhost:4300/*" },
      "defaultClientScopes": ["profile", "email", "roles", "shoplab-api"]
    },
    {
      "clientId": "pos",
      "enabled": true,
      "publicClient": true,
      "standardFlowEnabled": true,
      "directAccessGrantsEnabled": false,
      "redirectUris": ["http://localhost:4400/*"],
      "webOrigins": ["http://localhost:4400"],
      "attributes": { "pkce.code.challenge.method": "S256", "post.logout.redirect.uris": "http://localhost:4400/*" },
      "defaultClientScopes": ["profile", "email", "roles", "shoplab-api"]
    },
    {
      "clientId": "shoplab-cli",
      "name": "Chỉ dùng ở máy dev: lấy token bằng username/password",
      "enabled": true,
      "publicClient": true,
      "standardFlowEnabled": false,
      "directAccessGrantsEnabled": true,
      "defaultClientScopes": ["profile", "email", "roles", "shoplab-api"]
    }
  ],
  "users": [
    {
      "id": "00000000-0000-0000-0000-0000000000a1",
      "username": "admin@shoplab.local",
      "email": "admin@shoplab.local",
      "emailVerified": true,
      "firstName": "Quản trị",
      "lastName": "ShopLab",
      "enabled": true,
      "credentials": [{ "type": "password", "value": "Passw0rd!", "temporary": false }],
      "realmRoles": ["admin", "customer"]
    },
    {
      "id": "00000000-0000-0000-0000-0000000000a2",
      "username": "manager.q1",
      "email": "manager.q1@shoplab.local",
      "emailVerified": true,
      "firstName": "Quản lý",
      "lastName": "Quận 1",
      "enabled": true,
      "attributes": { "store_id": ["5a000000-0000-0000-0000-000000000001"] },
      "credentials": [{ "type": "password", "value": "Passw0rd!", "temporary": false }],
      "realmRoles": ["store_manager"]
    },
    {
      "id": "00000000-0000-0000-0000-0000000000a3",
      "username": "cashier.q1",
      "email": "cashier.q1@shoplab.local",
      "emailVerified": true,
      "firstName": "Thu ngân",
      "lastName": "Quận 1",
      "enabled": true,
      "attributes": { "store_id": ["5a000000-0000-0000-0000-000000000001"] },
      "credentials": [{ "type": "password", "value": "Passw0rd!", "temporary": false }],
      "realmRoles": ["cashier"]
    },
    {
      "id": "00000000-0000-0000-0000-0000000000c1",
      "username": "an.nguyen",
      "email": "an.nguyen@shoplab.local",
      "emailVerified": true,
      "firstName": "An",
      "lastName": "Nguyễn",
      "enabled": true,
      "credentials": [{ "type": "password", "value": "Passw0rd!", "temporary": false }],
      "realmRoles": ["customer"]
    },
    {
      "id": "00000000-0000-0000-0000-0000000000c2",
      "username": "binh.tran",
      "email": "binh.tran@shoplab.local",
      "emailVerified": true,
      "firstName": "Bình",
      "lastName": "Trần",
      "enabled": true,
      "credentials": [{ "type": "password", "value": "Passw0rd!", "temporary": false }],
      "realmRoles": ["customer"]
    },
    {
      "id": "00000000-0000-0000-0000-0000000000c3",
      "username": "chi.le",
      "email": "chi.le@shoplab.local",
      "emailVerified": true,
      "firstName": "Chi",
      "lastName": "Lê",
      "enabled": true,
      "credentials": [{ "type": "password", "value": "Passw0rd!", "temporary": false }],
      "realmRoles": ["customer"]
    }
  ]
}
```

#### `src/AppHost/AppHost.cs`

```csharp
using AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// ---------- Hạ tầng (S0) ----------
var pg = builder.AddPostgres("pg")
    .WithDataVolume("shoplab-pg")
    .WithPgAdmin();

var keycloakAdmin = builder.AddParameter("keycloak-admin", "admin");
var keycloakPassword = builder.AddParameter("keycloak-password", "admin", secret: true);
var keycloak = builder.AddKeycloak("keycloak", 8080, keycloakAdmin, keycloakPassword)
    .WithDataVolume("shoplab-keycloak")
    .WithRealmImport("../../docker/keycloak");

// ---------- Service ----------
var catalog = builder.AddProject<Projects.Catalog_Api>("catalog")
    .WithKeycloak(keycloak);

var gateway = builder.AddProject<Projects.Gateway_Web>("gateway")
    .WithKeycloak(keycloak)
    .WithReference(catalog)
    .WithExternalHttpEndpoints();

// ---------- Frontend (Nx workspace ở src/Web) ----------
// Cổng cố định vì Keycloak đăng ký redirect URI theo cổng.
// Nếu bản Aspire của bạn đặt tên API khác, xem docs integration JavaScript và đổi lại dòng dưới.
builder.AddJavaScriptApp("storefront", "../Web", "start:storefront")
    .WithHttpEndpoint(port: 4200, isProxied: false)
    .WaitFor(gateway);

builder.AddJavaScriptApp("admin", "../Web", "start:admin")
    .WithHttpEndpoint(port: 4300, isProxied: false)
    .WaitFor(gateway);

builder.Build().Run();
```

#### `src/AppHost/AppHost.csproj`

```xml
<Project Sdk="Aspire.AppHost.Sdk/13.5.0">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UserSecretsId>shoplab-apphost</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.PostgreSQL" />
    <PackageReference Include="Aspire.Hosting.Keycloak" />
    <PackageReference Include="Aspire.Hosting.JavaScript" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Gateways\Gateway.Web\Gateway.Web.csproj" />
    <ProjectReference Include="..\Services\Catalog.Api\Catalog.Api.csproj" />
  </ItemGroup>
</Project>
```

#### `src/AppHost/ShopLabResourceExtensions.cs`

```csharp
namespace AppHost;

public static class ShopLabResourceExtensions
{
    /// <summary>Truyền authority của realm shoplab cho service và đợi Keycloak sẵn sàng.</summary>
    public static IResourceBuilder<ProjectResource> WithKeycloak(
        this IResourceBuilder<ProjectResource> project,
        IResourceBuilder<KeycloakResource> keycloak)
    {
        return project
            .WithEnvironment("Keycloak__Authority",
                ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/shoplab"))
            .WaitFor(keycloak);
    }
}
```

#### `tools/get-token.sh`

```bash
#!/usr/bin/env bash
# Lấy access token từ Keycloak bằng client dev "shoplab-cli".
# Dùng:  TOKEN=$(./tools/get-token.sh an.nguyen)
#        curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/ping
set -euo pipefail

USERNAME="${1:-an.nguyen}"
PASSWORD="${2:-Passw0rd!}"
KEYCLOAK="${KEYCLOAK_URL:-http://localhost:8080}"

curl -s -X POST "$KEYCLOAK/realms/shoplab/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=password" \
  -d "client_id=shoplab-cli" \
  -d "username=$USERNAME" \
  --data-urlencode "password=$PASSWORD" \
  -d "scope=openid" | python3 -c 'import sys, json; print(json.load(sys.stdin)["access_token"])'
```

**Kiểm tra cuối buổi**

```bash
chmod +x tools/get-token.sh
./tools/get-token.sh an.nguyen | cut -c1-40      # in ra đầu token eyJ...
```

### Buổi 4 — Catalog.Api ping và Gateway.Web (YARP)

**Việc cần làm**

1. Catalog.Api: `GET /ping` yêu cầu đăng nhập, trả tên service + username.
2. Gateway.Web: YARP đọc `yarp.json`, cluster trỏ `https+http://catalog` (service discovery của Aspire), route `/api/ping`.
3. Gateway chặn mọi đường dẫn chứa `/internal` (route nội bộ giữa các service, dùng từ S2).

**Lệnh**

```bash
# 1. Catalog.Api
dotnet new web -n Catalog.Api -o src/Services/Catalog.Api
dotnet add src/Services/Catalog.Api reference src/ServiceDefaults src/BuildingBlocks
dotnet add src/Services/Catalog.Api package Microsoft.AspNetCore.OpenApi
dotnet add src/Services/Catalog.Api package Microsoft.Extensions.ApiDescription.Server
dotnet add src/Services/Catalog.Api package Scalar.AspNetCore

# 2. Gateway.Web (YARP)
dotnet new web -n Gateway.Web -o src/Gateways/Gateway.Web
dotnet add src/Gateways/Gateway.Web reference src/ServiceDefaults src/BuildingBlocks
dotnet add src/Gateways/Gateway.Web package Yarp.ReverseProxy
dotnet add src/Gateways/Gateway.Web package Microsoft.Extensions.ServiceDiscovery.Yarp

# 3. AppHost chạy được 2 project này
dotnet add src/AppHost reference src/Services/Catalog.Api src/Gateways/Gateway.Web
dotnet sln add src/Services/Catalog.Api src/Gateways/Gateway.Web

# 4. Thay csproj, Program.cs, launchSettings.json, yarp.json theo phần "File"
#    (xoá appsettings*.json, *.http do template sinh nếu không dùng)
dotnet build
aspire run --project src/AppHost
```

**File**

- `src/Services/Catalog.Api/Catalog.Api.csproj`
- `src/Services/Catalog.Api/Program.cs`
- `src/Services/Catalog.Api/Properties/launchSettings.json`
- `src/Gateways/Gateway.Web/Gateway.Web.csproj`
- `src/Gateways/Gateway.Web/Program.cs`
- `src/Gateways/Gateway.Web/Properties/launchSettings.json`
- `src/Gateways/Gateway.Web/yarp.json`
- `http/ping.http`
- `shoplab.slnx`

#### `src/Services/Catalog.Api/Catalog.Api.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <!-- Sinh file OpenAPI lúc build: contracts/openapi/Catalog.Api.json -->
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../../contracts/openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOptions>--file-name Catalog.Api</OpenApiGenerateDocumentsOptions>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Microsoft.Extensions.ApiDescription.Server">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Scalar.AspNetCore" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\ServiceDefaults\ServiceDefaults.csproj" />
    <ProjectReference Include="..\..\BuildingBlocks\BuildingBlocks.csproj" />
  </ItemGroup>
</Project>
```

#### `src/Services/Catalog.Api/Program.cs`

```csharp
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
        user = user.Username,
        at = DateTimeOffset.UtcNow
    }))
    .RequireAuthorization(Policies.Authenticated);

app.Run();
```

#### `src/Services/Catalog.Api/Properties/launchSettings.json`

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5101",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

#### `src/Gateways/Gateway.Web/Gateway.Web.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Yarp.ReverseProxy" />
    <PackageReference Include="Microsoft.Extensions.ServiceDiscovery.Yarp" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\ServiceDefaults\ServiceDefaults.csproj" />
    <ProjectReference Include="..\..\BuildingBlocks\BuildingBlocks.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Update="yarp.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

#### `src/Gateways/Gateway.Web/Program.cs`

```csharp
using BuildingBlocks.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabAuth();

builder.Configuration.AddJsonFile("yarp.json", optional: false, reloadOnChange: true);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    // "https+http://catalog" được phân giải qua service discovery của Aspire
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();

// Header Authorization được YARP chuyển tiếp nguyên vẹn; service phía sau tự xác thực lại.
app.MapReverseProxy();

app.Run();
```

#### `src/Gateways/Gateway.Web/Properties/launchSettings.json`

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5000",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

#### `src/Gateways/Gateway.Web/yarp.json`

```json
{
  "ReverseProxy": {
    "Routes": {
      "ping": {
        "ClusterId": "catalog",
        "AuthorizationPolicy": "authenticated",
        "Match": { "Path": "/api/ping" },
        "Transforms": [ { "PathSet": "/ping" } ]
      },
      "catalog": {
        "ClusterId": "catalog",
        "AuthorizationPolicy": "Anonymous",
        "Match": { "Path": "/api/catalog/{**rest}" },
        "Transforms": [ { "PathPattern": "/{**rest}" } ]
      }
    },
    "Clusters": {
      "catalog": {
        "Destinations": { "d1": { "Address": "https+http://catalog" } }
      }
    }
  }
}
```

#### `http/ping.http`

```http
### Lấy token trước: TOKEN=$(./tools/get-token.sh an.nguyen) rồi dán vào biến dưới
@gateway = http://localhost:5000
@token = paste-token-here

### Không token → 401
GET {{gateway}}/api/ping

### Có token → 200
GET {{gateway}}/api/ping
Authorization: Bearer {{token}}
```

#### `shoplab.slnx`

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/AppHost/AppHost.csproj" />
    <Project Path="src/BuildingBlocks/BuildingBlocks.csproj" />
    <Project Path="src/Contracts/Contracts.csproj" />
    <Project Path="src/ServiceDefaults/ServiceDefaults.csproj" />
  </Folder>
  <Folder Name="/src/Gateways/">
    <Project Path="src/Gateways/Gateway.Web/Gateway.Web.csproj" />
  </Folder>
  <Folder Name="/src/Services/">
    <Project Path="src/Services/Catalog.Api/Catalog.Api.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Catalog.Tests/Catalog.Tests.csproj" />
  </Folder>
</Solution>
```

**Kiểm tra cuối buổi**

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5000/api/ping            # 401
TOKEN=$(./tools/get-token.sh an.nguyen)
curl -s -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/ping          # {"service":"catalog","user":"an.nguyen",...}
# Aspire dashboard → Traces: thấy span gateway → catalog
```

### Buổi 5 — Hạ tầng test: xUnit v3 + Testcontainers

**Việc cần làm**

1. `tests/Directory.Build.props`: mọi project test tự có xUnit v3, Shouldly, Testcontainers.
2. `PostgresFixture`: một container cho cả class test.
3. Smoke test kết nối Postgres — chứng minh Docker + Testcontainers chạy trên máy bạn.

**Lệnh**

```bash
# Template xUnit v3 (cài một lần)
dotnet new install xunit.v3.templates

dotnet new xunit3 -n Catalog.Tests -o tests/Catalog.Tests
rm tests/Catalog.Tests/UnitTest1.cs
dotnet add tests/Catalog.Tests reference src/Services/Catalog.Api
dotnet add tests/Catalog.Tests package Testcontainers.PostgreSql
dotnet add tests/Catalog.Tests package Npgsql
dotnet sln add tests/Catalog.Tests

# Tạo tests/Directory.Build.props (xUnit v3 + Shouldly chung cho mọi project test),
# thay Catalog.Tests.csproj và viết PostgresFixture.cs, SmokeTests.cs theo phần "File"
dotnet test tests/Catalog.Tests
```

**File**

- `tests/Directory.Build.props`
- `tests/Catalog.Tests/Catalog.Tests.csproj`
- `tests/Catalog.Tests/Infrastructure/PostgresFixture.cs`
- `tests/Catalog.Tests/SmokeTests.cs`

#### `tests/Directory.Build.props`

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <!-- Cấu hình chung cho mọi project test: xUnit v3 + Shouldly + Testcontainers -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Shouldly" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
    <Using Include="Shouldly" />
  </ItemGroup>
</Project>
```

#### `tests/Catalog.Tests/Catalog.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Npgsql" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Services\Catalog.Api\Catalog.Api.csproj" />
  </ItemGroup>
</Project>
```

#### `tests/Catalog.Tests/Infrastructure/PostgresFixture.cs`

```csharp
using Testcontainers.PostgreSql;

namespace Catalog.Tests.Infrastructure;

/// <summary>
/// Một container Postgres cho cả class test. Testcontainers tự kéo image, tự xoá container khi xong.
/// Cần Docker đang chạy trên máy.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("catalogdb")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
```

#### `tests/Catalog.Tests/SmokeTests.cs`

```csharp
using Catalog.Tests.Infrastructure;
using Npgsql;

namespace Catalog.Tests;

/// <summary>S0: chỉ chứng minh bộ test kết nối được Postgres thật trong container.</summary>
public sealed class SmokeTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Can_connect_to_postgres()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand("select 1", conn);

        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        result.ShouldBe(1);
    }
}
```

**Kiểm tra cuối buổi**

```bash
dotnet test   # Passed: 1
```

> Ở S1, PostgresFixture chuyển sang project dùng chung tests/TestKit để mọi service tái sử dụng.

**Xong tuần 1 khi:**

- [ ] `aspire run` mọi resource Running
- [ ] `/api/ping` 401/200 đúng
- [ ] `dotnet test` xanh

---

## Tuần 2 — Frontend: Nx workspace, storefront + admin đăng nhập Keycloak

**Mục tiêu tuần**

- Nx workspace ở `src/Web` với 2 app (storefront 4200, admin 4300) và 3 lib (auth, api-client, ui).
- Đăng nhập OIDC Authorization Code + PKCE, interceptor gắn token, guard theo role.
- AppHost chạy luôn 2 app Angular; storefront gọi `/api/ping` qua proxy → gateway.

### Buổi 6 — Nx workspace và lib ui

**Việc cần làm**

1. Workspace tối giản: Angular 20 (standalone, zoneless, signals), builder `@angular/build`.
2. `tsconfig.base.json` khai báo path `@shoplab/*` cho các lib.
3. `tools/scaffold-angular-app.sh` tạo khung một app (project.json, tsconfig, main.ts, proxy) — dùng lại cho app POS ở S7.
4. Lib ui: `VndPipe` định dạng tiền Việt.

**Lệnh**

```bash
cd src
# 1. Nx workspace rỗng ở src/Web
npx create-nx-workspace@21.5.0 Web --preset=apps --pm=npm --nxCloud=skip --interactive=false
cd Web

# 2. Angular + Nx plugin
npm i @angular/core@^20.3 @angular/common@^20.3 @angular/compiler@^20.3 @angular/forms@^20.3       @angular/router@^20.3 @angular/platform-browser@^20.3 rxjs@~7.8 tslib angular-oauth2-oidc@^20
npm i -D @nx/angular@21.5.0 @angular/build@^20.3 @angular/cli@^20.3 @angular/compiler-cli@^20.3 typescript@~5.9

# 3. Lib ui
npx nx g @nx/angular:library --directory=libs/ui --name=ui --importPath=@shoplab/ui --standalone --skipTests

# 4. Thay package.json, nx.json, tsconfig.base.json, .gitignore và file của libs/ui theo phần "File"
npm install
cd ../..

# 5. Script tạo app tối giản (dùng lại cho POS ở S7)
chmod +x tools/scaffold-angular-app.sh
```

**File**

- `src/Web/package.json`
- `src/Web/nx.json`
- `src/Web/tsconfig.base.json`
- `src/Web/.gitignore`
- `tools/scaffold-angular-app.sh`
- `src/Web/libs/ui/project.json`
- `src/Web/libs/ui/src/index.ts`
- `src/Web/libs/ui/src/lib/vnd.pipe.ts`

#### `src/Web/package.json`

```json
{
  "name": "shoplab-web",
  "version": "0.0.0",
  "private": true,
  "scripts": {
    "start:storefront": "nx serve storefront",
    "start:admin": "nx serve admin",
    "start:pos": "nx serve pos",
    "build:all": "nx run-many -t build",
    "test:all": "nx run-many -t test"
  },
  "dependencies": {
    "@angular/animations": "^20.3.0",
    "@angular/cdk": "^20.2.0",
    "@angular/common": "^20.3.0",
    "@angular/compiler": "^20.3.0",
    "@angular/core": "^20.3.0",
    "@angular/forms": "^20.3.0",
    "@angular/material": "^20.2.0",
    "@angular/platform-browser": "^20.3.0",
    "@angular/router": "^20.3.0",
    "@angular/service-worker": "^20.3.0",
    "@microsoft/signalr": "^9.0.6",
    "angular-oauth2-oidc": "^20.0.2",
    "dexie": "^4.2.0",
    "rxjs": "~7.8.2",
    "tslib": "^2.8.1",
    "zone.js": "~0.15.1"
  },
  "devDependencies": {
    "@angular/build": "^20.3.0",
    "@angular/cli": "^20.3.0",
    "@angular/compiler-cli": "^20.3.0",
    "@nx/angular": "21.5.0",
    "@nx/jest": "21.5.0",
    "@nx/js": "21.5.0",
    "@types/jest": "^30.0.0",
    "@types/node": "^22.18.0",
    "jest": "^30.1.0",
    "jest-environment-node": "^30.1.0",
    "nx": "21.5.0",
    "ts-jest": "^29.4.0",
    "typescript": "~5.9.2"
  }
}
```

#### `src/Web/nx.json`

```json
{
  "$schema": "./node_modules/nx/schemas/nx-schema.json",
  "defaultBase": "main",
  "namedInputs": {
    "default": ["{projectRoot}/**/*"],
    "production": ["default", "!{projectRoot}/**/*.spec.ts"]
  },
  "targetDefaults": {
    "build": { "dependsOn": ["^build"], "inputs": ["production", "^production"], "cache": true },
    "test": { "cache": true }
  }
}
```

#### `src/Web/tsconfig.base.json`

```json
{
  "compileOnSave": false,
  "compilerOptions": {
    "rootDir": ".",
    "baseUrl": ".",
    "target": "ES2022",
    "module": "ES2022",
    "moduleResolution": "bundler",
    "lib": ["ES2022", "dom"],
    "strict": true,
    "noImplicitOverride": true,
    "noPropertyAccessFromIndexSignature": false,
    "noImplicitReturns": true,
    "noFallthroughCasesInSwitch": true,
    "experimentalDecorators": true,
    "importHelpers": true,
    "skipLibCheck": true,
    "sourceMap": true,
    "declaration": false,
    "esModuleInterop": true,
    "paths": {
      "@shoplab/auth": ["libs/auth/src/index.ts"],
      "@shoplab/api-client": ["libs/api-client/src/index.ts"],
      "@shoplab/ui": ["libs/ui/src/index.ts"],
      "@shoplab/pricing": ["libs/pricing/src/index.ts"]
    }
  },
  "angularCompilerOptions": {
    "strictInjectionParameters": true,
    "strictInputAccessModifiers": true,
    "strictTemplates": true
  },
  "exclude": ["node_modules", "tmp"]
}
```

#### `src/Web/.gitignore`

```
node_modules
dist
tmp
.nx
.angular
```

#### `tools/scaffold-angular-app.sh`

```bash
#!/usr/bin/env bash
# Tạo khung một app Angular trong Nx workspace (thay cho `nx g @nx/angular:application`
# khi muốn giữ cấu hình tối giản, dùng builder @angular/build).
# Dùng: ./tools/scaffold-angular-app.sh storefront 4200
set -euo pipefail
NAME="$1"; PORT="$2"
ROOT="$(cd "$(dirname "$0")/.." && pwd)/src/Web/apps/$NAME"
mkdir -p "$ROOT/src/app" "$ROOT/public"

cat > "$ROOT/project.json" <<EOF
{
  "name": "$NAME",
  "\$schema": "../../node_modules/nx/schemas/project-schema.json",
  "projectType": "application",
  "sourceRoot": "apps/$NAME/src",
  "prefix": "app",
  "targets": {
    "build": {
      "executor": "@angular/build:application",
      "outputs": ["{options.outputPath}"],
      "options": {
        "outputPath": "dist/apps/$NAME",
        "browser": "apps/$NAME/src/main.ts",
        "index": "apps/$NAME/src/index.html",
        "tsConfig": "apps/$NAME/tsconfig.app.json",
        "inlineStyleLanguage": "scss",
        "assets": [{ "glob": "**/*", "input": "apps/$NAME/public" }],
        "styles": ["apps/$NAME/src/styles.scss"]
      },
      "configurations": {
        "production": { "outputHashing": "all" },
        "development": { "optimization": false, "extractLicenses": false, "sourceMap": true }
      },
      "defaultConfiguration": "production"
    },
    "serve": {
      "executor": "@angular/build:dev-server",
      "options": { "port": $PORT, "proxyConfig": "apps/$NAME/proxy.conf.json" },
      "configurations": {
        "production": { "buildTarget": "$NAME:build:production" },
        "development": { "buildTarget": "$NAME:build:development" }
      },
      "defaultConfiguration": "development"
    }
  }
}
EOF

cat > "$ROOT/tsconfig.json" <<'EOF'
{
  "extends": "../../tsconfig.base.json",
  "files": [],
  "include": [],
  "references": [{ "path": "./tsconfig.app.json" }]
}
EOF

cat > "$ROOT/tsconfig.app.json" <<'EOF'
{
  "extends": "../../tsconfig.base.json",
  "compilerOptions": { "outDir": "../../dist/out-tsc", "types": [] },
  "files": ["src/main.ts"],
  "include": ["src/**/*.d.ts", "src/**/*.ts"],
  "exclude": ["src/**/*.spec.ts"]
}
EOF

# Mọi request /api/* đi qua Gateway.Web (cổng 5000) nên không cần CORS.
cat > "$ROOT/proxy.conf.json" <<'EOF'
{
  "/api": { "target": "http://localhost:5000", "secure": false, "changeOrigin": true, "ws": true },
  "/hubs": { "target": "http://localhost:5000", "secure": false, "changeOrigin": true, "ws": true }
}
EOF
echo "created $ROOT"
```

#### `src/Web/libs/ui/project.json`

```json
{
  "name": "ui",
  "$schema": "../../node_modules/nx/schemas/project-schema.json",
  "projectType": "library",
  "sourceRoot": "libs/ui/src",
  "targets": {}
}
```

#### `src/Web/libs/ui/src/index.ts`

```typescript
export * from './lib/vnd.pipe';
```

#### `src/Web/libs/ui/src/lib/vnd.pipe.ts`

```typescript
import { Pipe, PipeTransform } from '@angular/core';

const formatter = new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND', maximumFractionDigits: 0 });

/** 1234000 → "1.234.000 ₫". Tiền trong hệ thống luôn là số nguyên VND. */
@Pipe({ name: 'vnd', standalone: true })
export class VndPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    if (value === null || value === undefined) return '';
    return formatter.format(value);
  }
}
```

**Kiểm tra cuối buổi**

```bash
cd src/Web && npx nx show projects   # ui (app sẽ có ở buổi sau)
```

### Buổi 7 — Lib auth và api-client

**Việc cần làm**

1. `AuthService` bọc angular-oauth2-oidc: login/logout, `user()` là signal, role đọc từ `realm_access`.
2. `authInterceptor` gắn Bearer cho request `/api/*`; `roleGuard('admin')`, `loggedInGuard`.
3. api-client viết tay (không generate) — mỗi service một file, bắt đầu bằng `SystemApi.ping()`.

**Lệnh**

```bash
cd src/Web
npx nx g @nx/angular:library --directory=libs/auth --name=auth --importPath=@shoplab/auth --standalone --skipTests
npx nx g @nx/angular:library --directory=libs/api-client --name=api-client --importPath=@shoplab/api-client --standalone --skipTests
# Thay nội dung theo phần "File" (generator tự thêm path @shoplab/* vào tsconfig.base.json)
cd ../..
```

**File**

- `src/Web/libs/auth/project.json`
- `src/Web/libs/auth/src/index.ts`
- `src/Web/libs/auth/src/lib/auth.config.ts`
- `src/Web/libs/auth/src/lib/auth.interceptor.ts`
- `src/Web/libs/auth/src/lib/auth.service.ts`
- `src/Web/libs/auth/src/lib/role.guard.ts`
- `src/Web/libs/api-client/project.json`
- `src/Web/libs/api-client/src/index.ts`
- `src/Web/libs/api-client/src/lib/system.api.ts`

#### `src/Web/libs/auth/project.json`

```json
{
  "name": "auth",
  "$schema": "../../node_modules/nx/schemas/project-schema.json",
  "projectType": "library",
  "sourceRoot": "libs/auth/src",
  "targets": {}
}
```

#### `src/Web/libs/auth/src/index.ts`

```typescript
export * from './lib/auth.service';
export * from './lib/auth.config';
export * from './lib/auth.interceptor';
export * from './lib/role.guard';
```

#### `src/Web/libs/auth/src/lib/auth.config.ts`

```typescript
import { EnvironmentProviders, inject, makeEnvironmentProviders, provideAppInitializer } from '@angular/core';
import { provideOAuthClient } from 'angular-oauth2-oidc';
import { AUTH_CLIENT_ID, AuthService } from './auth.service';

/**
 * Dùng trong app.config.ts: provideShopLabAuth('storefront').
 * clientId phải khớp client trong realm shoplab (storefront | admin | pos).
 */
export function provideShopLabAuth(clientId: string): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideOAuthClient(),
    { provide: AUTH_CLIENT_ID, useValue: clientId },
    provideAppInitializer(() => inject(AuthService).init()),
  ]);
}
```

#### `src/Web/libs/auth/src/lib/auth.interceptor.ts`

```typescript
import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from './auth.service';

/** Gắn Bearer token cho mọi request tới /api và /hubs (đi qua Gateway). */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(AuthService).accessToken;
  const isApi = req.url.startsWith('/api') || req.url.startsWith('/hubs');
  if (!token || !isApi) return next(req);
  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
```

#### `src/Web/libs/auth/src/lib/auth.service.ts`

```typescript
import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';

export const AUTH_CLIENT_ID = new InjectionToken<string>('AUTH_CLIENT_ID');
export const KEYCLOAK_ISSUER = 'http://localhost:8080/realms/shoplab';

export interface AuthUser {
  id: string;
  username: string;
  name: string;
  email?: string;
  roles: string[];
  storeId?: string;
}

/** Bọc angular-oauth2-oidc: code flow + PKCE với Keycloak, giữ user hiện tại trong một signal. */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly oauth = inject(OAuthService);
  private readonly clientId = inject(AUTH_CLIENT_ID);

  readonly user = signal<AuthUser | null>(null);
  readonly isLoggedIn = computed(() => this.user() !== null);

  async init(): Promise<void> {
    this.oauth.configure({
      issuer: KEYCLOAK_ISSUER,
      clientId: this.clientId,
      redirectUri: window.location.origin + '/',
      postLogoutRedirectUri: window.location.origin + '/',
      responseType: 'code',
      scope: 'openid profile email offline_access',
      requireHttps: false,
      useSilentRefresh: false,
      showDebugInformation: false,
    });
    this.oauth.setupAutomaticSilentRefresh();
    this.oauth.events.subscribe(() => this.refreshUser());
    try {
      await this.oauth.loadDiscoveryDocumentAndTryLogin();
    } catch (err) {
      // Keycloak chưa chạy: app vẫn mở được ở chế độ khách.
      console.warn('Không tải được cấu hình Keycloak', err);
    }
    this.refreshUser();
  }

  login(returnUrl = window.location.pathname): void {
    this.oauth.initCodeFlow(returnUrl);
  }

  logout(): void {
    this.oauth.logOut();
    this.user.set(null);
  }

  get accessToken(): string | null {
    return this.oauth.hasValidAccessToken() ? this.oauth.getAccessToken() : null;
  }

  /** state truyền lúc login (đường dẫn muốn quay lại). */
  get returnUrl(): string | null {
    const state = this.oauth.state;
    return state ? decodeURIComponent(state) : null;
  }

  hasRole(role: string): boolean {
    return this.user()?.roles.includes(role) ?? false;
  }

  hasAnyRole(roles: string[]): boolean {
    return roles.some((r) => this.hasRole(r));
  }

  private refreshUser(): void {
    const token = this.accessToken;
    if (!token) {
      this.user.set(null);
      return;
    }
    const claims = decodeJwt(token);
    this.user.set({
      id: claims['sub'],
      username: claims['preferred_username'],
      name: claims['name'] ?? claims['preferred_username'],
      email: claims['email'],
      roles: claims['realm_access']?.roles ?? [],
      storeId: claims['store_id'],
    });
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function decodeJwt(token: string): Record<string, any> {
  const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
  const json = decodeURIComponent(
    atob(payload)
      .split('')
      .map((c) => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2))
      .join(''),
  );
  return JSON.parse(json);
}
```

#### `src/Web/libs/auth/src/lib/role.guard.ts`

```typescript
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Chưa đăng nhập → chuyển sang Keycloak; đăng nhập rồi nhưng thiếu role → /forbidden. */
export function roleGuard(...roles: string[]): CanActivateFn {
  return (_route, state) => {
    const auth = inject(AuthService);
    if (!auth.isLoggedIn()) {
      auth.login(state.url);
      return false;
    }
    if (roles.length === 0 || auth.hasAnyRole(roles)) return true;
    return inject(Router).parseUrl('/forbidden');
  };
}

export const loggedInGuard: CanActivateFn = roleGuard();
```

#### `src/Web/libs/api-client/project.json`

```json
{
  "name": "api-client",
  "$schema": "../../node_modules/nx/schemas/project-schema.json",
  "projectType": "library",
  "sourceRoot": "libs/api-client/src",
  "targets": {}
}
```

#### `src/Web/libs/api-client/src/index.ts`

```typescript
export * from './lib/system.api';
```

#### `src/Web/libs/api-client/src/lib/system.api.ts`

```typescript
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';

export interface PingResponse {
  service: string;
  user: string | null;
  at: string;
}

/**
 * Client viết tay theo contract OpenAPI của service (file sinh ở contracts/openapi/*.json).
 * Mọi URL bắt đầu bằng /api để đi qua Gateway.Web.
 */
@Injectable({ providedIn: 'root' })
export class SystemApi {
  private readonly http = inject(HttpClient);

  ping() {
    return this.http.get<PingResponse>('/api/ping');
  }
}
```

### Buổi 8 — App storefront

**Việc cần làm**

1. Tạo khung bằng script rồi thay nội dung theo file bên dưới.
2. `proxy.conf.json`: `/api` → `http://localhost:5000` (gateway) để tránh CORS khi dev.
3. Trang `/dev/ping`: đăng nhập rồi bấm Ping.

**Lệnh**

```bash
# Cách 1 — script của repo (cấu hình tối giản, builder @angular/build):
./tools/scaffold-angular-app.sh storefront 4200
# Cách 2 — generator của Nx:
#   cd src/Web && npx nx g @nx/angular:application --directory=apps/storefront --name=storefront --standalone --routing --style=scss --bundler=esbuild --e2eTestRunner=none

# Thay nội dung các file trong apps/storefront theo phần "File", rồi:
cd src/Web && npx nx serve storefront
```

**File**

- `src/Web/apps/storefront/project.json`
- `src/Web/apps/storefront/proxy.conf.json`
- `src/Web/apps/storefront/src/app/app.component.ts`
- `src/Web/apps/storefront/src/app/app.config.ts`
- `src/Web/apps/storefront/src/app/app.routes.ts`
- `src/Web/apps/storefront/src/app/layout/header.component.ts`
- `src/Web/apps/storefront/src/app/pages/forbidden.component.ts`
- `src/Web/apps/storefront/src/app/pages/home/home.component.ts`
- `src/Web/apps/storefront/src/index.html`
- `src/Web/apps/storefront/src/main.ts`
- `src/Web/apps/storefront/src/styles.scss`
- `src/Web/apps/storefront/tsconfig.app.json`
- `src/Web/apps/storefront/tsconfig.json`

#### `src/Web/apps/storefront/project.json`

```json
{
  "name": "storefront",
  "$schema": "../../node_modules/nx/schemas/project-schema.json",
  "projectType": "application",
  "sourceRoot": "apps/storefront/src",
  "prefix": "app",
  "targets": {
    "build": {
      "executor": "@angular/build:application",
      "outputs": ["{options.outputPath}"],
      "options": {
        "outputPath": "dist/apps/storefront",
        "browser": "apps/storefront/src/main.ts",
        "index": "apps/storefront/src/index.html",
        "tsConfig": "apps/storefront/tsconfig.app.json",
        "inlineStyleLanguage": "scss",
        "assets": [{ "glob": "**/*", "input": "apps/storefront/public" }],
        "styles": ["apps/storefront/src/styles.scss"]
      },
      "configurations": {
        "production": { "outputHashing": "all" },
        "development": { "optimization": false, "extractLicenses": false, "sourceMap": true }
      },
      "defaultConfiguration": "production"
    },
    "serve": {
      "executor": "@angular/build:dev-server",
      "options": { "port": 4200, "proxyConfig": "apps/storefront/proxy.conf.json" },
      "configurations": {
        "production": { "buildTarget": "storefront:build:production" },
        "development": { "buildTarget": "storefront:build:development" }
      },
      "defaultConfiguration": "development"
    }
  }
}
```

#### `src/Web/apps/storefront/proxy.conf.json`

```json
{
  "/api": { "target": "http://localhost:5000", "secure": false, "changeOrigin": true, "ws": true },
  "/hubs": { "target": "http://localhost:5000", "secure": false, "changeOrigin": true, "ws": true }
}
```

#### `src/Web/apps/storefront/src/app/app.component.ts`

```typescript
import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { HeaderComponent } from './layout/header.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, HeaderComponent],
  template: `
    <app-header />
    <main class="container">
      <router-outlet />
    </main>
  `,
})
export class AppComponent {}
```

#### `src/Web/apps/storefront/src/app/app.config.ts`

```typescript
import { ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { authInterceptor, provideShopLabAuth } from '@shoplab/auth';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideShopLabAuth('storefront'),
  ],
};
```

#### `src/Web/apps/storefront/src/app/app.routes.ts`

```typescript
import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./pages/home/home.component').then((m) => m.HomeComponent) },
  { path: 'forbidden', loadComponent: () => import('./pages/forbidden.component').then((m) => m.ForbiddenComponent) },
  { path: '**', redirectTo: '' },
];
```

#### `src/Web/apps/storefront/src/app/layout/header.component.ts`

```typescript
import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '@shoplab/auth';

@Component({
  selector: 'app-header',
  imports: [RouterLink],
  template: `
    <header class="bar">
      <div class="container row">
        <a routerLink="/" class="logo">ShopLab</a>
        <span class="grow"></span>
        @if (auth.user(); as user) {
          <span class="muted">Xin chào, {{ user.name }}</span>
          <button (click)="auth.logout()">Đăng xuất</button>
        } @else {
          <button class="primary" (click)="auth.login()">Đăng nhập</button>
        }
      </div>
    </header>
  `,
  styles: `
    .bar { background: #fff; border-bottom: 1px solid var(--line); }
    .bar .container { padding-top: 10px; padding-bottom: 10px; }
    .logo { font-weight: 700; font-size: 20px; color: var(--ink); }
  `,
})
export class HeaderComponent {
  protected readonly auth = inject(AuthService);
}
```

#### `src/Web/apps/storefront/src/app/pages/forbidden.component.ts`

```typescript
import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-forbidden',
  imports: [RouterLink],
  template: `
    <h2>Không có quyền truy cập</h2>
    <p class="muted">Tài khoản của bạn không có quyền vào trang này.</p>
    <a routerLink="/">Về trang chủ</a>
  `,
})
export class ForbiddenComponent {}
```

#### `src/Web/apps/storefront/src/app/pages/home/home.component.ts`

```typescript
import { Component, inject, signal } from '@angular/core';
import { JsonPipe } from '@angular/common';
import { AuthService } from '@shoplab/auth';
import { PingResponse, SystemApi } from '@shoplab/api-client';

/** S0: trang chủ chỉ để thử đăng nhập và gọi /api/ping qua gateway. */
@Component({
  selector: 'app-home',
  imports: [JsonPipe],
  template: `
    <h1>ShopLab</h1>
    @if (auth.isLoggedIn()) {
      <button (click)="ping()">Gọi /api/ping</button>
      @if (result(); as r) { <pre class="card">{{ r | json }}</pre> }
      @if (error()) { <p class="error">{{ error() }}</p> }
    } @else {
      <p class="muted">Đăng nhập để thử gọi API qua gateway.</p>
    }
  `,
})
export class HomeComponent {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(SystemApi);
  protected readonly result = signal<PingResponse | null>(null);
  protected readonly error = signal<string | null>(null);

  ping(): void {
    this.api.ping().subscribe({
      next: (r) => {
        this.result.set(r);
        this.error.set(null);
        console.log('ping', r);
      },
      error: (e) => this.error.set(`Lỗi ${e.status}`),
    });
  }
}
```

#### `src/Web/apps/storefront/src/index.html`

```html
<!doctype html>
<html lang="vi">
  <head>
    <meta charset="utf-8" />
    <title>ShopLab</title>
    <base href="/" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
  </head>
  <body>
    <app-root></app-root>
  </body>
</html>
```

#### `src/Web/apps/storefront/src/main.ts`

```typescript
import { bootstrapApplication } from '@angular/platform-browser';
import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';

bootstrapApplication(AppComponent, appConfig).catch((err) => console.error(err));
```

#### `src/Web/apps/storefront/src/styles.scss`

```scss
:root {
  --bg: #f7f7f5;
  --card: #ffffff;
  --ink: #1d1d1b;
  --muted: #6b6b66;
  --line: #e3e2dd;
  --accent: #1f6feb;
  --danger: #c0392b;
  --ok: #1e8449;
  font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
  color: var(--ink);
  background: var(--bg);
}
* { box-sizing: border-box; }
body { margin: 0; }
a { color: var(--accent); text-decoration: none; }
button {
  font: inherit; border: 1px solid var(--line); background: var(--card); color: var(--ink);
  padding: 6px 14px; border-radius: 6px; cursor: pointer;
}
button.primary { background: var(--accent); color: #fff; border-color: var(--accent); }
button.danger { color: var(--danger); }
button:disabled { opacity: .5; cursor: default; }
input, select, textarea {
  font: inherit; padding: 6px 10px; border: 1px solid var(--line); border-radius: 6px; background: #fff;
}
.container { max-width: 1120px; margin: 0 auto; padding: 16px; }
.card { background: var(--card); border: 1px solid var(--line); border-radius: 8px; padding: 16px; }
.muted { color: var(--muted); }
.error { color: var(--danger); }
.ok { color: var(--ok); }
.row { display: flex; gap: 12px; align-items: center; }
.grow { flex: 1; }
table { width: 100%; border-collapse: collapse; background: var(--card); }
th, td { text-align: left; padding: 8px; border-bottom: 1px solid var(--line); }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 16px; }
.badge { display: inline-block; font-size: 12px; padding: 2px 8px; border-radius: 99px; background: #eef3fe; color: var(--accent); }
.badge.off { background: #f3f3f1; color: var(--muted); }
.form { display: grid; gap: 10px; max-width: 640px; }
.form label { display: grid; gap: 4px; font-size: 14px; }
```

#### `src/Web/apps/storefront/tsconfig.app.json`

```json
{
  "extends": "../../tsconfig.base.json",
  "compilerOptions": { "outDir": "../../dist/out-tsc", "types": [] },
  "files": ["src/main.ts"],
  "include": ["src/**/*.d.ts", "src/**/*.ts"],
  "exclude": ["src/**/*.spec.ts"]
}
```

#### `src/Web/apps/storefront/tsconfig.json`

```json
{
  "extends": "../../tsconfig.base.json",
  "files": [],
  "include": [],
  "references": [{ "path": "./tsconfig.app.json" }]
}
```

**Kiểm tra cuối buổi**

```bash
# http://localhost:4200/dev/ping → Đăng nhập an.nguyen → Ping → thấy JSON từ catalog
```

### Buổi 9 — App admin + chạy cả hệ thống qua AppHost

**Việc cần làm**

1. Admin: layout shell (menu trái), toàn bộ route bọc `roleGuard('admin')`, trang `/forbidden`.
2. AppHost dùng `AddJavaScriptApp` chạy `npm run start:storefront` / `start:admin` với cổng cố định (redirect URI của Keycloak đăng ký theo cổng).
3. Chạy gate S0.

**Lệnh**

```bash
./tools/scaffold-angular-app.sh admin 4300
# Thay nội dung các file trong apps/admin theo phần "File"

# Bỏ comment 2 dòng AddJavaScriptApp trong AppHost.cs (nếu đã comment ở Buổi 3), rồi:
aspire run --project src/AppHost
```

**File**

- `src/Web/apps/admin/project.json`
- `src/Web/apps/admin/proxy.conf.json`
- `src/Web/apps/admin/src/app/app.component.ts`
- `src/Web/apps/admin/src/app/app.config.ts`
- `src/Web/apps/admin/src/app/app.routes.ts`
- `src/Web/apps/admin/src/app/layout/shell.component.ts`
- `src/Web/apps/admin/src/app/pages/forbidden.component.ts`
- `src/Web/apps/admin/src/app/pages/welcome.component.ts`
- `src/Web/apps/admin/src/index.html`
- `src/Web/apps/admin/src/main.ts`
- `src/Web/apps/admin/src/styles.scss`
- `src/Web/apps/admin/tsconfig.app.json`
- `src/Web/apps/admin/tsconfig.json`

#### `src/Web/apps/admin/project.json`

```json
{
  "name": "admin",
  "$schema": "../../node_modules/nx/schemas/project-schema.json",
  "projectType": "application",
  "sourceRoot": "apps/admin/src",
  "prefix": "app",
  "targets": {
    "build": {
      "executor": "@angular/build:application",
      "outputs": ["{options.outputPath}"],
      "options": {
        "outputPath": "dist/apps/admin",
        "browser": "apps/admin/src/main.ts",
        "index": "apps/admin/src/index.html",
        "tsConfig": "apps/admin/tsconfig.app.json",
        "inlineStyleLanguage": "scss",
        "assets": [{ "glob": "**/*", "input": "apps/admin/public" }],
        "styles": ["apps/admin/src/styles.scss"]
      },
      "configurations": {
        "production": { "outputHashing": "all" },
        "development": { "optimization": false, "extractLicenses": false, "sourceMap": true }
      },
      "defaultConfiguration": "production"
    },
    "serve": {
      "executor": "@angular/build:dev-server",
      "options": { "port": 4300, "proxyConfig": "apps/admin/proxy.conf.json" },
      "configurations": {
        "production": { "buildTarget": "admin:build:production" },
        "development": { "buildTarget": "admin:build:development" }
      },
      "defaultConfiguration": "development"
    }
  }
}
```

#### `src/Web/apps/admin/proxy.conf.json`

```json
{
  "/api": { "target": "http://localhost:5000", "secure": false, "changeOrigin": true, "ws": true },
  "/hubs": { "target": "http://localhost:5000", "secure": false, "changeOrigin": true, "ws": true }
}
```

#### `src/Web/apps/admin/src/app/app.component.ts`

```typescript
import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: `<router-outlet />`,
})
export class AppComponent {}
```

#### `src/Web/apps/admin/src/app/app.config.ts`

```typescript
import { ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { authInterceptor, provideShopLabAuth } from '@shoplab/auth';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideShopLabAuth('admin'),
  ],
};
```

#### `src/Web/apps/admin/src/app/app.routes.ts`

```typescript
import { Routes } from '@angular/router';
import { roleGuard } from '@shoplab/auth';
import { ShellComponent } from './layout/shell.component';

export const routes: Routes = [
  { path: 'forbidden', loadComponent: () => import('./pages/forbidden.component').then((m) => m.ForbiddenComponent) },
  {
    path: '',
    component: ShellComponent,
    canActivate: [roleGuard('admin')],
    children: [
      { path: '', loadComponent: () => import('./pages/welcome.component').then((m) => m.WelcomeComponent) },
    ],
  },
  { path: '**', redirectTo: '' },
];
```

#### `src/Web/apps/admin/src/app/layout/shell.component.ts`

```typescript
import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '@shoplab/auth';

interface NavItem {
  path: string;
  label: string;
}

/** Layout admin: menu trái + nội dung. Mỗi bước thêm mục menu mới vào danh sách này. */
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="layout">
      <nav>
        <div class="logo">ShopLab Admin</div>
        @for (item of nav; track item.path) {
          <a [routerLink]="item.path" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: item.path === '/' }">{{ item.label }}</a>
        }
        <div class="grow"></div>
        <div class="muted small">{{ auth.user()?.name }}</div>
        <button (click)="auth.logout()">Đăng xuất</button>
      </nav>
      <section class="content"><router-outlet /></section>
    </div>
  `,
  styles: `
    .layout { display: grid; grid-template-columns: 220px 1fr; min-height: 100vh; }
    nav { display: flex; flex-direction: column; gap: 4px; padding: 16px; background: #fff; border-right: 1px solid var(--line); }
    nav a { padding: 6px 10px; border-radius: 6px; color: var(--ink); }
    nav a.active { background: #eef3fe; color: var(--accent); }
    .logo { font-weight: 700; margin-bottom: 12px; }
    .grow { flex: 1; }
    .small { font-size: 13px; }
    .content { padding: 24px; }
  `,
})
export class ShellComponent {
  protected readonly auth = inject(AuthService);
  protected readonly nav: NavItem[] = [{ path: '/', label: 'Tổng quan' }];
}
```

#### `src/Web/apps/admin/src/app/pages/forbidden.component.ts`

```typescript
import { Component, inject } from '@angular/core';
import { AuthService } from '@shoplab/auth';

@Component({
  selector: 'app-forbidden',
  template: `
    <div class="container">
      <h2>Không có quyền truy cập</h2>
      <p class="muted">Admin chỉ dành cho tài khoản có role <code>admin</code>.</p>
      <button (click)="auth.logout()">Đăng nhập bằng tài khoản khác</button>
    </div>
  `,
})
export class ForbiddenComponent {
  protected readonly auth = inject(AuthService);
}
```

#### `src/Web/apps/admin/src/app/pages/welcome.component.ts`

```typescript
import { Component, inject } from '@angular/core';
import { AuthService } from '@shoplab/auth';

@Component({
  selector: 'app-welcome',
  template: `
    <h1>Xin chào {{ auth.user()?.name }}</h1>
    <p class="muted">Chọn một mục ở menu bên trái.</p>
  `,
})
export class WelcomeComponent {
  protected readonly auth = inject(AuthService);
}
```

#### `src/Web/apps/admin/src/index.html`

```html
<!doctype html>
<html lang="vi">
  <head>
    <meta charset="utf-8" />
    <title>ShopLab Admin</title>
    <base href="/" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
  </head>
  <body>
    <app-root></app-root>
  </body>
</html>
```

#### `src/Web/apps/admin/src/main.ts`

```typescript
import { bootstrapApplication } from '@angular/platform-browser';
import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';

bootstrapApplication(AppComponent, appConfig).catch((err) => console.error(err));
```

#### `src/Web/apps/admin/src/styles.scss`

```scss
:root {
  --bg: #f7f7f5;
  --card: #ffffff;
  --ink: #1d1d1b;
  --muted: #6b6b66;
  --line: #e3e2dd;
  --accent: #1f6feb;
  --danger: #c0392b;
  --ok: #1e8449;
  font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
  color: var(--ink);
  background: var(--bg);
}
* { box-sizing: border-box; }
body { margin: 0; }
a { color: var(--accent); text-decoration: none; }
button {
  font: inherit; border: 1px solid var(--line); background: var(--card); color: var(--ink);
  padding: 6px 14px; border-radius: 6px; cursor: pointer;
}
button.primary { background: var(--accent); color: #fff; border-color: var(--accent); }
button.danger { color: var(--danger); }
button:disabled { opacity: .5; cursor: default; }
input, select, textarea {
  font: inherit; padding: 6px 10px; border: 1px solid var(--line); border-radius: 6px; background: #fff;
}
.container { max-width: 1120px; margin: 0 auto; padding: 16px; }
.card { background: var(--card); border: 1px solid var(--line); border-radius: 8px; padding: 16px; }
.muted { color: var(--muted); }
.error { color: var(--danger); }
.ok { color: var(--ok); }
.row { display: flex; gap: 12px; align-items: center; }
.grow { flex: 1; }
table { width: 100%; border-collapse: collapse; background: var(--card); }
th, td { text-align: left; padding: 8px; border-bottom: 1px solid var(--line); }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 16px; }
.badge { display: inline-block; font-size: 12px; padding: 2px 8px; border-radius: 99px; background: #eef3fe; color: var(--accent); }
.badge.off { background: #f3f3f1; color: var(--muted); }
.form { display: grid; gap: 10px; max-width: 640px; }
.form label { display: grid; gap: 4px; font-size: 14px; }
```

#### `src/Web/apps/admin/tsconfig.app.json`

```json
{
  "extends": "../../tsconfig.base.json",
  "compilerOptions": { "outDir": "../../dist/out-tsc", "types": [] },
  "files": ["src/main.ts"],
  "include": ["src/**/*.d.ts", "src/**/*.ts"],
  "exclude": ["src/**/*.spec.ts"]
}
```

#### `src/Web/apps/admin/tsconfig.json`

```json
{
  "extends": "../../tsconfig.base.json",
  "files": [],
  "include": [],
  "references": [{ "path": "./tsconfig.app.json" }]
}
```

**Kiểm tra cuối buổi**

```bash
# http://localhost:4300 → admin@shoplab.local → vào được
# đăng xuất, đăng nhập an.nguyen → bị chuyển /forbidden
git add -A && git commit -m "s0: nền móng" && git tag s0-done
```

**Xong tuần 2 khi:**

- [ ] Storefront đăng nhập + ping được
- [ ] Admin chặn user không có role admin
- [ ] Commit + tag `s0-done`

---

## Gate S0

| # | Kiểm tra | Cách làm |
| --- | --- | --- |
| 1 | Mọi resource xanh | Aspire dashboard |
| 2 | Ping không token 401, có token 200 | `http/ping.http` |
| 3 | Admin chặn role customer | đăng nhập an.nguyen vào :4300 |
| 4 | Test xanh | `dotnet test` |
