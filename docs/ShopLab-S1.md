# ShopLab — S1: Web catalog

Mục tiêu của S1: **một web bán hàng xem được** — danh mục, sản phẩm, biến thể; tìm kiếm không dấu; phân trang keyset;
admin thêm/sửa/xoá mềm/khôi phục. Đồng thời chốt các **quy ước dữ liệu dùng chung cho cả lab**:

- `Auditable`: `IsDeleted` (mặc định false), `IsActive`, `CreatedAt`, `UpdatedAt` kiểu `DateTimeOffset`, do `AuditInterceptor` gán.
- Xoá = xoá mềm; query thường tự lọc bản đã xoá; unique index có điều kiện `is_deleted = false` để tạo lại mã đã xoá.
- `xmin` của Postgres làm row version (optimistic concurrency).
- Lỗi nghiệp vụ = `AppException` có mã → ProblemDetails `{ code, title }`; vi phạm unique (23505) → 409 `DUPLICATE`.
- Endpoint theo vertical slice: mỗi tính năng một class `IEndpoint`.

**Thời lượng:** 2 tuần · 8 buổi (mỗi buổi ~2–3 giờ ngoài giờ làm). **File:** 59 tạo mới, 15 sửa, 1 xoá.

## Lịch

| Tuần | Mục tiêu | Buổi |
| --- | --- | --- |
| Tuần 1 | Quy ước dữ liệu chung + Catalog API đầy đủ | Buổi 1: Persistence: Auditable, AuditInterceptor, soft delete<br>Buổi 2: Lỗi có mã, IEndpoint, phân trang keyset, ApiExtensions<br>Buổi 3: Domain + DbContext của Catalog<br>Buổi 4: Category API, Program, AppHost database, gateway<br>Buổi 5: Product API: tìm kiếm keyset, CRUD, seed |
| Tuần 2 | Storefront + admin catalog, test tích hợp | Buổi 6: api-client catalog + storefront list/detail<br>Buổi 7: Admin category + product<br>Buổi 8: TestKit + test Catalog |

## Trước khi bắt đầu

```bash
git checkout s0-done -b s1
```

---

## Tuần 1 — Quy ước dữ liệu chung + Catalog API đầy đủ

**Mục tiêu tuần**

- BuildingBlocks có Persistence (audit, soft delete), Results (lỗi có mã), Endpoints, Paging dùng cho mọi service sau này.
- Catalog.Api: Category/Product/Variant trên Postgres, snake_case, seed 4 danh mục + 7 SKU.
- API công khai: list danh mục, tìm sản phẩm (q không dấu, category, cursor, size), chi tiết theo slug.
- API admin: CRUD, bật/tắt, xoá mềm, khôi phục.

### Buổi 1 — Persistence: Auditable, AuditInterceptor, soft delete

**Việc cần làm**

1. `Auditable` + `IHasCreatedAt` (bản ghi append-only chỉ có CreatedAt).
2. `AuditInterceptor`: Added → gán CreatedAt=UpdatedAt=now; Modified → UpdatedAt; Deleted → đổi thành Modified + IsDeleted=true. Dùng `TimeProvider` để test được.
3. `ApplySoftDeleteFilters()`: thêm query filter `!IsDeleted` cho mọi entity kế thừa Auditable.
4. `AddShopLabDbContext<T>(connName)` + `InitializeDatabaseAsync<T>(seed)`: Development tự migrate (hoặc EnsureCreated) rồi seed.

**File**

- `Directory.Packages.props` *(sửa)*
- `src/BuildingBlocks/BuildingBlocks.csproj` *(sửa)*
- `src/BuildingBlocks/Persistence/AuditInterceptor.cs`
- `src/BuildingBlocks/Persistence/Auditable.cs`
- `src/BuildingBlocks/Persistence/DbContextExtensions.cs`
- `src/BuildingBlocks/Persistence/SoftDeleteQueryFilter.cs`

#### `Directory.Packages.props` *(sửa — nội dung đầy đủ sau khi sửa)*

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
    <PackageVersion Include="xunit.v3.extensibility.core" Version="3.0.1" />
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

#### `src/BuildingBlocks/BuildingBlocks.csproj` *(sửa — nội dung đầy đủ sau khi sửa)*

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="EFCore.NamingConventions" />
    <PackageReference Include="Aspire.Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Scalar.AspNetCore" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\ServiceDefaults\ServiceDefaults.csproj" />
  </ItemGroup>
</Project>
```

#### `src/BuildingBlocks/Persistence/AuditInterceptor.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildingBlocks.Persistence;

/// <summary>
/// Gán CreatedAt/UpdatedAt và biến Remove() thành xoá mềm, ngay trước khi EF sinh SQL.
/// Bản ghi append-only (IHasCreatedAt nhưng không phải Auditable) bị chặn sửa và xoá.
/// </summary>
public sealed class AuditInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null) return;
        var now = clock.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries<IHasCreatedAt>())
        {
            if (entry.Entity is Auditable auditable)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        auditable.CreatedAt = now;
                        auditable.UpdatedAt = now;
                        break;
                    case EntityState.Modified:
                        auditable.UpdatedAt = now;
                        entry.Property(nameof(Auditable.CreatedAt)).IsModified = false;
                        break;
                    case EntityState.Deleted:
                        entry.State = EntityState.Modified;
                        auditable.IsDeleted = true;
                        auditable.UpdatedAt = now;
                        entry.Property(nameof(Auditable.CreatedAt)).IsModified = false;
                        break;
                }
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                    throw new InvalidOperationException(
                        $"{entry.Entity.GetType().Name} là bản ghi append-only: không được sửa hay xoá, hãy ghi bút toán đảo.");
            }
        }
    }
}
```

#### `src/BuildingBlocks/Persistence/Auditable.cs`

```csharp
namespace BuildingBlocks.Persistence;

/// <summary>Bản ghi append-only (sổ cái, redemption, cash movement…): chỉ có thời điểm tạo.</summary>
public interface IHasCreatedAt
{
    DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Base cho mọi entity có audit + soft delete.
/// - CreatedAt, UpdatedAt do AuditInterceptor gán; code nghiệp vụ không tự gán.
/// - IsDeleted: Remove() bị interceptor đổi thành xoá mềm; query thường không thấy bản đã xoá.
/// - IsActive: công tắc bật/tắt của admin, khác với Status (vòng đời nghiệp vụ).
/// </summary>
public abstract class Auditable : IHasCreatedAt
{
    public bool IsDeleted { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

#### `src/BuildingBlocks/Persistence/DbContextExtensions.cs`

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Persistence;

public static class DbContextExtensions
{
    /// <summary>
    /// Đăng ký DbContext trên Postgres: snake_case, interceptor (audit, outbox…) lấy từ DI,
    /// rồi Enrich bằng Aspire (health check, retry, tracing).
    /// connectionName là tên database khai báo trong AppHost (vd "catalogdb").
    /// </summary>
    public static IHostApplicationBuilder AddShopLabDbContext<TContext>(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? npgsql = null)
        where TContext : DbContext
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<ISaveChangesInterceptor, AuditInterceptor>());

        builder.Services.AddDbContext<TContext>((sp, options) =>
        {
            var cs = builder.Configuration.GetConnectionString(connectionName)
                     ?? throw new InvalidOperationException($"Thiếu connection string '{connectionName}'.");
            options.UseNpgsql(cs, o => npgsql?.Invoke(o))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetServices<ISaveChangesInterceptor>());
        });

        builder.EnrichNpgsqlDbContext<TContext>(settings => settings.DisableRetry = true);
        return builder;
    }

    /// <summary>
    /// Development/LoadTest: áp migration (hoặc EnsureCreated nếu chưa tạo migration nào), rồi seed.
    /// </summary>
    public static async Task InitializeDatabaseAsync<TContext>(
        this WebApplication app,
        Func<TContext, IServiceProvider, Task>? seed = null)
        where TContext : DbContext
    {
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("LoadTest"))
            return;

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInit");
        await db.MigrateOrCreateAsync(logger);
        if (seed is not null)
            await seed(db, scope.ServiceProvider);
    }

    public static async Task MigrateOrCreateAsync(this DbContext db, ILogger? logger = null)
    {
        if (db.Database.GetMigrations().Any())
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            // Chưa chạy `dotnet ef migrations add Init`: tạo schema thẳng từ model để vẫn chạy được.
            logger?.LogWarning("{Context} chưa có migration, dùng EnsureCreated.", db.GetType().Name);
            await db.Database.EnsureCreatedAsync();
        }
    }
}
```

#### `src/BuildingBlocks/Persistence/SoftDeleteQueryFilter.cs`

```csharp
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Persistence;

public static class SoftDeleteQueryFilter
{
    /// <summary>
    /// Gắn query filter "!IsDeleted" cho mọi entity kế thừa Auditable.
    /// Gọi ở cuối OnModelCreating. Muốn xem cả bản đã xoá: query.IgnoreQueryFilters().
    /// </summary>
    public static ModelBuilder ApplySoftDeleteFilters(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            if (!typeof(Auditable).IsAssignableFrom(clr) || entityType.BaseType is not null || entityType.IsOwned())
                continue;

            var param = Expression.Parameter(clr, "e");
            var body = Expression.Not(Expression.Property(param, nameof(Auditable.IsDeleted)));
            modelBuilder.Entity(clr).HasQueryFilter(Expression.Lambda(body, param));
            modelBuilder.Entity(clr).Property(nameof(Auditable.IsDeleted)).HasDefaultValue(false);
            // IsActive không đặt default ở DB: EF coi false là "chưa gán" và sẽ ghi default true thay vào.
        }
        return modelBuilder;
    }
}
```

**Kiểm tra cuối buổi**

```bash
dotnet build src/BuildingBlocks
```

### Buổi 2 — Lỗi có mã, IEndpoint, phân trang keyset, ApiExtensions

**Việc cần làm**

1. `AppErrors.cs`: NotFound/Conflict/BadRequest/Forbidden/Dependency — mỗi lỗi có `Code`.
2. `AppExceptionHandler` đổi exception thành ProblemDetails; Postgres 23505 → 409 DUPLICATE.
3. `IEndpoint` + `MapEndpoints(assembly)`: quét và map mọi slice.
4. `KeysetCursor` (key + id, base64url) + `PagedResult<T>`; `PagingDefaults.Clamp(size)`.
5. `AddShopLabApi()` / `UseShopLabApi()`: một dòng cho auth + lỗi + OpenAPI + validation + endpoint.

**File**

- `src/BuildingBlocks/Results/AppErrors.cs`
- `src/BuildingBlocks/Results/ProblemDetailsMapping.cs`
- `src/BuildingBlocks/Endpoints/IEndpoint.cs`
- `src/BuildingBlocks/Paging/KeysetCursor.cs`
- `src/BuildingBlocks/ApiExtensions.cs`

#### `src/BuildingBlocks/Results/AppErrors.cs`

```csharp
namespace BuildingBlocks.Results;

/// <summary>
/// Lỗi nghiệp vụ có mã. Ném ra từ handler/domain; ProblemDetailsMapping đổi thành HTTP status + ProblemDetails
/// với extension "code" để frontend hiện đúng thông điệp.
/// </summary>
public class AppException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class NotFoundException(string code, string message) : AppException(code, message, 404)
{
    public static NotFoundException For(string entity, object key) =>
        new($"{entity.ToUpperInvariant()}_NOT_FOUND", $"Không tìm thấy {entity} '{key}'.");
}

public sealed class ConflictException(string code, string message) : AppException(code, message, 409);

public sealed class BadRequestException(string code, string message) : AppException(code, message, 400);

public sealed class ForbiddenException(string code, string message) : AppException(code, message, 403);

/// <summary>Service phía sau không trả lời được (dùng ở S2 khi gọi HTTP đồng bộ).</summary>
public sealed class DependencyException(string code, string message) : AppException(code, message, 503);
```

#### `src/BuildingBlocks/Results/ProblemDetailsMapping.cs`

```csharp
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Results;

/// <summary>Đổi AppException (và vài exception hạ tầng) thành ProblemDetails thống nhất.</summary>
public sealed class AppExceptionHandler(IProblemDetailsService problemDetails, ILogger<AppExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, code, title) = exception switch
        {
            AppException app => (app.StatusCode, app.Code, app.Message),
            DbUpdateConcurrencyException => (409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa bị người khác sửa, hãy thử lại."),
            OperationCanceledException when ct.IsCancellationRequested => (499, "CLIENT_CLOSED", "Client đã huỷ request."),
            _ => (500, "INTERNAL_ERROR", "Lỗi không mong muốn.")
        };

        if (status >= 500)
            logger.LogError(exception, "Lỗi xử lý {Path}", http.Request.Path);

        http.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://shoplab.local/errors/{code.ToLowerInvariant()}",
            Instance = http.Request.Path
        };
        details.Extensions["code"] = code;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = details,
            Exception = exception
        });
    }
}
```

#### `src/BuildingBlocks/Endpoints/IEndpoint.cs`

```csharp
using System.Reflection;
using Microsoft.AspNetCore.Routing;

namespace BuildingBlocks.Endpoints;

/// <summary>
/// Một use case = một file trong Features/ = một class IEndpoint.
/// Class tự khai báo route của mình trong Map().
/// </summary>
public interface IEndpoint
{
    void Map(IEndpointRouteBuilder app);
}

public static class EndpointExtensions
{
    /// <summary>Quét assembly, tạo mọi IEndpoint (constructor không tham số) và gọi Map().</summary>
    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app, Assembly assembly)
    {
        var endpoints = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IEndpoint).IsAssignableFrom(t))
            .OrderBy(t => t.FullName)
            .Select(t => (IEndpoint)Activator.CreateInstance(t)!);

        foreach (var endpoint in endpoints)
            endpoint.Map(app);

        return app;
    }
}
```

#### `src/BuildingBlocks/Paging/KeysetCursor.cs`

```csharp
using System.Text;
using System.Text.Json;

namespace BuildingBlocks.Paging;

/// <summary>
/// Cursor cho phân trang keyset: vị trí (sortKey, id) của bản ghi cuối trang trước, mã hoá base64url.
/// Trang sau = WHERE (sort_key, id) > (@key, @id) ORDER BY sort_key, id LIMIT n.
/// Không dùng OFFSET để trang sâu không chậm dần và không lặp/sót khi có bản ghi mới chèn vào.
/// </summary>
public sealed record KeysetCursor(string Key, Guid Id)
{
    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static KeysetCursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<KeysetCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        }
        catch (Exception)
        {
            throw new Results.BadRequestException("INVALID_CURSOR", "Cursor không hợp lệ.");
        }
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor);

public static class PagingDefaults
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    public static int Clamp(int? size) => Math.Clamp(size ?? DefaultSize, 1, MaxSize);
}
```

#### `src/BuildingBlocks/ApiExtensions.cs`

```csharp
using System.Reflection;
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;

namespace BuildingBlocks;

public static class ApiExtensions
{
    /// <summary>Mọi service HTTP gọi một lần: ServiceDefaults nằm ngoài, cái này lo auth, lỗi, OpenAPI, validation.</summary>
    public static IHostApplicationBuilder AddShopLabApi(this IHostApplicationBuilder builder)
    {
        builder.AddShopLabAuth();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<AppExceptionHandler>();
        builder.Services.AddValidation(); // validation có sẵn của .NET 10 cho minimal API (DataAnnotations)
        builder.Services.AddOpenApi();
        return builder;
    }

    public static WebApplication UseShopLabApi(this WebApplication app, Assembly endpointsAssembly)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        app.MapDefaultEndpoints();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEndpoints(endpointsAssembly);
        return app;
    }
}
```

**Kiểm tra cuối buổi**

```bash
dotnet build src/BuildingBlocks
```

### Buổi 3 — Domain + DbContext của Catalog

**Việc cần làm**

1. `Category` (slug unique), `Product` (Status Draft/Active/Archived, `SearchText` không dấu để tìm), `ProductVariant` (SKU unique, giá `long` ₫).
2. `Slugs.From()` bỏ dấu tiếng Việt (đ → d).
3. Cấu hình EF: schema `catalog`, unique index có filter `is_deleted = false`, `xmin` row version.
4. `CatalogDbContextFactory` cho `dotnet ef` lúc design time; `tools/ef-migrations.sh` tạo migration cho service bất kỳ.

**Lệnh**

```bash
chmod +x tools/ef-migrations.sh
./tools/ef-migrations.sh Catalog Initial     # tuỳ chọn — không có migration thì Development dùng EnsureCreated
```

**File**

- `src/Services/Catalog.Api/Catalog.Api.csproj` *(sửa)*
- `src/Services/Catalog.Api/ApiMarker.cs`
- `src/Services/Catalog.Api/Domain/Category.cs`
- `src/Services/Catalog.Api/Domain/Product.cs`
- `src/Services/Catalog.Api/Domain/ProductVariant.cs`
- `src/Services/Catalog.Api/Domain/Slugs.cs`
- `src/Services/Catalog.Api/Data/CatalogDbContext.cs`
- `src/Services/Catalog.Api/Data/CatalogDbContextFactory.cs`
- `src/Services/Catalog.Api/Data/Configurations/CatalogConfigurations.cs`
- `tools/ef-migrations.sh`

#### `src/Services/Catalog.Api/Catalog.Api.csproj` *(sửa — nội dung đầy đủ sau khi sửa)*

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <!-- Sinh file OpenAPI lúc build: contracts/openapi/Catalog.Api.json -->
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../../contracts/openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOptions>--file-name Catalog.Api</OpenApiGenerateDocumentsOptions>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.ApiDescription.Server">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\ServiceDefaults\ServiceDefaults.csproj" />
    <ProjectReference Include="..\..\BuildingBlocks\BuildingBlocks.csproj" />
    <ProjectReference Include="..\..\Contracts\Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- seed/*.json ở gốc repo được copy vào bin/seed -->
    <None Include="..\..\..\seed\**\*.json" Link="seed\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

#### `src/Services/Catalog.Api/ApiMarker.cs`

```csharp
namespace Catalog.Api;

/// <summary>Kiểu đánh dấu assembly: dùng cho WebApplicationFactory&lt;ApiMarker&gt; trong test và quét endpoint.</summary>
public sealed class ApiMarker;
```

#### `src/Services/Catalog.Api/Domain/Category.cs`

```csharp
using BuildingBlocks.Persistence;

namespace Catalog.Api.Domain;

public sealed class Category : Auditable
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Name { get; private set; } = default!;
    public string Slug { get; private set; } = default!;
    public Guid? ParentId { get; private set; }

    private Category() { }

    public static Category Create(string name, string slug, Guid? parentId = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.CreateVersion7(),
        Name = name.Trim(),
        Slug = Slugs.Normalize(slug),
        ParentId = parentId
    };

    public void Update(string name, string slug, Guid? parentId)
    {
        if (parentId == Id) throw new BuildingBlocks.Results.BadRequestException("CATEGORY_SELF_PARENT", "Category không thể là cha của chính nó.");
        Name = name.Trim();
        Slug = Slugs.Normalize(slug);
        ParentId = parentId;
    }
}
```

#### `src/Services/Catalog.Api/Domain/Product.cs`

```csharp
using BuildingBlocks.Persistence;
using BuildingBlocks.Results;

namespace Catalog.Api.Domain;

public enum ProductStatus
{
    Draft = 0,
    Active = 1,
    Archived = 2
}

public sealed class Product : Auditable
{
    private readonly List<ProductVariant> _variants = [];

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Name { get; private set; } = default!;
    public string Slug { get; private set; } = default!;
    public string Description { get; private set; } = "";
    public Guid CategoryId { get; private set; }
    public string Brand { get; private set; } = "";
    public ProductStatus Status { get; private set; } = ProductStatus.Draft;

    /// <summary>Tên + thương hiệu viết thường, bỏ dấu; dùng cho tìm kiếm "binh" ra "Bình giữ nhiệt".</summary>
    public string SearchText { get; private set; } = "";

    /// <summary>Tăng mỗi lần ghi (kể cả khi chỉ đổi variant). Projection sang Mongo dùng để bỏ qua event cũ.</summary>
    public long Version { get; private set; }

    public IReadOnlyList<ProductVariant> Variants => _variants;

    private Product() { }

    public static Product Create(string name, string slug, string description, Guid categoryId, string brand, ProductStatus status)
    {
        var p = new Product();
        p.Update(name, slug, description, categoryId, brand, status);
        return p;
    }

    public void Update(string name, string slug, string description, Guid categoryId, string brand, ProductStatus status)
    {
        Name = name.Trim();
        Slug = Slugs.Normalize(string.IsNullOrWhiteSpace(slug) ? name : slug);
        Description = description.Trim();
        CategoryId = categoryId;
        Brand = brand.Trim();
        Status = status;
        SearchText = Slugs.RemoveDiacritics($"{Name} {Brand}").ToLowerInvariant();
        Touch();
    }

    /// <summary>
    /// Đồng bộ danh sách variant theo SKU: SKU mới thì thêm, SKU có sẵn thì sửa, SKU không còn trong danh sách thì xoá mềm.
    /// Trả về các variant cần Remove() khỏi DbContext.
    /// </summary>
    public IReadOnlyList<ProductVariant> SyncVariants(IEnumerable<VariantInput> inputs)
    {
        var list = inputs.ToList();
        if (list.Count == 0) throw new BadRequestException("PRODUCT_NO_VARIANT", "Sản phẩm phải có ít nhất một variant.");
        var dupe = list.GroupBy(v => v.Sku.Trim().ToUpperInvariant()).FirstOrDefault(g => g.Count() > 1);
        if (dupe is not null) throw new BadRequestException("DUPLICATE_SKU", $"SKU {dupe.Key} bị lặp.");

        foreach (var input in list)
        {
            var sku = input.Sku.Trim().ToUpperInvariant();
            var existing = _variants.FirstOrDefault(v => v.Sku == sku && !v.IsDeleted);
            if (existing is null)
                _variants.Add(ProductVariant.Create(Id, sku, input));
            else
                existing.Update(input);
        }

        var keep = list.Select(v => v.Sku.Trim().ToUpperInvariant()).ToHashSet();
        var removed = _variants.Where(v => !v.IsDeleted && !keep.Contains(v.Sku)).ToList();
        Touch();
        return removed;
    }

    public void SetActive(bool active)
    {
        IsActive = active;
        Touch();
    }

    public void Restore()
    {
        IsDeleted = false;
        Touch();
    }

    public void MarkDeleted() => Touch();

    public void Touch() => Version++;

    /// <summary>Sản phẩm khách nhìn thấy: Active, đang bật và chưa xoá.</summary>
    public bool IsVisible => Status == ProductStatus.Active && IsActive && !IsDeleted;
}

public sealed record VariantInput(
    string Sku,
    string Name,
    long Price,
    long? CompareAtPrice,
    string? Barcode,
    Dictionary<string, string>? Attributes);
```

#### `src/Services/Catalog.Api/Domain/ProductVariant.cs`

```csharp
using BuildingBlocks.Persistence;
using BuildingBlocks.Results;

namespace Catalog.Api.Domain;

public sealed class ProductVariant : Auditable
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public long Price { get; private set; }
    public long? CompareAtPrice { get; private set; }
    public string? Barcode { get; private set; }
    public Dictionary<string, string> Attributes { get; private set; } = [];

    /// <summary>Projection từ event StockChanged của Inventory (từ S3). Trước đó mặc định true.</summary>
    public bool InStock { get; private set; } = true;

    public Product Product { get; private set; } = default!;

    private ProductVariant() { }

    internal static ProductVariant Create(Guid productId, string sku, VariantInput input)
    {
        var v = new ProductVariant { ProductId = productId, Sku = sku };
        v.Update(input);
        return v;
    }

    internal void Update(VariantInput input)
    {
        if (input.Price <= 0) throw new BadRequestException("INVALID_PRICE", $"Giá của {Sku} phải lớn hơn 0.");
        if (input.CompareAtPrice is { } compare && compare < input.Price)
            throw new BadRequestException("INVALID_COMPARE_PRICE", $"Giá gạch của {Sku} phải lớn hơn hoặc bằng giá bán.");

        Name = input.Name.Trim();
        Price = input.Price;
        CompareAtPrice = input.CompareAtPrice;
        Barcode = string.IsNullOrWhiteSpace(input.Barcode) ? null : input.Barcode.Trim();
        Attributes = input.Attributes ?? [];
    }

    public bool SetInStock(bool inStock)
    {
        if (InStock == inStock) return false;
        InStock = inStock;
        return true;
    }
}
```

#### `src/Services/Catalog.Api/Domain/Slugs.cs`

```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Catalog.Api.Domain;

public static partial class Slugs
{
    /// <summary>"Bình giữ nhiệt 500 ml" → "binh-giu-nhiet-500-ml".</summary>
    public static string Normalize(string input)
    {
        var text = RemoveDiacritics(input).ToLowerInvariant();
        text = NonSlugChars().Replace(text, "-");
        return MultiDash().Replace(text, "-").Trim('-');
    }

    /// <summary>Bỏ dấu tiếng Việt, gồm cả đ/Đ (không phải tổ hợp dấu nên phải thay riêng).</summary>
    public static string RemoveDiacritics(string input)
    {
        var normalized = input.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiDash();
}
```

#### `src/Services/Catalog.Api/Data/CatalogDbContext.cs`

```csharp
using BuildingBlocks.Persistence;
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Data;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> Variants => Set<ProductVariant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
        modelBuilder.ApplySoftDeleteFilters();
    }
}
```

#### `src/Services/Catalog.Api/Data/CatalogDbContextFactory.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Catalog.Api.Data;

/// <summary>
/// Chỉ dùng cho `dotnet ef migrations add …` (lúc đó không có AppHost truyền connection string).
/// Migration không cần kết nối thật, nên connection string ở đây chỉ để EF dựng model.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=catalogdb;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CatalogDbContext(options);
    }
}
```

#### `src/Services/Catalog.Api/Data/Configurations/CatalogConfigurations.cs`

```csharp
using System.Text.Json;
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Api.Data.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("categories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(200).IsRequired();
        // Unique chỉ trên bản chưa xoá: xoá mềm rồi tạo lại cùng slug không bị lỗi trùng.
        b.HasIndex(x => x.Slug).IsUnique().HasFilter("is_deleted = false");
    }
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("products");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(300).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(300).IsRequired();
        b.Property(x => x.Brand).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.Slug).IsUnique().HasFilter("is_deleted = false");
        // Phục vụ SearchProducts: lọc theo category rồi sắp theo tên + id (keyset)
        b.HasIndex(x => new { x.CategoryId, x.Name, x.Id });
        b.HasIndex(x => new { x.Name, x.Id });
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Variants).WithOne(v => v.Product).HasForeignKey(v => v.ProductId);
        b.Navigation(x => x.Variants).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Ignore(x => x.IsVisible);
    }
}

public sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<ProductVariant> b)
    {
        b.ToTable("product_variants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Sku).HasMaxLength(64).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Barcode).HasMaxLength(64);
        b.HasIndex(x => x.Sku).IsUnique().HasFilter("is_deleted = false");
        b.HasIndex(x => x.Barcode).HasFilter("barcode IS NOT NULL");

        // Thuộc tính lưu jsonb; cần ValueComparer để EF phát hiện thay đổi trong dictionary.
        b.Property(x => x.Attributes)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, Json) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(),
                    v => new Dictionary<string, string>(v)));
    }
}
```

#### `tools/ef-migrations.sh`

```bash
#!/usr/bin/env bash
# Tạo migration cho một hoặc mọi service có DbContext.
#   ./tools/ef-migrations.sh Init                 # mọi service
#   ./tools/ef-migrations.sh AddOutbox Inventory  # một service
# Service chưa có migration vẫn chạy được (EnsureCreated), nhưng nên tạo migration ngay từ đầu
# để thấy rõ schema thay đổi qua từng bước.
set -euo pipefail
NAME="${1:?Tên migration, vd Init}"
ONLY="${2:-}"
SERVICES=(Catalog Customer Inventory Discount Ordering Payment FlashSale Store)
for svc in "${SERVICES[@]}"; do
  [[ -n "$ONLY" && "$ONLY" != "$svc" ]] && continue
  proj="src/Services/$svc.Api"
  [[ -d "$proj" ]] || continue
  echo "==> $svc: $NAME"
  dotnet ef migrations add "$NAME" -p "$proj" -o Data/Migrations
done
```

**Kiểm tra cuối buổi**

```bash
dotnet build src/Services/Catalog.Api
```

### Buổi 4 — Category API, Program, AppHost database, gateway

**Việc cần làm**

1. Slice Categories: list (công khai), create/update/delete (admin).
2. Program.cs chuyển sang `AddShopLabApi` + `AddShopLabDbContext<CatalogDbContext>("catalogdb")`.
3. AppHost: `pg.AddDatabase("catalogdb")` và `.WithReference(catalogDb)`.
4. Gateway: route `/api/catalog/**` (ẩn danh) và `/api/admin/catalog/**` (cần đăng nhập, service tự kiểm role).

**File**

- `src/Services/Catalog.Api/Features/Ping.cs`
- `src/Services/Catalog.Api/Features/Categories/CategoryDto.cs`
- `src/Services/Catalog.Api/Features/Categories/CreateCategory.cs`
- `src/Services/Catalog.Api/Features/Categories/DeleteCategory.cs`
- `src/Services/Catalog.Api/Features/Categories/ListCategories.cs`
- `src/Services/Catalog.Api/Features/Categories/UpdateCategory.cs`
- `src/Services/Catalog.Api/Program.cs` *(sửa)*
- `src/AppHost/AppHost.cs` *(sửa)*
- `src/Gateways/Gateway.Web/yarp.json` *(sửa)*
- `shoplab.slnx` *(sửa)*

#### `src/Services/Catalog.Api/Features/Ping.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;

namespace Catalog.Api.Features;

public sealed class Ping : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/ping", (ICurrentUser user) => Results.Ok(new
            {
                service = "catalog",
                user = user.Username,
                at = DateTimeOffset.UtcNow
            }))
            .RequireAuthorization(Policies.Authenticated)
            .WithTags("System");
}
```

#### `src/Services/Catalog.Api/Features/Categories/CategoryDto.cs`

```csharp
using Catalog.Api.Domain;

namespace Catalog.Api.Features.Categories;

public sealed record CategoryDto(Guid Id, string Name, string Slug, Guid? ParentId, bool IsActive);

public static class CategoryMapping
{
    public static CategoryDto ToDto(this Category c) => new(c.Id, c.Name, c.Slug, c.ParentId, c.IsActive);
}
```

#### `src/Services/Catalog.Api/Features/Categories/CreateCategory.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Categories;

public sealed record SaveCategoryRequest(
    [property: Required, StringLength(200, MinimumLength = 2)] string Name,
    [property: StringLength(200)] string? Slug,
    Guid? ParentId,
    bool IsActive = true);

public sealed class CreateCategory : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/admin/categories", async (SaveCategoryRequest req, CatalogDbContext db, CancellationToken ct) =>
            {
                var category = Category.Create(req.Name, req.Slug ?? req.Name, req.ParentId);
                category.IsActive = req.IsActive;
                if (await db.Categories.AnyAsync(c => c.Slug == category.Slug, ct))
                    throw new ConflictException("CATEGORY_SLUG_TAKEN", $"Slug '{category.Slug}' đã tồn tại.");

                db.Categories.Add(category);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/admin/categories/{category.Id}", category.ToDto());
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Categories");
}
```

#### `src/Services/Catalog.Api/Features/Categories/DeleteCategory.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Categories;

/// <summary>Xoá mềm; không cho xoá khi còn sản phẩm chưa xoá thuộc category.</summary>
public sealed class DeleteCategory : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/admin/categories/{id:guid}", async (Guid id, CatalogDbContext db, CancellationToken ct) =>
            {
                var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct)
                               ?? throw NotFoundException.For("category", id);
                if (await db.Products.AnyAsync(p => p.CategoryId == id, ct))
                    throw new ConflictException("CATEGORY_IN_USE", "Category còn sản phẩm, hãy chuyển sản phẩm sang category khác trước.");

                db.Categories.Remove(category); // AuditInterceptor đổi thành xoá mềm
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Categories");
}
```

#### `src/Services/Catalog.Api/Features/Categories/ListCategories.cs`

```csharp
using BuildingBlocks.Endpoints;
using Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Categories;

/// <summary>GET /categories: công khai, chỉ category đang bật.</summary>
public sealed class ListCategories : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/categories", async (CatalogDbContext db, CancellationToken ct) =>
                await db.Categories.AsNoTracking()
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.Name)
                    .Select(c => new CategoryDto(c.Id, c.Name, c.Slug, c.ParentId, c.IsActive))
                    .ToListAsync(ct))
            .AllowAnonymous()
            .WithTags("Categories");

        // Admin thấy cả category đang tắt.
        app.MapGet("/admin/categories", async (CatalogDbContext db, CancellationToken ct) =>
                await db.Categories.AsNoTracking()
                    .OrderBy(c => c.Name)
                    .Select(c => new CategoryDto(c.Id, c.Name, c.Slug, c.ParentId, c.IsActive))
                    .ToListAsync(ct))
            .RequireAuthorization(BuildingBlocks.Auth.Policies.Admin)
            .WithTags("Admin.Categories");
    }
}
```

#### `src/Services/Catalog.Api/Features/Categories/UpdateCategory.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Categories;

public sealed class UpdateCategory : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/admin/categories/{id:guid}", async (Guid id, SaveCategoryRequest req, CatalogDbContext db, CancellationToken ct) =>
            {
                var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct)
                               ?? throw NotFoundException.For("category", id);
                category.Update(req.Name, req.Slug ?? req.Name, req.ParentId);
                category.IsActive = req.IsActive;

                if (await db.Categories.AnyAsync(c => c.Slug == category.Slug && c.Id != id, ct))
                    throw new ConflictException("CATEGORY_SLUG_TAKEN", $"Slug '{category.Slug}' đã tồn tại.");

                await db.SaveChangesAsync(ct);
                return Results.Ok(category.ToDto());
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Categories");
}
```

#### `src/Services/Catalog.Api/Program.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks;
using BuildingBlocks.Persistence;
using Catalog.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddShopLabDbContext<CatalogDbContext>("catalogdb");

var app = builder.Build();

await app.InitializeDatabaseAsync<CatalogDbContext>(SeedData.RunAsync);
app.UseShopLabApi(typeof(Catalog.Api.ApiMarker).Assembly);

app.Run();
```

#### `src/AppHost/AppHost.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

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

// ---------- Database (mỗi service một database trên cùng container Postgres) ----------
var catalogDb = pg.AddDatabase("catalogdb");

// ---------- Service ----------
var catalog = builder.AddProject<Projects.Catalog_Api>("catalog")
    .WithKeycloak(keycloak)
    .WithReference(catalogDb).WaitFor(catalogDb);

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

#### `src/Gateways/Gateway.Web/yarp.json` *(sửa — nội dung đầy đủ sau khi sửa)*

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
      },
      "catalog-admin": {
        "ClusterId": "catalog",
        "AuthorizationPolicy": "authenticated",
        "Match": { "Path": "/api/admin/catalog/{**rest}" },
        "Transforms": [ { "PathPattern": "/admin/{**rest}" } ]
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

#### `shoplab.slnx` *(sửa — nội dung đầy đủ sau khi sửa)*

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
    <Project Path="tests/TestKit/TestKit.csproj" />
  </Folder>
</Solution>
```

**Kiểm tra cuối buổi**

```bash
aspire run --project src/AppHost
curl -s http://localhost:5000/api/catalog/categories    # [] (chưa seed)
```

### Buổi 5 — Product API: tìm kiếm keyset, CRUD, seed

**Việc cần làm**

1. `ProductSearch`: lọc category, `q` so với SearchText (không dấu), sắp theo (CreatedAt desc, Id), cursor keyset — không OFFSET.
2. Công khai chỉ thấy Product Active + IsActive + chưa xoá; admin xem tất cả, có `includeDeleted`.
3. Create/Update (đồng bộ danh sách variant), active on/off, delete (mềm), restore.
4. Seed `seed/categories.json`, `seed/products.json` (csproj link thư mục seed vào bin).

**File**

- `src/Services/Catalog.Api/Features/Products/AdminSearchProducts.cs`
- `src/Services/Catalog.Api/Features/Products/CreateProduct.cs`
- `src/Services/Catalog.Api/Features/Products/DeleteProduct.cs`
- `src/Services/Catalog.Api/Features/Products/GetProduct.cs`
- `src/Services/Catalog.Api/Features/Products/ProductDtos.cs`
- `src/Services/Catalog.Api/Features/Products/ProductSearch.cs`
- `src/Services/Catalog.Api/Features/Products/RestoreProduct.cs`
- `src/Services/Catalog.Api/Features/Products/SearchProducts.cs`
- `src/Services/Catalog.Api/Features/Products/SetProductActive.cs`
- `src/Services/Catalog.Api/Features/Products/UpdateProduct.cs`
- `seed/categories.json`
- `seed/products.json`
- `src/Services/Catalog.Api/Data/SeedData.cs`
- `http/catalog.http`
- `http/gate-s1.http`

#### `src/Services/Catalog.Api/Features/Products/AdminSearchProducts.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using Catalog.Api.Data;

namespace Catalog.Api.Features.Products;

/// <summary>GET /admin/products?q=&amp;includeDeleted=true — admin thấy cả bản Draft, đã tắt, đã xoá.</summary>
public sealed class AdminSearchProducts : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/admin/products", async (
                string? category, string? q, string? cursor, int? size, bool? includeDeleted,
                CatalogDbContext db, CancellationToken ct) =>
                await ProductSearch.ExecuteAsync(db,
                    new ProductSearchRequest(category, q, cursor, size, VisibleOnly: false, IncludeDeleted: includeDeleted == true), ct))
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}
```

#### `src/Services/Catalog.Api/Features/Products/CreateProduct.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Products;

public sealed record SaveProductRequest(
    [property: Required, StringLength(300, MinimumLength = 2)] string Name,
    [property: StringLength(300)] string? Slug,
    [property: StringLength(4000)] string? Description,
    Guid CategoryId,
    [property: StringLength(100)] string? Brand,
    ProductStatus Status,
    [property: Required, MinLength(1)] List<SaveVariantRequest> Variants);

public sealed record SaveVariantRequest(
    [property: Required, StringLength(64)] string Sku,
    [property: Required, StringLength(200)] string Name,
    [property: Range(1, long.MaxValue)] long Price,
    long? CompareAtPrice,
    string? Barcode,
    Dictionary<string, string>? Attributes)
{
    public VariantInput ToInput() => new(Sku, Name, Price, CompareAtPrice, Barcode, Attributes);
}

public sealed class CreateProduct : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/admin/products", async (SaveProductRequest req, CatalogDbContext db, CancellationToken ct) =>
            {
                await ProductGuards.EnsureCategoryExists(db, req.CategoryId, ct);
                var product = Product.Create(req.Name, req.Slug ?? req.Name, req.Description ?? "", req.CategoryId, req.Brand ?? "", req.Status);
                product.SyncVariants(req.Variants.Select(v => v.ToInput()));
                await ProductGuards.EnsureUnique(db, product, ct);

                db.Products.Add(product);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/admin/products/{product.Id}", product.ToDto());
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}

internal static class ProductGuards
{
    public static async Task EnsureCategoryExists(CatalogDbContext db, Guid categoryId, CancellationToken ct)
    {
        if (!await db.Categories.AnyAsync(c => c.Id == categoryId, ct))
            throw new BadRequestException("CATEGORY_NOT_FOUND", "Category không tồn tại.");
    }

    public static async Task EnsureUnique(CatalogDbContext db, Product product, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(p => p.Slug == product.Slug && p.Id != product.Id, ct))
            throw new ConflictException("PRODUCT_SLUG_TAKEN", $"Slug '{product.Slug}' đã tồn tại.");

        var skus = product.Variants.Where(v => !v.IsDeleted).Select(v => v.Sku).ToList();
        var taken = await db.Variants.Where(v => skus.Contains(v.Sku) && v.ProductId != product.Id)
            .Select(v => v.Sku).FirstOrDefaultAsync(ct);
        if (taken is not null)
            throw new ConflictException("SKU_TAKEN", $"SKU '{taken}' đã thuộc sản phẩm khác.");
    }
}
```

#### `src/Services/Catalog.Api/Features/Products/DeleteProduct.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Products;

public sealed class DeleteProduct : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/admin/products/{id:guid}", async (Guid id, CatalogDbContext db, CancellationToken ct) =>
            {
                var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
                              ?? throw NotFoundException.For("product", id);
                product.MarkDeleted();
                db.Products.Remove(product); // AuditInterceptor đổi thành IsDeleted = true
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}
```

#### `src/Services/Catalog.Api/Features/Products/GetProduct.cs`

```csharp
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Products;

public sealed class GetProduct : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        // Storefront: theo slug, chỉ sản phẩm đang hiển thị
        app.MapGet("/products/{slug}", async (string slug, CatalogDbContext db, CancellationToken ct) =>
            {
                var product = await db.Products.AsNoTracking().Include(p => p.Variants)
                    .FirstOrDefaultAsync(p => p.Slug == slug && p.Status == ProductStatus.Active && p.IsActive, ct)
                    ?? throw NotFoundException.For("product", slug);
                return Results.Ok(product.ToDto());
            })
            .AllowAnonymous()
            .WithTags("Products");

        // Admin: theo id, thấy cả bản đã tắt hoặc đã xoá
        app.MapGet("/admin/products/{id:guid}", async (Guid id, CatalogDbContext db, CancellationToken ct) =>
            {
                var product = await db.Products.AsNoTracking().IgnoreQueryFilters()
                    .Include(p => p.Variants.Where(v => !v.IsDeleted))
                    .FirstOrDefaultAsync(p => p.Id == id, ct)
                    ?? throw NotFoundException.For("product", id);
                return Results.Ok(product.ToDto());
            })
            .RequireAuthorization(BuildingBlocks.Auth.Policies.Admin)
            .WithTags("Admin.Products");
    }
}
```

#### `src/Services/Catalog.Api/Features/Products/ProductDtos.cs`

```csharp
using Catalog.Api.Domain;

namespace Catalog.Api.Features.Products;

public sealed record VariantDto(
    Guid Id,
    string Sku,
    string Name,
    long Price,
    long? CompareAtPrice,
    string? Barcode,
    Dictionary<string, string> Attributes,
    bool InStock);

public sealed record ProductDto(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    Guid CategoryId,
    string Brand,
    string Status,
    bool IsActive,
    bool IsDeleted,
    long Version,
    long MinPrice,
    IReadOnlyList<VariantDto> Variants,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Dòng trong danh sách sản phẩm (gọn hơn ProductDto).</summary>
public sealed record ProductSummaryDto(
    Guid Id,
    string Name,
    string Slug,
    Guid CategoryId,
    string Brand,
    long MinPrice,
    long? CompareAtPrice,
    bool InStock,
    bool IsActive,
    bool IsDeleted,
    string Status);

public static class ProductMapping
{
    public static ProductDto ToDto(this Product p) => new(
        p.Id, p.Name, p.Slug, p.Description, p.CategoryId, p.Brand, p.Status.ToString(),
        p.IsActive, p.IsDeleted, p.Version,
        p.Variants.Count == 0 ? 0 : p.Variants.Min(v => v.Price),
        p.Variants.OrderBy(v => v.Price).Select(v => v.ToDto()).ToList(),
        p.CreatedAt, p.UpdatedAt);

    public static VariantDto ToDto(this ProductVariant v) =>
        new(v.Id, v.Sku, v.Name, v.Price, v.CompareAtPrice, v.Barcode, v.Attributes, v.InStock);

    public static ProductSummaryDto ToSummary(this Product p)
    {
        var cheapest = p.Variants.OrderBy(v => v.Price).FirstOrDefault();
        return new ProductSummaryDto(
            p.Id, p.Name, p.Slug, p.CategoryId, p.Brand,
            cheapest?.Price ?? 0, cheapest?.CompareAtPrice,
            p.Variants.Any(v => v.InStock), p.IsActive, p.IsDeleted, p.Status.ToString());
    }
}
```

#### `src/Services/Catalog.Api/Features/Products/ProductSearch.cs`

```csharp
using BuildingBlocks.Paging;
using Catalog.Api.Data;
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Products;

public sealed record ProductSearchRequest(
    string? Category,
    string? Q,
    string? Cursor,
    int? Size,
    bool VisibleOnly = true,
    bool IncludeDeleted = false);

/// <summary>
/// Truy vấn tìm kiếm tách riêng khỏi endpoint để test trực tiếp (SearchProductsTests).
/// Sắp theo (Name, Id), phân trang keyset.
/// </summary>
public static class ProductSearch
{
    public static async Task<PagedResult<ProductSummaryDto>> ExecuteAsync(
        CatalogDbContext db, ProductSearchRequest req, CancellationToken ct)
    {
        var size = PagingDefaults.Clamp(req.Size);
        IQueryable<Product> query = db.Products.AsNoTracking().Include(p => p.Variants);

        if (req.IncludeDeleted)
            query = query.IgnoreQueryFilters();

        if (req.VisibleOnly)
            query = query.Where(p => p.Status == ProductStatus.Active && p.IsActive);

        if (!string.IsNullOrWhiteSpace(req.Category))
        {
            var slug = req.Category.Trim().ToLowerInvariant();
            var categoryId = await db.Categories.Where(c => c.Slug == slug).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            if (categoryId is null)
                return new PagedResult<ProductSummaryDto>([], null);
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var q = Slugs.RemoveDiacritics(req.Q.Trim()).ToLowerInvariant();
            query = query.Where(p => p.SearchText.Contains(q));
        }

        var cursor = KeysetCursor.Decode(req.Cursor);
        if (cursor is not null)
        {
            query = query.Where(p =>
                string.Compare(p.Name, cursor.Key) > 0 ||
                (p.Name == cursor.Key && p.Id.CompareTo(cursor.Id) > 0));
        }

        var page = await query
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Take(size + 1)
            .ToListAsync(ct);

        string? next = null;
        if (page.Count > size)
        {
            page.RemoveAt(size);
            var last = page[^1];
            next = new KeysetCursor(last.Name, last.Id).Encode();
        }

        return new PagedResult<ProductSummaryDto>(page.Select(p => p.ToSummary()).ToList(), next);
    }
}
```

#### `src/Services/Catalog.Api/Features/Products/RestoreProduct.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Products;

/// <summary>Khôi phục bản đã xoá mềm. Nếu slug/SKU đã bị sản phẩm khác dùng thì báo trùng.</summary>
public sealed class RestoreProduct : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/admin/products/{id:guid}/restore", async (Guid id, CatalogDbContext db, CancellationToken ct) =>
            {
                var product = await db.Products.IgnoreQueryFilters().Include(p => p.Variants)
                                  .FirstOrDefaultAsync(p => p.Id == id, ct)
                              ?? throw NotFoundException.For("product", id);
                if (!product.IsDeleted) return Results.NoContent();

                product.Restore();
                await ProductGuards.EnsureUnique(db, product, ct);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}
```

#### `src/Services/Catalog.Api/Features/Products/SearchProducts.cs`

```csharp
using BuildingBlocks.Endpoints;
using Catalog.Api.Data;

namespace Catalog.Api.Features.Products;

/// <summary>GET /products?category=dien-tu&amp;q=binh&amp;cursor=…&amp;size=20 — công khai, chỉ sản phẩm khách thấy được.</summary>
public sealed class SearchProducts : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/products", async (
                string? category, string? q, string? cursor, int? size,
                CatalogDbContext db, CancellationToken ct) =>
                await ProductSearch.ExecuteAsync(db, new ProductSearchRequest(category, q, cursor, size), ct))
            .AllowAnonymous()
            .WithTags("Products");
}
```

#### `src/Services/Catalog.Api/Features/Products/SetProductActive.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Products;

public sealed record SetActiveRequest(bool IsActive);

public sealed class SetProductActive : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/admin/products/{id:guid}/active", async (Guid id, SetActiveRequest req, CatalogDbContext db, CancellationToken ct) =>
            {
                var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
                              ?? throw NotFoundException.For("product", id);
                product.SetActive(req.IsActive);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}
```

#### `src/Services/Catalog.Api/Features/Products/UpdateProduct.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Products;

public sealed class UpdateProduct : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/admin/products/{id:guid}", async (Guid id, SaveProductRequest req, CatalogDbContext db, CancellationToken ct) =>
            {
                var product = await db.Products.Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == id, ct)
                              ?? throw NotFoundException.For("product", id);
                await ProductGuards.EnsureCategoryExists(db, req.CategoryId, ct);

                product.Update(req.Name, req.Slug ?? req.Name, req.Description ?? "", req.CategoryId, req.Brand ?? "", req.Status);
                var removed = product.SyncVariants(req.Variants.Select(v => v.ToInput()));
                db.Variants.RemoveRange(removed); // xoá mềm variant không còn trong form
                await ProductGuards.EnsureUnique(db, product, ct);

                await db.SaveChangesAsync(ct);
                return Results.Ok(product.ToDto());
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}
```

#### `seed/categories.json`

```json
[
  { "slug": "dien-tu", "name": "Điện tử" },
  { "slug": "gia-dung", "name": "Gia dụng" },
  { "slug": "thoi-trang", "name": "Thời trang" },
  { "slug": "van-phong-pham", "name": "Văn phòng phẩm" }
]
```

#### `seed/products.json`

```json
[
  {
    "slug": "sl-buds-pro", "name": "Tai nghe SL Buds Pro", "category": "dien-tu", "brand": "ShopLab", "status": "Active",
    "description": "Tai nghe true wireless, chống ồn chủ động, pin 30 giờ.",
    "variants": [
      { "sku": "SL-BUDS-PRO-BLK", "name": "Đen", "price": 2490000, "compareAtPrice": 2990000, "barcode": "8930000000011", "attributes": { "color": "black" } }
    ]
  },
  {
    "slug": "sl-sac-nhanh-65w", "name": "Sạc nhanh SL 65W", "category": "dien-tu", "brand": "ShopLab", "status": "Active",
    "description": "Củ sạc GaN 65W, 2 cổng USB-C và 1 cổng USB-A.",
    "variants": [
      { "sku": "SL-CHG-65W-WHT", "name": "Trắng", "price": 690000, "compareAtPrice": 890000, "barcode": "8930000000028", "attributes": { "color": "white" } }
    ]
  },
  {
    "slug": "sl-chuot-khong-day", "name": "Chuột không dây SL", "category": "dien-tu", "brand": "ShopLab", "status": "Active",
    "description": "Chuột không dây 2.4 GHz và Bluetooth, pin sạc 3 tháng.",
    "variants": [
      { "sku": "SL-MOUSE-WL", "name": "Xám", "price": 450000, "compareAtPrice": null, "barcode": "8930000000035", "attributes": { "color": "gray" } }
    ]
  },
  {
    "slug": "sl-binh-giu-nhiet-500", "name": "Bình giữ nhiệt SL 500 ml", "category": "gia-dung", "brand": "ShopLab", "status": "Active",
    "description": "Thép không gỉ 2 lớp, giữ nóng 12 giờ, giữ lạnh 24 giờ.",
    "variants": [
      { "sku": "SL-BOTTLE-500-SLV", "name": "Bạc", "price": 390000, "compareAtPrice": 450000, "barcode": "8930000000042", "attributes": { "color": "silver", "capacity": "500ml" } }
    ]
  },
  {
    "slug": "sl-balo-20l", "name": "Balo SL 20L", "category": "thoi-trang", "brand": "ShopLab", "status": "Active",
    "description": "Balo chống nước, ngăn laptop 15.6 inch.",
    "variants": [
      { "sku": "SL-BACKPACK-20L", "name": "Đen", "price": 890000, "compareAtPrice": null, "barcode": "8930000000059", "attributes": { "color": "black" } }
    ]
  },
  {
    "slug": "sl-ao-thun-basic", "name": "Áo thun SL Basic", "category": "thoi-trang", "brand": "ShopLab", "status": "Active",
    "description": "Cotton 100%, form regular.",
    "variants": [
      { "sku": "SL-TEE-BASIC-M", "name": "Size M", "price": 199000, "compareAtPrice": 249000, "barcode": "8930000000066", "attributes": { "size": "M" } }
    ]
  },
  {
    "slug": "sl-so-tay-a5", "name": "Sổ tay SL A5", "category": "van-phong-pham", "brand": "ShopLab", "status": "Active",
    "description": "Sổ tay A5, 160 trang giấy chấm.",
    "variants": [
      { "sku": "SL-NOTE-A5", "name": "Bìa kraft", "price": 49000, "compareAtPrice": null, "barcode": "8930000000073", "attributes": { "cover": "kraft" } }
    ]
  }
]
```

#### `src/Services/Catalog.Api/Data/SeedData.cs`

```csharp
using System.Text.Json;
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Data;

/// <summary>Đọc seed/categories.json và seed/products.json (copy vào bin/seed lúc build) khi bảng còn trống.</summary>
public static class SeedData
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record CategorySeed(string Slug, string Name);
    private sealed record VariantSeed(string Sku, string Name, long Price, long? CompareAtPrice, string? Barcode, Dictionary<string, string>? Attributes);
    private sealed record ProductSeed(string Slug, string Name, string Category, string Brand, string Status, string Description, List<VariantSeed> Variants);

    public static async Task RunAsync(CatalogDbContext db, IServiceProvider _)
    {
        if (await db.Categories.IgnoreQueryFilters().AnyAsync()) return;

        var dir = Path.Combine(AppContext.BaseDirectory, "seed");
        var categories = Read<List<CategorySeed>>(dir, "categories.json")
            .Select(c => Category.Create(c.Name, c.Slug))
            .ToDictionary(c => c.Slug);
        db.Categories.AddRange(categories.Values);

        foreach (var p in Read<List<ProductSeed>>(dir, "products.json"))
        {
            var product = Product.Create(p.Name, p.Slug, p.Description, categories[p.Category].Id, p.Brand,
                Enum.Parse<ProductStatus>(p.Status));
            product.SyncVariants(p.Variants.Select(v => new VariantInput(v.Sku, v.Name, v.Price, v.CompareAtPrice, v.Barcode, v.Attributes)));
            db.Products.Add(product);
        }

        await db.SaveChangesAsync();
    }

    private static T Read<T>(string dir, string file) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(dir, file)), Json)
        ?? throw new InvalidOperationException($"seed/{file} rỗng");
}
```

#### `http/catalog.http`

```http
### Catalog qua gateway. Token admin: TOKEN=$(./tools/get-token.sh admin@shoplab.local)
@gateway = http://localhost:5000
@admin = paste-admin-token
@customer = paste-customer-token

### Công khai: category
GET {{gateway}}/api/catalog/categories

### Công khai: tìm sản phẩm, lọc category, không dấu
GET {{gateway}}/api/catalog/products?category=dien-tu&size=2

### Trang tiếp theo: dán nextCursor của lần trước
GET {{gateway}}/api/catalog/products?category=dien-tu&size=2&cursor=paste-cursor

### Tìm không dấu
GET {{gateway}}/api/catalog/products?q=binh

### Chi tiết theo slug
GET {{gateway}}/api/catalog/products/sl-buds-pro

### Admin: customer gọi → 403
GET {{gateway}}/api/admin/catalog/products
Authorization: Bearer {{customer}}

### Admin: danh sách cả bản đã xoá
GET {{gateway}}/api/admin/catalog/products?includeDeleted=true
Authorization: Bearer {{admin}}

### Admin: tạo category
POST {{gateway}}/api/admin/catalog/categories
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "name": "Phụ kiện", "isActive": true }

### Admin: tạo sản phẩm 2 variant (đổi categoryId bằng id lấy từ GET categories)
POST {{gateway}}/api/admin/catalog/products
Authorization: Bearer {{admin}}
Content-Type: application/json

{
  "name": "Cáp USB-C SL 1m",
  "categoryId": "paste-category-id",
  "brand": "ShopLab",
  "status": "Active",
  "variants": [
    { "sku": "SL-CABLE-1M-BLK", "name": "Đen", "price": 99000 },
    { "sku": "SL-CABLE-1M-WHT", "name": "Trắng", "price": 99000, "attributes": { "color": "white" } }
  ]
}

### Admin: thiếu tên → 400 ProblemDetails
POST {{gateway}}/api/admin/catalog/products
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "name": "", "categoryId": "paste-category-id", "status": "Active", "variants": [] }

### Admin: tắt sản phẩm (storefront không thấy nữa)
POST {{gateway}}/api/admin/catalog/products/paste-product-id/active
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "isActive": false }

### Admin: xoá mềm
DELETE {{gateway}}/api/admin/catalog/products/paste-product-id
Authorization: Bearer {{admin}}

### Admin: khôi phục
POST {{gateway}}/api/admin/catalog/products/paste-product-id/restore
Authorization: Bearer {{admin}}
```

#### `http/gate-s1.http`

```http
### Catalog qua gateway. Token admin: TOKEN=$(./tools/get-token.sh admin@shoplab.local)
@gateway = http://localhost:5000
@admin = paste-admin-token
@customer = paste-customer-token

### Công khai: category
GET {{gateway}}/api/catalog/categories

### Công khai: tìm sản phẩm, lọc category, không dấu
GET {{gateway}}/api/catalog/products?category=dien-tu&size=2

### Trang tiếp theo: dán nextCursor của lần trước
GET {{gateway}}/api/catalog/products?category=dien-tu&size=2&cursor=paste-cursor

### Tìm không dấu
GET {{gateway}}/api/catalog/products?q=binh

### Chi tiết theo slug
GET {{gateway}}/api/catalog/products/sl-buds-pro

### Admin: customer gọi → 403
GET {{gateway}}/api/admin/catalog/products
Authorization: Bearer {{customer}}

### Admin: danh sách cả bản đã xoá
GET {{gateway}}/api/admin/catalog/products?includeDeleted=true
Authorization: Bearer {{admin}}

### Admin: tạo category
POST {{gateway}}/api/admin/catalog/categories
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "name": "Phụ kiện", "isActive": true }

### Admin: tạo sản phẩm 2 variant (đổi categoryId bằng id lấy từ GET categories)
POST {{gateway}}/api/admin/catalog/products
Authorization: Bearer {{admin}}
Content-Type: application/json

{
  "name": "Cáp USB-C SL 1m",
  "categoryId": "paste-category-id",
  "brand": "ShopLab",
  "status": "Active",
  "variants": [
    { "sku": "SL-CABLE-1M-BLK", "name": "Đen", "price": 99000 },
    { "sku": "SL-CABLE-1M-WHT", "name": "Trắng", "price": 99000, "attributes": { "color": "white" } }
  ]
}

### Admin: thiếu tên → 400 ProblemDetails
POST {{gateway}}/api/admin/catalog/products
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "name": "", "categoryId": "paste-category-id", "status": "Active", "variants": [] }

### Admin: tắt sản phẩm (storefront không thấy nữa)
POST {{gateway}}/api/admin/catalog/products/paste-product-id/active
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "isActive": false }

### Admin: xoá mềm
DELETE {{gateway}}/api/admin/catalog/products/paste-product-id
Authorization: Bearer {{admin}}

### Admin: khôi phục
POST {{gateway}}/api/admin/catalog/products/paste-product-id/restore
Authorization: Bearer {{admin}}
```

**Kiểm tra cuối buổi**

```bash
# xoá DB cũ để seed chạy lại: Aspire dashboard → pg → Stop, rồi docker volume rm shoplab-pg (lab)
curl -s "http://localhost:5000/api/catalog/products?category=dien-tu&size=2" | jq '.items[].name, .nextCursor'
curl -s "http://localhost:5000/api/catalog/products?q=binh" | jq '.items[].name'     # "Bình giữ nhiệt…"
```

**Xong tuần 1 khi:**

- [ ] Seed hiện đủ 7 SKU
- [ ] Phân trang 2 trang không trùng/sót
- [ ] Xoá mềm rồi khôi phục được qua `http/catalog.http`

---

## Tuần 2 — Storefront + admin catalog, test tích hợp

**Mục tiêu tuần**

- Storefront: trang chủ/danh mục/tìm kiếm với nút Xem thêm (cursor), chi tiết sản phẩm chọn biến thể.
- Admin: quản lý danh mục, danh sách + form sản phẩm (FormArray biến thể).
- TestKit dùng chung; test audit, search, phân quyền chạy trên Postgres thật.

### Buổi 6 — api-client catalog + storefront list/detail

**Việc cần làm**

1. `catalog.models.ts`, `catalog.api.ts` (CatalogApi + AdminCatalogApi), `toParams`, `readProblem`.
2. `product-list`: route `/`, `/c/:slug`, query `?q=` (component input binding), Xem thêm bằng cursor.
3. `product-detail` `/p/:slug`: chọn biến thể, hiện giá + giá gạch.
4. Header có ô tìm kiếm; trang 404.

**Lệnh**

```bash
cd src/Web && npx nx serve storefront
```

**File**

- `src/Web/libs/api-client/src/index.ts` *(sửa)*
- `src/Web/libs/api-client/src/lib/catalog.api.ts`
- `src/Web/libs/api-client/src/lib/catalog.models.ts`
- `src/Web/libs/api-client/src/lib/http-params.ts`
- `src/Web/apps/storefront/src/app/app.routes.ts` *(sửa)*
- `src/Web/apps/storefront/src/app/layout/header.component.ts` *(sửa)*
- `src/Web/apps/storefront/src/app/pages/not-found.component.ts`
- `src/Web/apps/storefront/src/app/pages/product-detail/product-detail.component.html`
- `src/Web/apps/storefront/src/app/pages/product-detail/product-detail.component.ts`
- `src/Web/apps/storefront/src/app/pages/product-list/product-list.component.html`
- `src/Web/apps/storefront/src/app/pages/product-list/product-list.component.ts`

#### `src/Web/libs/api-client/src/index.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
export * from './lib/http-params';
export * from './lib/system.api';
export * from './lib/catalog.models';
export * from './lib/catalog.api';
```

#### `src/Web/libs/api-client/src/lib/catalog.api.ts`

```typescript
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  Category,
  Paged,
  Product,
  ProductQuery,
  ProductSummary,
  SaveCategory,
  SaveProduct,
} from './catalog.models';
import { toParams } from './http-params';

/** API công khai của Catalog: /api/catalog/* (gateway bỏ tiền tố, Catalog nhận /products…). */
@Injectable({ providedIn: 'root' })
export class CatalogApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/catalog';

  categories() {
    return this.http.get<Category[]>(`${this.base}/categories`);
  }

  search(query: ProductQuery) {
    return this.http.get<Paged<ProductSummary>>(`${this.base}/products`, { params: toParams(query) });
  }

  getBySlug(slug: string) {
    return this.http.get<Product>(`${this.base}/products/${encodeURIComponent(slug)}`);
  }
}

/** API quản trị của Catalog: /api/admin/catalog/* → Catalog /admin/*. */
@Injectable({ providedIn: 'root' })
export class AdminCatalogApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/admin/catalog';

  categories() {
    return this.http.get<Category[]>(`${this.base}/categories`);
  }

  createCategory(body: SaveCategory) {
    return this.http.post<Category>(`${this.base}/categories`, body);
  }

  updateCategory(id: string, body: SaveCategory) {
    return this.http.put<Category>(`${this.base}/categories/${id}`, body);
  }

  deleteCategory(id: string) {
    return this.http.delete<void>(`${this.base}/categories/${id}`);
  }

  searchProducts(query: ProductQuery) {
    return this.http.get<Paged<ProductSummary>>(`${this.base}/products`, { params: toParams(query) });
  }

  getProduct(id: string) {
    return this.http.get<Product>(`${this.base}/products/${id}`);
  }

  createProduct(body: SaveProduct) {
    return this.http.post<Product>(`${this.base}/products`, body);
  }

  updateProduct(id: string, body: SaveProduct) {
    return this.http.put<Product>(`${this.base}/products/${id}`, body);
  }

  deleteProduct(id: string) {
    return this.http.delete<void>(`${this.base}/products/${id}`);
  }

  setActive(id: string, isActive: boolean) {
    return this.http.post<void>(`${this.base}/products/${id}/active`, { isActive });
  }

  restore(id: string) {
    return this.http.post<void>(`${this.base}/products/${id}/restore`, {});
  }
}
```

#### `src/Web/libs/api-client/src/lib/catalog.models.ts`

```typescript
export interface Paged<T> {
  items: T[];
  nextCursor: string | null;
}

export interface Category {
  id: string;
  name: string;
  slug: string;
  parentId: string | null;
  isActive: boolean;
}

export type ProductStatus = 'Draft' | 'Active' | 'Archived';

export interface ProductSummary {
  id: string;
  name: string;
  slug: string;
  categoryId: string;
  brand: string;
  minPrice: number;
  compareAtPrice: number | null;
  inStock: boolean;
  isActive: boolean;
  isDeleted: boolean;
  status: ProductStatus;
}

export interface Variant {
  id: string;
  sku: string;
  name: string;
  price: number;
  compareAtPrice: number | null;
  barcode: string | null;
  attributes: Record<string, string>;
  inStock: boolean;
}

export interface Product {
  id: string;
  name: string;
  slug: string;
  description: string;
  categoryId: string;
  brand: string;
  status: ProductStatus;
  isActive: boolean;
  isDeleted: boolean;
  version: number;
  minPrice: number;
  variants: Variant[];
  createdAt: string;
  updatedAt: string;
}

export interface SaveCategory {
  name: string;
  slug?: string | null;
  parentId?: string | null;
  isActive: boolean;
}

export interface SaveVariant {
  sku: string;
  name: string;
  price: number;
  compareAtPrice?: number | null;
  barcode?: string | null;
  attributes?: Record<string, string>;
}

export interface SaveProduct {
  name: string;
  slug?: string | null;
  description?: string | null;
  categoryId: string;
  brand?: string | null;
  status: ProductStatus;
  variants: SaveVariant[];
}

export interface ProductQuery {
  category?: string | null;
  q?: string | null;
  cursor?: string | null;
  size?: number;
  includeDeleted?: boolean;
}

/** Lỗi ProblemDetails do API trả về; `code` là mã nghiệp vụ (vd SKU_TAKEN). */
export interface ApiProblem {
  status: number;
  title: string;
  code?: string;
}
```

#### `src/Web/libs/api-client/src/lib/http-params.ts`

```typescript
import { HttpErrorResponse, HttpParams } from '@angular/common/http';
import { ApiProblem } from './catalog.models';

/** Bỏ qua giá trị null/undefined/'' khi dựng query string. */
export function toParams(query: object): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === null || value === undefined || value === '') continue;
    params = params.set(key, String(value));
  }
  return params;
}

/** Đọc ProblemDetails từ lỗi HTTP để hiện thông điệp tiếng Việt do server trả. */
export function readProblem(err: unknown): ApiProblem {
  if (err instanceof HttpErrorResponse) {
    const body = err.error ?? {};
    return { status: err.status, title: body.title ?? err.message, code: body.code };
  }
  return { status: 0, title: String(err) };
}
```

#### `src/Web/apps/storefront/src/app/app.routes.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./pages/product-list/product-list.component').then((m) => m.ProductListComponent) },
  { path: 'c/:slug', loadComponent: () => import('./pages/product-list/product-list.component').then((m) => m.ProductListComponent) },
  { path: 'p/:slug', loadComponent: () => import('./pages/product-detail/product-detail.component').then((m) => m.ProductDetailComponent) },
  { path: 'dev/ping', loadComponent: () => import('./pages/home/home.component').then((m) => m.HomeComponent) },
  { path: 'forbidden', loadComponent: () => import('./pages/forbidden.component').then((m) => m.ForbiddenComponent) },
  { path: '**', loadComponent: () => import('./pages/not-found.component').then((m) => m.NotFoundComponent) },
];
```

#### `src/Web/apps/storefront/src/app/layout/header.component.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '@shoplab/auth';

@Component({
  selector: 'app-header',
  imports: [RouterLink, FormsModule],
  template: `
    <header class="bar">
      <div class="container row">
        <a routerLink="/" class="logo">ShopLab</a>
        <form class="grow search" (ngSubmit)="search()">
          <input name="q" [(ngModel)]="q" placeholder="Tìm sản phẩm…" />
        </form>
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
    .search input { width: 100%; max-width: 420px; }
  `,
})
export class HeaderComponent {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected q = '';

  search(): void {
    const q = this.q.trim();
    this.router.navigate(['/'], { queryParams: q ? { q } : {} });
  }
}
```

#### `src/Web/apps/storefront/src/app/pages/not-found.component.ts`

```typescript
import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-not-found',
  imports: [RouterLink],
  template: `
    <h2>Không tìm thấy trang</h2>
    <p class="muted">Sản phẩm có thể đã ngừng bán hoặc đường dẫn bị sai.</p>
    <a routerLink="/">Về trang chủ</a>
  `,
})
export class NotFoundComponent {}
```

#### `src/Web/apps/storefront/src/app/pages/product-detail/product-detail.component.html`

```html
@if (product(); as p) {
  <div class="layout">
    <div class="thumb">🛍️</div>
    <div>
      <p class="muted">{{ p.brand }}</p>
      <h1>{{ p.name }}</h1>
      @if (variant(); as v) {
        <p class="price">
          {{ v.price | vnd }}
          @if (v.compareAtPrice) { <span class="compare muted">{{ v.compareAtPrice | vnd }}</span> }
        </p>
        @if (v.inStock) {
          <span class="badge">Còn hàng</span>
        } @else {
          <span class="badge off">Hết hàng</span>
        }
      }

      @if (p.variants.length > 1) {
        <h4>Phân loại</h4>
        <div class="variants">
          @for (v of p.variants; track v.sku) {
            <button [class.selected]="v.sku === variant()?.sku" (click)="selectedSku.set(v.sku)">{{ v.name }}</button>
          }
        </div>
      }

      <!-- S2 thêm nút "Thêm vào giỏ" ở đây -->

      <h4>Mô tả</h4>
      <p>{{ p.description }}</p>
      <p class="muted">SKU: {{ variant()?.sku }}</p>
    </div>
  </div>
} @else {
  <p class="muted">Đang tải…</p>
}
```

#### `src/Web/apps/storefront/src/app/pages/product-detail/product-detail.component.ts`

```typescript
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { CatalogApi, Product } from '@shoplab/api-client';
import { VndPipe } from '@shoplab/ui';

@Component({
  selector: 'app-product-detail',
  imports: [VndPipe],
  templateUrl: './product-detail.component.html',
  styles: `
    .layout { display: grid; grid-template-columns: 1fr 1fr; gap: 32px; }
    .thumb { aspect-ratio: 1; background: #efeee9; border-radius: 8px; display: grid; place-items: center; font-size: 64px; }
    .price { font-size: 24px; font-weight: 700; }
    .compare { text-decoration: line-through; font-size: 16px; margin-left: 8px; font-weight: 400; }
    .variants { display: flex; gap: 8px; flex-wrap: wrap; }
    .variants button.selected { border-color: var(--accent); color: var(--accent); }
    @media (max-width: 720px) { .layout { grid-template-columns: 1fr; } }
  `,
})
export class ProductDetailComponent {
  private readonly api = inject(CatalogApi);
  private readonly router = inject(Router);

  readonly slug = input.required<string>();

  protected readonly product = signal<Product | null>(null);
  protected readonly selectedSku = signal<string | null>(null);
  protected readonly variant = computed(() => {
    const p = this.product();
    return p?.variants.find((v) => v.sku === this.selectedSku()) ?? p?.variants[0] ?? null;
  });

  constructor() {
    effect(() => {
      this.api.getBySlug(this.slug()).subscribe({
        next: (p) => {
          this.product.set(p);
          this.selectedSku.set(p.variants[0]?.sku ?? null);
        },
        error: () => this.router.navigateByUrl('/not-found', { skipLocationChange: true }),
      });
    });
  }
}
```

#### `src/Web/apps/storefront/src/app/pages/product-list/product-list.component.html`

```html
<nav class="cats">
  <a routerLink="/" [class.active]="!slug()">Tất cả</a>
  @for (c of categories(); track c.id) {
    <a [routerLink]="['/c', c.slug]" [class.active]="slug() === c.slug">{{ c.name }}</a>
  }
</nav>

@if (q()) {
  <p class="muted">Kết quả cho "{{ q() }}"</p>
}

@if (error()) {
  <p class="error">{{ error() }}</p>
}

<div class="grid">
  @for (p of products(); track p.id) {
    <a class="card product" [routerLink]="['/p', p.slug]">
      <div class="thumb">🛍️</div>
      <div>{{ p.name }}</div>
      <div class="price">
        {{ p.minPrice | vnd }}
        @if (p.compareAtPrice) { <span class="compare muted">{{ p.compareAtPrice | vnd }}</span> }
      </div>
      @if (!p.inStock) { <span class="badge off">Hết hàng</span> }
    </a>
  } @empty {
    @if (!loading()) { <p class="muted">Không có sản phẩm.</p> }
  }
</div>

@if (loading()) {
  <p class="muted">Đang tải…</p>
} @else if (nextCursor()) {
  <button class="more" (click)="loadMore()">Xem thêm</button>
}
```

#### `src/Web/apps/storefront/src/app/pages/product-list/product-list.component.ts`

```typescript
import { Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CatalogApi, Category, ProductSummary, readProblem } from '@shoplab/api-client';
import { VndPipe } from '@shoplab/ui';

/** Trang chủ (/), trang category (/c/:slug), kết quả tìm kiếm (/?q=…). Phân trang bằng cursor + nút "Xem thêm". */
@Component({
  selector: 'app-product-list',
  imports: [RouterLink, VndPipe],
  templateUrl: './product-list.component.html',
  styles: `
    .cats { display: flex; gap: 8px; flex-wrap: wrap; margin: 8px 0 20px; }
    .cats a { padding: 4px 12px; border: 1px solid var(--line); border-radius: 99px; color: var(--ink); background: #fff; }
    .cats a.active { border-color: var(--accent); color: var(--accent); }
    .product { display: grid; gap: 6px; }
    .thumb { aspect-ratio: 1; background: #efeee9; border-radius: 6px; display: grid; place-items: center; font-size: 32px; }
    .price { font-weight: 600; }
    .compare { text-decoration: line-through; margin-left: 6px; font-weight: 400; }
    .more { margin: 24px auto; display: block; }
  `,
})
export class ProductListComponent {
  private readonly api = inject(CatalogApi);

  /** Từ route /c/:slug (withComponentInputBinding). */
  readonly slug = input<string>();
  /** Từ query ?q=… */
  readonly q = input<string>();

  protected readonly categories = signal<Category[]>([]);
  protected readonly products = signal<ProductSummary[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.api.categories().subscribe((c) => this.categories.set(c));
    // Đổi category hoặc từ khoá thì tải lại từ trang đầu
    effect(() => {
      const category = this.slug();
      const q = this.q();
      this.load(category, q, null);
    });
  }

  loadMore(): void {
    this.load(this.slug(), this.q(), this.nextCursor());
  }

  private load(category: string | undefined, q: string | undefined, cursor: string | null): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.search({ category, q, cursor, size: 12 }).subscribe({
      next: (page) => {
        this.products.update((list) => (cursor ? [...list, ...page.items] : page.items));
        this.nextCursor.set(page.nextCursor);
        this.loading.set(false);
      },
      error: (e) => {
        this.error.set(readProblem(e).title);
        this.loading.set(false);
      },
    });
  }
}
```

**Kiểm tra cuối buổi**

```bash
# :4200 → chọn Điện tử → Xem thêm → mở 1 sản phẩm → đổi biến thể
```

### Buổi 7 — Admin category + product

**Việc cần làm**

1. Trang Category: thêm/sửa inline, xoá.
2. Danh sách sản phẩm: tìm, bật/tắt, xoá mềm, hiện cả bản đã xoá, khôi phục.
3. Form sản phẩm: ReactiveForms, FormArray biến thể, mỗi biến thể có FormArray thuộc tính key-value.

**File**

- `src/Web/apps/admin/src/app/app.routes.ts` *(sửa)*
- `src/Web/apps/admin/src/app/layout/shell.component.ts` *(sửa)*
- `src/Web/apps/admin/src/app/pages/categories/categories.component.ts`
- `src/Web/apps/admin/src/app/pages/products/product-form.component.html`
- `src/Web/apps/admin/src/app/pages/products/product-form.component.ts`
- `src/Web/apps/admin/src/app/pages/products/product-list.component.ts`

#### `src/Web/apps/admin/src/app/app.routes.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

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
      // S1
      { path: 'categories', loadComponent: () => import('./pages/categories/categories.component').then((m) => m.CategoriesComponent) },
      { path: 'products', loadComponent: () => import('./pages/products/product-list.component').then((m) => m.ProductListComponent) },
      { path: 'products/new', loadComponent: () => import('./pages/products/product-form.component').then((m) => m.ProductFormComponent) },
      { path: 'products/:id', loadComponent: () => import('./pages/products/product-form.component').then((m) => m.ProductFormComponent) },
    ],
  },
  { path: '**', redirectTo: '' },
];
```

#### `src/Web/apps/admin/src/app/layout/shell.component.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

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
  protected readonly nav: NavItem[] = [
    { path: '/', label: 'Tổng quan' },
    { path: '/categories', label: 'Category' },
    { path: '/products', label: 'Sản phẩm' },
  ];
}
```

#### `src/Web/apps/admin/src/app/pages/categories/categories.component.ts`

```typescript
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AdminCatalogApi, Category, readProblem } from '@shoplab/api-client';

/** Danh sách category + form tạo/sửa ngay trên trang. */
@Component({
  selector: 'app-categories',
  imports: [ReactiveFormsModule],
  template: `
    <h1>Category</h1>
    <div class="layout">
      <table>
        <thead><tr><th>Tên</th><th>Slug</th><th>Trạng thái</th><th></th></tr></thead>
        <tbody>
          @for (c of categories(); track c.id) {
            <tr>
              <td>{{ c.name }}</td>
              <td class="muted">{{ c.slug }}</td>
              <td><span class="badge" [class.off]="!c.isActive">{{ c.isActive ? 'Bật' : 'Tắt' }}</span></td>
              <td class="row">
                <button (click)="edit(c)">Sửa</button>
                <button class="danger" (click)="remove(c)">Xoá</button>
              </td>
            </tr>
          }
        </tbody>
      </table>

      <form class="card form" [formGroup]="form" (ngSubmit)="save()">
        <h3>{{ editingId() ? 'Sửa category' : 'Thêm category' }}</h3>
        <label>Tên <input formControlName="name" /></label>
        <label>Slug (để trống thì sinh từ tên) <input formControlName="slug" /></label>
        <label class="row"><input type="checkbox" formControlName="isActive" /> Đang bật</label>
        @if (error()) { <p class="error">{{ error() }}</p> }
        <div class="row">
          <button class="primary" type="submit" [disabled]="form.invalid">Lưu</button>
          @if (editingId()) { <button type="button" (click)="reset()">Huỷ</button> }
        </div>
      </form>
    </div>
  `,
  styles: `.layout { display: grid; grid-template-columns: 2fr 1fr; gap: 24px; align-items: start; }`,
})
export class CategoriesComponent {
  private readonly api = inject(AdminCatalogApi);
  private readonly fb = inject(FormBuilder);

  protected readonly categories = signal<Category[]>([]);
  protected readonly editingId = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.minLength(2)]],
    slug: [''],
    isActive: [true],
  });

  constructor() {
    this.load();
  }

  load(): void {
    this.api.categories().subscribe((c) => this.categories.set(c));
  }

  edit(c: Category): void {
    this.editingId.set(c.id);
    this.form.setValue({ name: c.name, slug: c.slug, isActive: c.isActive });
  }

  reset(): void {
    this.editingId.set(null);
    this.error.set(null);
    this.form.reset({ name: '', slug: '', isActive: true });
  }

  save(): void {
    const value = this.form.getRawValue();
    const body = { ...value, slug: value.slug || null };
    const id = this.editingId();
    const req = id ? this.api.updateCategory(id, body) : this.api.createCategory(body);
    req.subscribe({
      next: () => {
        this.reset();
        this.load();
      },
      error: (e) => this.error.set(readProblem(e).title),
    });
  }

  remove(c: Category): void {
    if (!confirm(`Xoá category "${c.name}"?`)) return;
    this.api.deleteCategory(c.id).subscribe({
      next: () => this.load(),
      error: (e) => alert(readProblem(e).title),
    });
  }
}
```

#### `src/Web/apps/admin/src/app/pages/products/product-form.component.html`

```html
<div class="row">
  <h1 class="grow">{{ id() && id() !== 'new' ? 'Sửa sản phẩm' : 'Thêm sản phẩm' }}</h1>
  <a routerLink="/products">← Danh sách</a>
</div>

<form class="form" [formGroup]="form" (ngSubmit)="save()">
  <label>Tên <input formControlName="name" /></label>
  <label>Slug (để trống thì sinh từ tên) <input formControlName="slug" /></label>
  <label>
    Category
    <select formControlName="categoryId">
      <option value="">-- chọn --</option>
      @for (c of categories(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
    </select>
  </label>
  <label>Thương hiệu <input formControlName="brand" /></label>
  <label>
    Trạng thái
    <select formControlName="status">
      @for (s of statuses; track s) { <option [value]="s">{{ s }}</option> }
    </select>
  </label>
  <label>Mô tả <textarea rows="3" formControlName="description"></textarea></label>

  <h3>Variant</h3>
  <div formArrayName="variants" class="form">
    @for (v of variants.controls; track $index; let i = $index) {
      <div class="variant" [formGroupName]="i">
        <div class="cols">
          <label>SKU <input formControlName="sku" /></label>
          <label>Tên <input formControlName="name" /></label>
          <label>Giá (VND) <input type="number" formControlName="price" /></label>
          <label>Giá gạch <input type="number" formControlName="compareAtPrice" /></label>
        </div>
        <label>Barcode <input formControlName="barcode" /></label>
        <div class="attrs" formArrayName="attributes">
          <span class="muted">Thuộc tính</span>
          @for (a of attrs(i).controls; track $index; let j = $index) {
            <div class="row" [formGroupName]="j">
              <input placeholder="key (vd color)" formControlName="key" />
              <input placeholder="value (vd black)" formControlName="value" />
              <button type="button" (click)="attrs(i).removeAt(j)">×</button>
            </div>
          }
          <button type="button" (click)="addAttr(i)">+ Thuộc tính</button>
        </div>
        @if (variants.length > 1) {
          <button type="button" class="danger" (click)="removeVariant(i)">Bỏ variant này</button>
        }
      </div>
    }
  </div>
  <button type="button" (click)="addVariant()">+ Variant</button>

  @if (error()) { <p class="error">{{ error() }}</p> }
  <button class="primary" type="submit" [disabled]="form.invalid">Lưu</button>
</form>
```

#### `src/Web/apps/admin/src/app/pages/products/product-form.component.ts`

```typescript
import { Component, effect, inject, input, signal } from '@angular/core';
import { FormArray, FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminCatalogApi, Category, Product, ProductStatus, SaveProduct, readProblem } from '@shoplab/api-client';

type AttrGroup = FormGroup<{ key: import('@angular/forms').FormControl<string>; value: import('@angular/forms').FormControl<string> }>;

/** /products/new và /products/:id. Variant là FormArray, thuộc tính của variant là FormArray cặp key-value. */
@Component({
  selector: 'app-product-form',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './product-form.component.html',
  styles: `
    .variant { border: 1px solid var(--line); border-radius: 8px; padding: 12px; display: grid; gap: 8px; }
    .cols { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; }
    .attrs { display: grid; gap: 4px; }
  `,
})
export class ProductFormComponent {
  private readonly api = inject(AdminCatalogApi);
  private readonly fb = inject(FormBuilder).nonNullable;
  private readonly router = inject(Router);

  /** Route param :id; undefined khi tạo mới. */
  readonly id = input<string>();

  protected readonly categories = signal<Category[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly statuses: ProductStatus[] = ['Draft', 'Active', 'Archived'];

  protected readonly form = this.fb.group({
    name: ['', [Validators.required, Validators.minLength(2)]],
    slug: [''],
    description: [''],
    categoryId: ['', Validators.required],
    brand: [''],
    status: ['Active' as ProductStatus],
    variants: this.fb.array([this.variantGroup()]),
  });

  constructor() {
    this.api.categories().subscribe((c) => this.categories.set(c));
    effect(() => {
      const id = this.id();
      if (id && id !== 'new') this.api.getProduct(id).subscribe((p) => this.fill(p));
    });
  }

  get variants(): FormArray {
    return this.form.controls.variants;
  }

  attrs(i: number): FormArray<AttrGroup> {
    return this.variants.at(i).get('attributes') as FormArray<AttrGroup>;
  }

  addVariant(): void {
    this.variants.push(this.variantGroup());
  }

  removeVariant(i: number): void {
    if (this.variants.length > 1) this.variants.removeAt(i);
  }

  addAttr(i: number): void {
    this.attrs(i).push(this.attrGroup('', ''));
  }

  save(): void {
    const v = this.form.getRawValue();
    const body: SaveProduct = {
      name: v.name,
      slug: v.slug || null,
      description: v.description,
      categoryId: v.categoryId,
      brand: v.brand,
      status: v.status,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      variants: v.variants.map((x: any) => ({
        sku: x.sku,
        name: x.name,
        price: Number(x.price),
        compareAtPrice: x.compareAtPrice ? Number(x.compareAtPrice) : null,
        barcode: x.barcode || null,
        attributes: Object.fromEntries(
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          (x.attributes as any[]).filter((a) => a.key).map((a) => [a.key, a.value]),
        ),
      })),
    };
    const id = this.id();
    const req = id && id !== 'new' ? this.api.updateProduct(id, body) : this.api.createProduct(body);
    req.subscribe({
      next: () => this.router.navigateByUrl('/products'),
      error: (e) => this.error.set(readProblem(e).title),
    });
  }

  private fill(p: Product): void {
    this.form.patchValue({
      name: p.name,
      slug: p.slug,
      description: p.description,
      categoryId: p.categoryId,
      brand: p.brand,
      status: p.status,
    });
    this.variants.clear();
    for (const v of p.variants) {
      const g = this.variantGroup();
      g.patchValue({
        sku: v.sku,
        name: v.name,
        price: v.price,
        compareAtPrice: v.compareAtPrice ?? null,
        barcode: v.barcode ?? '',
      });
      const attrs = g.get('attributes') as FormArray<AttrGroup>;
      for (const [key, value] of Object.entries(v.attributes ?? {})) attrs.push(this.attrGroup(key, value));
      this.variants.push(g);
    }
  }

  private variantGroup() {
    return this.fb.group({
      sku: ['', Validators.required],
      name: ['', Validators.required],
      price: [0 as number, [Validators.required, Validators.min(1)]],
      compareAtPrice: [null as number | null],
      barcode: [''],
      attributes: this.fb.array<AttrGroup>([]),
    });
  }

  private attrGroup(key: string, value: string): AttrGroup {
    return this.fb.group({ key: [key], value: [value] });
  }
}
```

#### `src/Web/apps/admin/src/app/pages/products/product-list.component.ts`

```typescript
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminCatalogApi, ProductSummary, readProblem } from '@shoplab/api-client';
import { VndPipe } from '@shoplab/ui';

@Component({
  selector: 'app-product-list',
  imports: [FormsModule, RouterLink, VndPipe],
  template: `
    <div class="row">
      <h1 class="grow">Sản phẩm</h1>
      <a routerLink="/products/new"><button class="primary">Thêm sản phẩm</button></a>
    </div>
    <form class="row filters" (ngSubmit)="reload()">
      <input name="q" [(ngModel)]="q" placeholder="Tìm theo tên" />
      <label class="row"><input type="checkbox" name="deleted" [(ngModel)]="includeDeleted" (change)="reload()" /> Hiện cả bản đã xoá</label>
      <button type="submit">Tìm</button>
    </form>

    <table>
      <thead><tr><th>Tên</th><th>Giá từ</th><th>Trạng thái</th><th>Bật</th><th></th></tr></thead>
      <tbody>
        @for (p of products(); track p.id) {
          <tr [class.deleted]="p.isDeleted">
            <td><a [routerLink]="['/products', p.id]">{{ p.name }}</a></td>
            <td>{{ p.minPrice | vnd }}</td>
            <td>{{ p.isDeleted ? 'Đã xoá' : p.status }}</td>
            <td>
              @if (!p.isDeleted) {
                <input type="checkbox" [checked]="p.isActive" (change)="toggle(p)" />
              }
            </td>
            <td class="row">
              @if (p.isDeleted) {
                <button (click)="restore(p)">Khôi phục</button>
              } @else {
                <button class="danger" (click)="remove(p)">Xoá</button>
              }
            </td>
          </tr>
        }
      </tbody>
    </table>
    @if (nextCursor()) { <button class="more" (click)="loadMore()">Xem thêm</button> }
  `,
  styles: `
    .filters { margin: 12px 0; }
    tr.deleted td { color: var(--muted); text-decoration: line-through; }
    .more { margin-top: 12px; }
  `,
})
export class ProductListComponent {
  private readonly api = inject(AdminCatalogApi);

  protected q = '';
  protected includeDeleted = false;
  protected readonly products = signal<ProductSummary[]>([]);
  protected readonly nextCursor = signal<string | null>(null);

  constructor() {
    this.reload();
  }

  reload(): void {
    this.fetch(null);
  }

  loadMore(): void {
    this.fetch(this.nextCursor());
  }

  private fetch(cursor: string | null): void {
    this.api.searchProducts({ q: this.q, includeDeleted: this.includeDeleted, cursor, size: 25 }).subscribe((page) => {
      this.products.update((list) => (cursor ? [...list, ...page.items] : page.items));
      this.nextCursor.set(page.nextCursor);
    });
  }

  toggle(p: ProductSummary): void {
    this.api.setActive(p.id, !p.isActive).subscribe(() => this.reload());
  }

  remove(p: ProductSummary): void {
    if (!confirm(`Xoá "${p.name}"? Có thể khôi phục lại sau.`)) return;
    this.api.deleteProduct(p.id).subscribe(() => this.reload());
  }

  restore(p: ProductSummary): void {
    this.api.restore(p.id).subscribe({
      next: () => this.reload(),
      error: (e) => alert(readProblem(e).title),
    });
  }
}
```

**Kiểm tra cuối buổi**

```bash
# :4300 → Sản phẩm → Thêm → lưu → thấy trên storefront; xoá → storefront mất; khôi phục → hiện lại
```

### Buổi 8 — TestKit + test Catalog

**Việc cần làm**

1. Tách `PostgresFixture` sang `tests/TestKit` (mỗi test tạo database riêng: `CreateDatabaseAsync`).
2. `ShopLabApiFactory<TMarker>`: chạy service trong bộ nhớ, thay auth bằng `TestAuthHandler` (header `X-Test-Auth: sub;roles;store`).
3. Viết test theo bảng dưới, chạy, commit + tag.

**Lệnh**

```bash
git rm tests/Catalog.Tests/Infrastructure/PostgresFixture.cs
python3 tools/sln-sync.py
dotnet test tests/Catalog.Tests
```

**Xoá file**

- `tests/Catalog.Tests/Infrastructure/PostgresFixture.cs`

**File**

- `tests/TestKit/PostgresFixture.cs`
- `tests/TestKit/ShopLabApiFactory.cs`
- `tests/TestKit/TestAuthHandler.cs`
- `tests/TestKit/TestKit.csproj`
- `tests/Directory.Build.props` *(sửa)*
- `tests/Catalog.Tests/AuditTests.cs`
- `tests/Catalog.Tests/Catalog.Tests.csproj` *(sửa)*
- `tests/Catalog.Tests/CatalogAuthTests.cs`
- `tests/Catalog.Tests/CatalogDb.cs`
- `tests/Catalog.Tests/SearchProductsTests.cs`
- `tests/Catalog.Tests/SmokeTests.cs` *(sửa)*

#### `tests/TestKit/PostgresFixture.cs`

```csharp
using Npgsql;
using Testcontainers.PostgreSql;

namespace TestKit;

/// <summary>
/// Một container Postgres dùng chung cho cả class test (IClassFixture&lt;PostgresFixture&gt;).
/// Mỗi test gọi CreateDatabaseAsync() để có database riêng, không dính dữ liệu của test khác.
/// Cần Docker đang chạy.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Tạo database mới tên ngẫu nhiên, trả về connection string trỏ vào nó.</summary>
    public async Task<string> CreateDatabaseAsync(string prefix = "test")
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(40, prefix.Length + 33)];
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ToString();
    }
}
```

#### `tests/TestKit/ShopLabApiFactory.cs`

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace TestKit;

/// <summary>
/// Dựng một service thật trong bộ nhớ để gọi HTTP:
/// - connection string trỏ vào container của test (đặt qua Settings)
/// - xác thực thay bằng TestAuthHandler
/// - môi trường Development để service tự tạo schema + seed như khi chạy local
/// </summary>
public class ShopLabApiFactory<TMarker> : WebApplicationFactory<TMarker> where TMarker : class
{
    public Dictionary<string, string?> Settings { get; } = new();
    public Action<IServiceCollection>? ConfigureServicesHook { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(o =>
                {
                    o.DefaultScheme = TestAuthHandler.Scheme;
                    o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                    o.DefaultChallengeScheme = TestAuthHandler.Scheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            ConfigureServicesHook?.Invoke(services);
        });
    }
}
```

#### `tests/TestKit/TestAuthHandler.cs`

```csharp
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestKit;

/// <summary>
/// Xác thực giả cho test API: header "X-Test-Auth: &lt;sub&gt;;&lt;role1,role2&gt;;&lt;store_id?&gt;".
/// Không có header → chưa đăng nhập (401 ở endpoint cần auth).
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";
    public const string Header = "X-Test-Auth";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value) || string.IsNullOrWhiteSpace(value))
            return Task.FromResult(AuthenticateResult.NoResult());

        var parts = value.ToString().Split(';');
        var sub = parts[0];
        var roles = parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        var claims = new List<Claim>
        {
            new("sub", sub),
            new("preferred_username", $"user-{sub[..8]}"),
            new("email", $"{sub[..8]}@test.local"),
            new("name", $"Test {sub[..8]}"),
        };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
            claims.Add(new Claim("store_id", parts[2]));

        var identity = new ClaimsIdentity(claims, Scheme, "preferred_username", "role");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}

public static class TestUsers
{
    public static readonly Guid Admin = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid Manager = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    public static readonly Guid Cashier = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    public static readonly Guid An = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    public static readonly Guid Binh = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    public static readonly Guid Chi = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    public static readonly Guid StoreQ1 = Guid.Parse("5a000000-0000-0000-0000-000000000001");

    /// <summary>Gắn header xác thực giả cho mọi request của client.</summary>
    public static HttpClient As(this HttpClient client, Guid userId, params string[] roles)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.Header);
        client.DefaultRequestHeaders.Add(TestAuthHandler.Header, $"{userId};{string.Join(',', roles)}");
        return client;
    }

    public static HttpClient AsStaff(this HttpClient client, Guid userId, Guid storeId, params string[] roles)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.Header);
        client.DefaultRequestHeaders.Add(TestAuthHandler.Header, $"{userId};{string.Join(',', roles)};{storeId}");
        return client;
    }

    public static HttpClient AsAnonymous(this HttpClient client)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.Header);
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }
}
```

#### `tests/TestKit/TestKit.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Thư viện hỗ trợ test dùng chung; không phải project test. -->
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <IsTestProject>false</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Testcontainers.Redis" />
    <PackageReference Include="Testcontainers.RabbitMq" />
    <PackageReference Include="Testcontainers.MongoDb" />
    <PackageReference Include="Npgsql" />
  </ItemGroup>
</Project>
```

#### `tests/Directory.Build.props` *(sửa — nội dung đầy đủ sau khi sửa)*

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <!-- Cấu hình chung cho mọi project test: xUnit v3 + Shouldly + Testcontainers -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <!-- Item được đánh giá sau mọi property, nên csproj đặt IsTestProject=false (TestKit) sẽ bỏ qua nhóm này. -->
  <ItemGroup Condition="'$(IsTestProject)' == 'true'">
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup Condition="'$(IsTestProject)' != 'true'">
    <PackageReference Include="xunit.v3.extensibility.core" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Shouldly" />
    <Using Include="Xunit" />
    <Using Include="Shouldly" />
  </ItemGroup>
</Project>
```

#### `tests/Catalog.Tests/AuditTests.cs`

```csharp
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;
using TestKit;

namespace Catalog.Tests;

/// <summary>
/// Kiểm AuditInterceptor + soft delete trên Postgres thật.
/// Chạy: dotnet test tests/Catalog.Tests --filter AuditTests
/// </summary>
public sealed class AuditTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Create_sets_CreatedAt_equal_UpdatedAt()
    {
        var (newDb, clock) = await CatalogDb.CreateAsync(pg);
        var category = await CatalogDb.AddCategoryAsync(newDb);

        await using var db = newDb();
        var saved = await db.Categories.SingleAsync(c => c.Id == category.Id);

        saved.CreatedAt.ShouldBe(clock.GetUtcNow());
        saved.UpdatedAt.ShouldBe(saved.CreatedAt);
        saved.IsDeleted.ShouldBeFalse();
        saved.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_changes_only_UpdatedAt()
    {
        var (newDb, clock) = await CatalogDb.CreateAsync(pg);
        var category = await CatalogDb.AddCategoryAsync(newDb);
        var createdAt = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromHours(2));
        await using (var db = newDb())
        {
            var c = await db.Categories.SingleAsync(x => x.Id == category.Id);
            c.Update("Điện tử mới", c.Slug, null);
            await db.SaveChangesAsync();
        }

        await using var check = newDb();
        var saved = await check.Categories.SingleAsync(c => c.Id == category.Id);
        saved.CreatedAt.ShouldBe(createdAt);
        saved.UpdatedAt.ShouldBe(createdAt.AddHours(2));
    }

    [Fact]
    public async Task Remove_becomes_soft_delete_hidden_from_normal_queries()
    {
        var (newDb, _) = await CatalogDb.CreateAsync(pg);
        var category = await CatalogDb.AddCategoryAsync(newDb);

        await using (var db = newDb())
        {
            var c = await db.Categories.SingleAsync(x => x.Id == category.Id);
            db.Categories.Remove(c);
            await db.SaveChangesAsync();
        }

        await using var check = newDb();
        (await check.Categories.AnyAsync(c => c.Id == category.Id)).ShouldBeFalse();
        var raw = await check.Categories.IgnoreQueryFilters().SingleAsync(c => c.Id == category.Id);
        raw.IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Soft_deleted_sku_can_be_created_again()
    {
        var (newDb, _) = await CatalogDb.CreateAsync(pg);
        var category = await CatalogDb.AddCategoryAsync(newDb);

        Guid firstId;
        await using (var db = newDb())
        {
            var first = CatalogDb.NewProduct(category.Id, "Sản phẩm A", "SKU-REUSE");
            db.Products.Add(first);
            await db.SaveChangesAsync();
            firstId = first.Id;
        }
        await using (var db = newDb())
        {
            var first = await db.Products.Include(p => p.Variants).SingleAsync(p => p.Id == firstId);
            db.Variants.RemoveRange(first.Variants);
            db.Products.Remove(first);
            await db.SaveChangesAsync();
        }

        await using (var db = newDb())
        {
            // unique index có lọc is_deleted = false nên không lỗi trùng
            db.Products.Add(CatalogDb.NewProduct(category.Id, "Sản phẩm A", "SKU-REUSE"));
            await Should.NotThrowAsync(() => db.SaveChangesAsync());
        }
    }
}
```

#### `tests/Catalog.Tests/Catalog.Tests.csproj` *(sửa — nội dung đầy đủ sau khi sửa)*

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\TestKit\TestKit.csproj" />
    <ProjectReference Include="..\..\src\Services\Catalog.Api\Catalog.Api.csproj" />
  </ItemGroup>
</Project>
```

#### `tests/Catalog.Tests/CatalogAuthTests.cs`

```csharp
using System.Net;
using Catalog.Api;
using TestKit;

namespace Catalog.Tests;

/// <summary>Gọi HTTP vào Catalog chạy trong bộ nhớ: phân quyền API admin.</summary>
public sealed class CatalogAuthTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<HttpClient> CreateClientAsync()
    {
        var factory = new ShopLabApiFactory<ApiMarker>();
        factory.Settings["ConnectionStrings:catalogdb"] = await pg.CreateDatabaseAsync("catalogapi");
        return factory.CreateClient();
    }

    [Fact]
    public async Task Anonymous_can_browse_products()
    {
        var client = await CreateClientAsync();
        var res = await client.GetAsync("/products", TestContext.Current.CancellationToken);
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Anonymous_gets_401_on_admin_api()
    {
        var client = await CreateClientAsync();
        var res = await client.GetAsync("/admin/products", TestContext.Current.CancellationToken);
        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Customer_gets_403_on_admin_api()
    {
        var client = (await CreateClientAsync()).As(TestUsers.An, "customer");
        var res = await client.GetAsync("/admin/products", TestContext.Current.CancellationToken);
        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_gets_200_on_admin_api_and_sees_seeded_products()
    {
        var client = (await CreateClientAsync()).As(TestUsers.Admin, "admin");
        var res = await client.GetAsync("/admin/products?size=50", TestContext.Current.CancellationToken);
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("Tai nghe SL Buds Pro");
    }
}
```

#### `tests/Catalog.Tests/CatalogDb.cs`

```csharp
using BuildingBlocks.Persistence;
using Catalog.Api.Data;
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using TestKit;

namespace Catalog.Tests;

/// <summary>Tạo CatalogDbContext trên database mới của container, có AuditInterceptor với đồng hồ giả.</summary>
internal static class CatalogDb
{
    public static async Task<(Func<CatalogDbContext> New, FakeTimeProvider Clock)> CreateAsync(PostgresFixture pg)
    {
        var cs = await pg.CreateDatabaseAsync("catalog");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        CatalogDbContext New() => new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new AuditInterceptor(clock))
            .Options);

        await using var db = New();
        await db.Database.EnsureCreatedAsync();
        return (New, clock);
    }

    public static async Task<Category> AddCategoryAsync(Func<CatalogDbContext> newDb, string slug = "dien-tu")
    {
        await using var db = newDb();
        var category = Category.Create(slug, slug);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    public static Product NewProduct(Guid categoryId, string name, string sku, long price = 100_000)
    {
        // slug kèm SKU để nhiều sản phẩm trùng tên không vi phạm unique slug
        var p = Product.Create(name, $"{name}-{sku}", "", categoryId, "ShopLab", ProductStatus.Active);
        p.SyncVariants([new VariantInput(sku, "Mặc định", price, null, null, null)]);
        return p;
    }
}
```

#### `tests/Catalog.Tests/SearchProductsTests.cs`

```csharp
using Catalog.Api.Domain;
using Catalog.Api.Features.Products;
using TestKit;

namespace Catalog.Tests;

/// <summary>Phân trang keyset: không lặp, không sót; bản đã xoá hoặc đã tắt không hiện cho khách.</summary>
public sealed class SearchProductsTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Paging_returns_every_product_exactly_once()
    {
        var (newDb, _) = await CatalogDb.CreateAsync(pg);
        var category = await CatalogDb.AddCategoryAsync(newDb);
        await using (var db = newDb())
        {
            // Tên trùng nhau theo cặp để thử nhánh so sánh theo Id khi Name bằng nhau
            for (var i = 0; i < 25; i++)
                db.Products.Add(CatalogDb.NewProduct(category.Id, $"Sản phẩm {i / 2:D2}", $"SKU-{i:D3}"));
            await db.SaveChangesAsync();
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            await using var db = newDb();
            var page = await ProductSearch.ExecuteAsync(db, new ProductSearchRequest(null, null, cursor, 10), CancellationToken.None);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null);

        pages.ShouldBe(3);
        seen.Count.ShouldBe(25);
        seen.Distinct().Count().ShouldBe(25);
    }

    [Fact]
    public async Task Deleted_inactive_and_draft_products_are_hidden_from_storefront()
    {
        var (newDb, _) = await CatalogDb.CreateAsync(pg);
        var category = await CatalogDb.AddCategoryAsync(newDb);
        Product visible, inactive, deleted, draft;
        await using (var db = newDb())
        {
            visible = CatalogDb.NewProduct(category.Id, "Hiện", "SKU-V");
            inactive = CatalogDb.NewProduct(category.Id, "Tắt", "SKU-I");
            deleted = CatalogDb.NewProduct(category.Id, "Xoá", "SKU-D");
            draft = Product.Create("Nháp", "nhap", "", category.Id, "", ProductStatus.Draft);
            draft.SyncVariants([new VariantInput("SKU-DR", "x", 1000, null, null, null)]);
            inactive.SetActive(false);
            db.Products.AddRange(visible, inactive, deleted, draft);
            await db.SaveChangesAsync();
            db.Products.Remove(deleted);
            await db.SaveChangesAsync();
        }

        await using var check = newDb();
        var storefront = await ProductSearch.ExecuteAsync(check, new ProductSearchRequest(null, null, null, 50), CancellationToken.None);
        storefront.Items.Select(i => i.Name).ShouldBe(["Hiện"]);

        var admin = await ProductSearch.ExecuteAsync(check,
            new ProductSearchRequest(null, null, null, 50, VisibleOnly: false, IncludeDeleted: true), CancellationToken.None);
        admin.Items.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Search_ignores_vietnamese_diacritics()
    {
        var (newDb, _) = await CatalogDb.CreateAsync(pg);
        var category = await CatalogDb.AddCategoryAsync(newDb);
        await using (var db = newDb())
        {
            db.Products.Add(CatalogDb.NewProduct(category.Id, "Bình giữ nhiệt", "SKU-B"));
            db.Products.Add(CatalogDb.NewProduct(category.Id, "Balo", "SKU-L"));
            await db.SaveChangesAsync();
        }

        await using var check = newDb();
        var result = await ProductSearch.ExecuteAsync(check, new ProductSearchRequest(null, "binh", null, 10), CancellationToken.None);
        result.Items.Single().Name.ShouldBe("Bình giữ nhiệt");
    }
}
```

#### `tests/Catalog.Tests/SmokeTests.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using Npgsql;
using TestKit;

namespace Catalog.Tests;

/// <summary>S0: chứng minh bộ test kết nối được Postgres thật trong container.</summary>
public sealed class SmokeTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Can_connect_to_postgres()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand("select 1", conn);

        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        result.ShouldBe(1);
    }
}
```

**Kiểm tra cuối buổi**

```bash
dotnet test tests/Catalog.Tests --logger "console;verbosity=normal"
git add -A && git commit -m "s1: web catalog" && git tag s1-done
```

> Test: AuditTests (Create_sets_CreatedAt_equal_UpdatedAt, Update_changes_only_UpdatedAt, Remove_becomes_soft_delete_hidden_from_normal_queries, Soft_deleted_sku_can_be_created_again) · SearchProductsTests (Paging_returns_every_product_exactly_once, Deleted_inactive_and_draft_products_are_hidden_from_storefront, Search_ignores_vietnamese_diacritics) · CatalogAuthTests (anonymous 200 / 401 admin, customer 403, admin 200).

**Xong tuần 2 khi:**

- [ ] Storefront duyệt + tìm được
- [ ] Admin CRUD được
- [ ] `dotnet test` xanh, tag `s1-done`

---

## Gate S1

| # | Kiểm tra |
| --- | --- |
| 1 | `dotnet test tests/Catalog.Tests` xanh |
| 2 | Tìm "binh" ra "Bình giữ nhiệt" (không dấu) |
| 3 | Phân trang `size=2` đi hết danh mục không trùng, không sót |
| 4 | Xoá mềm → storefront không thấy; tạo lại sản phẩm cùng SKU được; khôi phục được |
| 5 | Customer gọi `/api/admin/catalog/*` → 403 |
