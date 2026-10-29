# ShopLab — S2: Đặt hàng đồng bộ

Mục tiêu của S2: **mua được hàng từ đầu đến cuối** — hồ sơ khách, giỏ hàng, tồn kho, thanh toán giả, đặt hàng —
với cách đơn giản nhất: Ordering **gọi HTTP đồng bộ** lần lượt Cart → Customer → Catalog → Inventory (giữ hàng) → Payment → Inventory (chốt).

Cách này **cố ý có lỗi**: service chết giữa chừng làm đơn kẹt, payment chậm làm khách bị trừ tiền cho đơn đã huỷ, bấm hai lần ra hai đơn.
Cuối S2 bạn đo và ghi lại các lỗi đó (`docs/notes/s2-failures.md`), S3 sửa bằng event + saga.

Service mới: Customer (5102), Cart (5103, Redis), Inventory (5104, sổ cái), Ordering (5105), Payment (5106).

**Thời lượng:** 3 tuần · 13 buổi (mỗi buổi ~2–3 giờ ngoài giờ làm). **File:** 117 tạo mới, 16 sửa.

## Lịch

| Tuần | Mục tiêu | Buổi |
| --- | --- | --- |
| Tuần 1 | Customer và Inventory (sổ cái tồn kho, chống bán vượt) | Buổi 1: new-service.py + Customer domain/DbContext/seed<br>Buổi 2: Customer API + test<br>Buổi 3: Inventory domain: sổ cái + giữ hàng<br>Buổi 4: Inventory API<br>Buổi 5: Test Inventory: đồng thời và đối soát |
| Tuần 2 | Cart, Payment giả, Ordering checkout đồng bộ | Buổi 6: Catalog internal variant + Cart (Redis)<br>Buổi 7: Payment giả + Chaos middleware<br>Buổi 8: Ordering checkout đồng bộ + AppHost/Gateway đầy đủ<br>Buổi 9: Test kịch bản gãy + double submit |
| Tuần 3 | UI mua hàng, admin vận hành, đo tải | Buổi 10: api-client + giỏ hàng storefront<br>Buổi 11: Checkout, đơn hàng, hồ sơ<br>Buổi 12: Admin: khách hàng, tồn kho, đơn hàng<br>Buổi 13: k6 baseline + ghi chú lỗi |

## Trước khi bắt đầu

```bash
git checkout s1-done -b s2
chmod +x tools/*.sh
```

---

## Tuần 1 — Customer và Inventory (sổ cái tồn kho, chống bán vượt)

**Mục tiêu tuần**

- Script `tools/new-service.py` tạo khung service theo quy ước repo.
- Customer: hồ sơ tạo từ token Keycloak (`/me/ensure`), địa chỉ (một mặc định), loyalty, admin khoá/mở khoá, API nội bộ cho Ordering.
- Inventory: mỗi thay đổi tồn là một dòng `StockMovement` (append-only); `StockLevel` là số tổng hợp; giữ hàng bằng `Reservation`.
- Chứng minh bằng test: 50 request giữ song song trên 10 tồn → đúng 10 thắng.

### Buổi 1 — new-service.py + Customer domain/DbContext/seed

**Việc cần làm**

1. Viết `tools/new-service.py` (csproj + launchSettings + ApiMarker + appsettings), chạy cho Customer.
2. `Customer` (Id = sub của Keycloak), `Address`, `LoyaltyAccount` (điểm, hạng Member/Silver/Gold).
3. Seed 3 khách trùng ID với user Keycloak (`seed/customers.json`), chi.le bị khoá để test.
4. AppHost: thêm `var customerDb = pg.AddDatabase("customerdb");` và project customer (bản AppHost đầy đủ của S2 ở Buổi 8).

**Lệnh**

```bash
python3 tools/new-service.py Customer 5102
python3 tools/sln-sync.py
dotnet build src/Services/Customer.Api
```

**File**

- `tools/new-service.py`
- `src/Services/Customer.Api/Customer.Api.csproj`
- `src/Services/Customer.Api/ApiMarker.cs`
- `src/Services/Customer.Api/Domain/Customer.cs`
- `src/Services/Customer.Api/Data/CustomerDbContext.cs`
- `src/Services/Customer.Api/Data/SeedData.cs`
- `seed/customers.json`

#### `tools/new-service.py`

```python
#!/usr/bin/env python3
"""Tạo khung một service .NET theo quy ước của repo (thay cho `dotnet new web` + sửa csproj bằng tay).

  python3 tools/new-service.py Inventory 5104            # service có EF + seed
  python3 tools/new-service.py Cart 5103 --no-ef         # service không dùng Postgres
  python3 tools/new-service.py Bff.Admin 5201 --gateway  # đặt trong src/Gateways

Sau đó chạy python3 tools/sln-sync.py.
"""
import sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
args = sys.argv[1:]
name, port = args[0], args[1]
no_ef = "--no-ef" in args
gateway = "--gateway" in args

folder = root / ("src/Gateways" if gateway else "src/Services") / (name if gateway else f"{name}.Api")
proj_name = folder.name
folder.mkdir(parents=True, exist_ok=True)
(folder / "Properties").mkdir(exist_ok=True)

ef = "" if no_ef else """
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>"""

(folder / f"{proj_name}.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../../contracts/openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOptions>--file-name {proj_name}</OpenApiGenerateDocumentsOptions>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.ApiDescription.Server">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>{ef}
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\\..\\ServiceDefaults\\ServiceDefaults.csproj" />
    <ProjectReference Include="..\\..\\BuildingBlocks\\BuildingBlocks.csproj" />
    <ProjectReference Include="..\\..\\Contracts\\Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="..\\..\\..\\seed\\**\\*.json" Link="seed\\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
""", encoding="utf-8")

(folder / "Properties/launchSettings.json").write_text(f"""{{
  "profiles": {{
    "http": {{
      "commandName": "Project",
      "applicationUrl": "http://localhost:{port}",
      "environmentVariables": {{ "ASPNETCORE_ENVIRONMENT": "Development" }}
    }}
  }}
}}
""", encoding="utf-8")

ns = proj_name
(folder / "ApiMarker.cs").write_text(f"""namespace {ns};

/// <summary>Kiểu đánh dấu assembly: dùng cho WebApplicationFactory&lt;ApiMarker&gt; trong test và quét endpoint.</summary>
public sealed class ApiMarker;
""", encoding="utf-8")
(folder / "appsettings.json").write_text("""{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*"
}
""", encoding="utf-8")
print(f"Đã tạo {folder.relative_to(root)}")
```

#### `src/Services/Customer.Api/Customer.Api.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../../contracts/openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOptions>--file-name Customer.Api</OpenApiGenerateDocumentsOptions>
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
    <None Include="..\..\..\seed\**\*.json" Link="seed\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

#### `src/Services/Customer.Api/ApiMarker.cs`

```csharp
namespace Customer.Api;

/// <summary>Kiểu đánh dấu assembly: dùng cho WebApplicationFactory&lt;ApiMarker&gt; trong test và quét endpoint.</summary>
public sealed class ApiMarker;
```

#### `src/Services/Customer.Api/Domain/Customer.cs`

```csharp
using BuildingBlocks.Persistence;

namespace Customer.Api.Domain;

public enum CustomerStatus { Active = 0, Banned = 1 }

public enum LoyaltyTier { Member = 0, Silver = 1, Gold = 2 }

/// <summary>Id = claim "sub" của Keycloak, nên mọi service dùng chung một CustomerId.</summary>
public sealed class Customer : Auditable
{
    public Guid Id { get; private set; }
    public string KeycloakUserId { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string? Phone { get; private set; }
    public string FullName { get; private set; } = default!;
    public CustomerStatus Status { get; private set; } = CustomerStatus.Active;

    public List<Address> Addresses { get; private set; } = [];
    public LoyaltyAccount Loyalty { get; private set; } = default!;

    private Customer() { }

    public static Customer Create(Guid id, string email, string fullName, string? phone = null) => new()
    {
        Id = id,
        KeycloakUserId = id.ToString(),
        Email = email.Trim().ToLowerInvariant(),
        FullName = fullName.Trim(),
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
        Loyalty = LoyaltyAccount.Create(id)
    };

    public void UpdateProfile(string fullName, string? phone)
    {
        FullName = fullName.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
    }

    public void Ban() => Status = CustomerStatus.Banned;
    public void Unban() => Status = CustomerStatus.Active;

    /// <summary>Đặt một địa chỉ làm mặc định, bỏ mặc định ở các địa chỉ còn lại.</summary>
    public void SetDefaultAddress(Guid addressId)
    {
        foreach (var a in Addresses.Where(a => !a.IsDeleted))
            a.IsDefault = a.Id == addressId;
    }
}

public sealed class Address : Auditable
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid CustomerId { get; private set; }
    public string Line1 { get; private set; } = default!;
    public string Ward { get; private set; } = default!;
    public string District { get; private set; } = default!;
    public string City { get; private set; } = default!;
    public bool IsDefault { get; set; }

    private Address() { }

    public static Address Create(Guid customerId, string line1, string ward, string district, string city) =>
        new() { CustomerId = customerId, Line1 = line1.Trim(), Ward = ward.Trim(), District = district.Trim(), City = city.Trim() };

    public void Update(string line1, string ward, string district, string city)
    {
        Line1 = line1.Trim();
        Ward = ward.Trim();
        District = district.Trim();
        City = city.Trim();
    }

    public override string ToString() => $"{Line1}, {Ward}, {District}, {City}";
}

public sealed class LoyaltyAccount : Auditable
{
    public Guid CustomerId { get; private set; }
    public int Points { get; private set; }
    public LoyaltyTier Tier { get; private set; } = LoyaltyTier.Member;

    private LoyaltyAccount() { }

    public static LoyaltyAccount Create(Guid customerId) => new() { CustomerId = customerId };

    /// <summary>1 điểm cho mỗi 10.000 ₫. Hạng: Silver từ 500 điểm, Gold từ 1.000 điểm.</summary>
    public int AddPointsForAmount(long amount)
    {
        var earned = (int)(amount / 10_000);
        Points += earned;
        Tier = Points >= 1000 ? LoyaltyTier.Gold : Points >= 500 ? LoyaltyTier.Silver : LoyaltyTier.Member;
        return earned;
    }

    public void SetForSeed(int points, LoyaltyTier tier)
    {
        Points = points;
        Tier = tier;
    }
}
```

#### `src/Services/Customer.Api/Data/CustomerDbContext.cs`

```csharp
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.Data;

public sealed class CustomerDbContext(DbContextOptions<CustomerDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Customer> Customers => Set<Domain.Customer>();
    public DbSet<Domain.Address> Addresses => Set<Domain.Address>();
    public DbSet<Domain.LoyaltyAccount> Loyalty => Set<Domain.LoyaltyAccount>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("customer");

        b.Entity<Domain.Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.KeycloakUserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Phone).IsUnique().HasFilter("is_deleted = false AND phone IS NOT NULL");
            e.HasIndex(x => x.Email);
            e.HasMany(x => x.Addresses).WithOne().HasForeignKey(a => a.CustomerId);
            e.HasOne(x => x.Loyalty).WithOne().HasForeignKey<Domain.LoyaltyAccount>(l => l.CustomerId);
        });

        b.Entity<Domain.Address>(e =>
        {
            e.ToTable("addresses");
            e.HasKey(x => x.Id);
            e.Property(x => x.Line1).HasMaxLength(300).IsRequired();
            e.Property(x => x.Ward).HasMaxLength(100);
            e.Property(x => x.District).HasMaxLength(100);
            e.Property(x => x.City).HasMaxLength(100);
        });

        b.Entity<Domain.LoyaltyAccount>(e =>
        {
            e.ToTable("loyalty_accounts");
            e.HasKey(x => x.CustomerId);
            e.Property(x => x.Tier).HasConversion<string>().HasMaxLength(20);
        });

        b.ApplySoftDeleteFilters();
    }
}

public sealed class CustomerDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<CustomerDbContext>
{
    public CustomerDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CustomerDbContext>()
        .UseNpgsql("Host=localhost;Database=customerdb;Username=postgres;Password=postgres")
        .UseSnakeCaseNamingConvention().Options);
}
```

#### `src/Services/Customer.Api/Data/SeedData.cs`

```csharp
using System.Text.Json;
using Customer.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.Data;

public static class SeedData
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AddressSeed(string Line1, string Ward, string District, string City, bool IsDefault);
    private sealed record CustomerSeed(Guid Id, string Email, string FullName, string? Phone, string Status,
        int Points, string Tier, List<AddressSeed> Addresses);

    public static async Task RunAsync(CustomerDbContext db, IServiceProvider _)
    {
        if (await db.Customers.IgnoreQueryFilters().AnyAsync()) return;

        var path = Path.Combine(AppContext.BaseDirectory, "seed", "customers.json");
        var seeds = JsonSerializer.Deserialize<List<CustomerSeed>>(await File.ReadAllTextAsync(path), Json)!;
        foreach (var s in seeds)
        {
            var c = Domain.Customer.Create(s.Id, s.Email, s.FullName, s.Phone);
            if (s.Status == nameof(CustomerStatus.Banned)) c.Ban();
            c.Loyalty.SetForSeed(s.Points, Enum.Parse<LoyaltyTier>(s.Tier));
            foreach (var a in s.Addresses)
            {
                var address = Address.Create(c.Id, a.Line1, a.Ward, a.District, a.City);
                address.IsDefault = a.IsDefault;
                c.Addresses.Add(address);
            }
            db.Customers.Add(c);
        }
        await db.SaveChangesAsync();
    }
}
```

#### `seed/customers.json`

```json
[
  {
    "id": "00000000-0000-0000-0000-0000000000c1", "email": "an.nguyen@shoplab.local", "fullName": "Nguyễn An",
    "phone": "0901000001", "status": "Active", "points": 1200, "tier": "Gold",
    "addresses": [
      { "line1": "12 Nguyễn Huệ", "ward": "Bến Nghé", "district": "Quận 1", "city": "TP.HCM", "isDefault": true }
    ]
  },
  {
    "id": "00000000-0000-0000-0000-0000000000c2", "email": "binh.tran@shoplab.local", "fullName": "Trần Bình",
    "phone": "0901000002", "status": "Active", "points": 80, "tier": "Member",
    "addresses": [
      { "line1": "45 Xuân Thuỷ", "ward": "Dịch Vọng Hậu", "district": "Cầu Giấy", "city": "Hà Nội", "isDefault": true }
    ]
  },
  {
    "id": "00000000-0000-0000-0000-0000000000c3", "email": "chi.le@shoplab.local", "fullName": "Lê Chi",
    "phone": "0901000003", "status": "Banned", "points": 0, "tier": "Member",
    "addresses": []
  }
]
```

### Buổi 2 — Customer API + test

**Việc cần làm**

1. `/me/ensure` idempotent (gọi nhiều lần vẫn một hồ sơ), `/me` xem/sửa, `/me/addresses` CRUD.
2. Admin: danh sách keyset theo email, chi tiết, ban/unban.
3. Nội bộ: `/internal/customers/{id}/status`, địa chỉ mặc định / theo id (Ordering gọi).
4. Test `CustomerApiTests` bằng `ShopLabApiFactory`.

**Lệnh**

```bash
mkdir -p tests/Customer.Tests && python3 tools/sln-sync.py
dotnet test tests/Customer.Tests
```

**File**

- `src/Services/Customer.Api/Features/Addresses/Addresses.cs`
- `src/Services/Customer.Api/Features/Admin/AdminCustomers.cs`
- `src/Services/Customer.Api/Features/CustomerDtos.cs`
- `src/Services/Customer.Api/Features/Internal/InternalCustomers.cs`
- `src/Services/Customer.Api/Features/Me/EnsureMe.cs`
- `src/Services/Customer.Api/Features/Me/GetMe.cs`
- `src/Services/Customer.Api/Features/Me/UpdateMe.cs`
- `src/Services/Customer.Api/Program.cs`
- `src/Services/Customer.Api/appsettings.json`
- `src/Services/Customer.Api/Properties/launchSettings.json`
- `http/customer.http`
- `tests/Customer.Tests/Customer.Tests.csproj`
- `tests/Customer.Tests/CustomerApiTests.cs`

#### `src/Services/Customer.Api/Features/Addresses/Addresses.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Customer.Api.Data;
using Customer.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.Features.Addresses;

public sealed record SaveAddressRequest(
    [property: Required, StringLength(300)] string Line1,
    [property: Required, StringLength(100)] string Ward,
    [property: Required, StringLength(100)] string District,
    [property: Required, StringLength(100)] string City,
    bool IsDefault = false);

/// <summary>Địa chỉ của khách đang đăng nhập: thêm, sửa, xoá mềm, đặt mặc định (luôn chỉ một địa chỉ mặc định).</summary>
public sealed class Addresses : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/me/addresses").RequireAuthorization(Policies.Authenticated).WithTags("Addresses");

        group.MapGet("/", async (ICurrentUser user, CustomerDbContext db, CancellationToken ct) =>
            await db.Addresses.AsNoTracking().Where(a => a.CustomerId == user.Id)
                .OrderByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt)
                .Select(a => new AddressDto(a.Id, a.Line1, a.Ward, a.District, a.City, a.IsDefault))
                .ToListAsync(ct));

        group.MapPost("/", async (SaveAddressRequest req, ICurrentUser user, CustomerDbContext db, CancellationToken ct) =>
        {
            var customer = await Load(db, user.Id, ct);
            var address = Address.Create(customer.Id, req.Line1, req.Ward, req.District, req.City);
            customer.Addresses.Add(address);
            // Địa chỉ đầu tiên luôn là mặc định
            if (req.IsDefault || customer.Addresses.Count(a => !a.IsDeleted) == 1)
                customer.SetDefaultAddress(address.Id);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/me/addresses/{address.Id}", address.ToDto());
        });

        group.MapPut("/{id:guid}", async (Guid id, SaveAddressRequest req, ICurrentUser user, CustomerDbContext db, CancellationToken ct) =>
        {
            var customer = await Load(db, user.Id, ct);
            var address = customer.Addresses.FirstOrDefault(a => a.Id == id) ?? throw NotFoundException.For("address", id);
            address.Update(req.Line1, req.Ward, req.District, req.City);
            if (req.IsDefault) customer.SetDefaultAddress(id);
            await db.SaveChangesAsync(ct);
            return Results.Ok(address.ToDto());
        });

        group.MapPost("/{id:guid}/default", async (Guid id, ICurrentUser user, CustomerDbContext db, CancellationToken ct) =>
        {
            var customer = await Load(db, user.Id, ct);
            if (customer.Addresses.All(a => a.Id != id)) throw NotFoundException.For("address", id);
            customer.SetDefaultAddress(id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (Guid id, ICurrentUser user, CustomerDbContext db, CancellationToken ct) =>
        {
            var customer = await Load(db, user.Id, ct);
            var address = customer.Addresses.FirstOrDefault(a => a.Id == id) ?? throw NotFoundException.For("address", id);
            db.Addresses.Remove(address); // xoá mềm
            address.IsDefault = false;
            var next = customer.Addresses.FirstOrDefault(a => a.Id != id && !a.IsDeleted);
            if (next is not null && customer.Addresses.All(a => !a.IsDefault || a.Id == id))
                next.IsDefault = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<Domain.Customer> Load(CustomerDbContext db, Guid id, CancellationToken ct) =>
        await db.Customers.Include(c => c.Addresses).Include(c => c.Loyalty).FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw NotFoundException.For("customer", id);
}
```

#### `src/Services/Customer.Api/Features/Admin/AdminCustomers.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Paging;
using BuildingBlocks.Results;
using Customer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.Features.Admin;

public sealed record CustomerRowDto(Guid Id, string Email, string FullName, string? Phone, string Status, int Points, string Tier);

/// <summary>Admin: danh sách (keyset theo email), chi tiết, ban, unban.</summary>
public sealed class AdminCustomers : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/customers").RequireAuthorization(Policies.Admin).WithTags("Admin.Customers");

        group.MapGet("/", async (string? q, string? cursor, int? size, CustomerDbContext db, CancellationToken ct) =>
        {
            var take = PagingDefaults.Clamp(size);
            var query = db.Customers.AsNoTracking().Include(c => c.Loyalty).AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLowerInvariant();
                query = query.Where(c => c.Email.Contains(term) || c.FullName.ToLower().Contains(term) || (c.Phone != null && c.Phone.Contains(term)));
            }
            var after = KeysetCursor.Decode(cursor);
            if (after is not null)
                query = query.Where(c => string.Compare(c.Email, after.Key) > 0 || (c.Email == after.Key && c.Id.CompareTo(after.Id) > 0));

            var rows = await query.OrderBy(c => c.Email).ThenBy(c => c.Id).Take(take + 1).ToListAsync(ct);
            string? next = null;
            if (rows.Count > take)
            {
                rows.RemoveAt(take);
                next = new KeysetCursor(rows[^1].Email, rows[^1].Id).Encode();
            }
            return new PagedResult<CustomerRowDto>(rows.Select(c => new CustomerRowDto(
                c.Id, c.Email, c.FullName, c.Phone, c.Status.ToString(), c.Loyalty.Points, c.Loyalty.Tier.ToString())).ToList(), next);
        });

        group.MapGet("/{id:guid}", async (Guid id, CustomerDbContext db, CancellationToken ct) =>
        {
            var c = await db.Customers.AsNoTracking().Include(x => x.Addresses).Include(x => x.Loyalty)
                        .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFoundException.For("customer", id);
            return Results.Ok(c.ToDto());
        });

        group.MapPost("/{id:guid}/ban", async (Guid id, CustomerDbContext db, CancellationToken ct) =>
        {
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFoundException.For("customer", id);
            c.Ban();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/unban", async (Guid id, CustomerDbContext db, CancellationToken ct) =>
        {
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFoundException.For("customer", id);
            c.Unban();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
```

#### `src/Services/Customer.Api/Features/CustomerDtos.cs`

```csharp
using Customer.Api.Domain;

namespace Customer.Api.Features;

public sealed record AddressDto(Guid Id, string Line1, string Ward, string District, string City, bool IsDefault);

public sealed record CustomerDto(
    Guid Id, string Email, string FullName, string? Phone, string Status,
    int Points, string Tier, IReadOnlyList<AddressDto> Addresses, DateTimeOffset CreatedAt);

public static class CustomerMapping
{
    public static AddressDto ToDto(this Address a) => new(a.Id, a.Line1, a.Ward, a.District, a.City, a.IsDefault);

    public static CustomerDto ToDto(this Domain.Customer c) => new(
        c.Id, c.Email, c.FullName, c.Phone, c.Status.ToString(),
        c.Loyalty?.Points ?? 0, (c.Loyalty?.Tier ?? LoyaltyTier.Member).ToString(),
        c.Addresses.Where(a => !a.IsDeleted).OrderByDescending(a => a.IsDefault).Select(a => a.ToDto()).ToList(),
        c.CreatedAt);
}
```

#### `src/Services/Customer.Api/Features/Internal/InternalCustomers.cs`

```csharp
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Customer.Api.Data;
using Customer.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.Features.Internal;

public sealed record CustomerStatusDto(Guid Id, string Status);

/// <summary>
/// Route nội bộ cho service khác gọi (gateway chặn mọi đường dẫn chứa /internal).
/// Lab không xác thực service-to-service; dự án thật nên dùng client credentials hoặc mTLS.
/// </summary>
public sealed class InternalCustomers : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/customers").AllowAnonymous().WithTags("Internal");

        // Khách chưa từng gọi /me/ensure (vd user ảo của k6) được coi là Active.
        group.MapGet("/{id:guid}/status", async (Guid id, CustomerDbContext db, CancellationToken ct) =>
        {
            var status = await db.Customers.AsNoTracking().Where(c => c.Id == id).Select(c => (CustomerStatus?)c.Status).FirstOrDefaultAsync(ct);
            return new CustomerStatusDto(id, (status ?? CustomerStatus.Active).ToString());
        });

        group.MapGet("/{id:guid}/addresses/{addressId:guid}", async (Guid id, Guid addressId, CustomerDbContext db, CancellationToken ct) =>
        {
            var a = await db.Addresses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == addressId && x.CustomerId == id, ct)
                    ?? throw NotFoundException.For("address", addressId);
            return Results.Ok(a.ToDto());
        });

        group.MapGet("/{id:guid}/addresses/default", async (Guid id, CustomerDbContext db, CancellationToken ct) =>
        {
            var a = await db.Addresses.AsNoTracking().Where(x => x.CustomerId == id).OrderByDescending(x => x.IsDefault).FirstOrDefaultAsync(ct);
            return a is null ? Results.NotFound() : Results.Ok(a.ToDto());
        });
    }
}
```

#### `src/Services/Customer.Api/Features/Me/EnsureMe.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using Customer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.Features.Me;

/// <summary>
/// POST /me/ensure — storefront gọi ngay sau khi đăng nhập. Chưa có hồ sơ thì tạo từ claim của Keycloak.
/// Gọi nhiều lần vẫn chỉ có một bản ghi (Id = sub là khoá chính).
/// </summary>
public sealed class EnsureMe : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/me/ensure", async (ICurrentUser user, CustomerDbContext db, CancellationToken ct) =>
            {
                var customer = await db.Customers.Include(c => c.Addresses).Include(c => c.Loyalty)
                    .FirstOrDefaultAsync(c => c.Id == user.Id, ct);
                if (customer is null)
                {
                    customer = Domain.Customer.Create(user.Id, user.Email ?? $"{user.Id}@unknown", user.FullName ?? user.Username ?? "Khách");
                    db.Customers.Add(customer);
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException)
                    {
                        // Hai tab đăng nhập cùng lúc: bản kia đã tạo trước, đọc lại.
                        db.ChangeTracker.Clear();
                        customer = await db.Customers.Include(c => c.Addresses).Include(c => c.Loyalty).FirstAsync(c => c.Id == user.Id, ct);
                    }
                }
                return Results.Ok(customer.ToDto());
            })
            .RequireAuthorization(Policies.Authenticated)
            .WithTags("Me");
}
```

#### `src/Services/Customer.Api/Features/Me/GetMe.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Customer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.Features.Me;

public sealed class GetMe : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/me", async (ICurrentUser user, CustomerDbContext db, CancellationToken ct) =>
            {
                var c = await db.Customers.AsNoTracking().Include(x => x.Addresses).Include(x => x.Loyalty)
                            .FirstOrDefaultAsync(x => x.Id == user.Id, ct)
                        ?? throw NotFoundException.For("customer", user.Id);
                return Results.Ok(c.ToDto());
            })
            .RequireAuthorization(Policies.Authenticated)
            .WithTags("Me");
}
```

#### `src/Services/Customer.Api/Features/Me/UpdateMe.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Customer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.Features.Me;

public sealed record UpdateMeRequest(
    [property: Required, StringLength(200, MinimumLength = 2)] string FullName,
    [property: RegularExpression(@"^0\d{9}$", ErrorMessage = "Số điện thoại gồm 10 chữ số, bắt đầu bằng 0")] string? Phone);

public sealed class UpdateMe : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/me", async (UpdateMeRequest req, ICurrentUser user, CustomerDbContext db, CancellationToken ct) =>
            {
                var c = await db.Customers.Include(x => x.Addresses).Include(x => x.Loyalty)
                            .FirstOrDefaultAsync(x => x.Id == user.Id, ct)
                        ?? throw NotFoundException.For("customer", user.Id);
                if (req.Phone is not null && await db.Customers.AnyAsync(x => x.Phone == req.Phone && x.Id != c.Id, ct))
                    throw new ConflictException("PHONE_TAKEN", "Số điện thoại đã được dùng cho tài khoản khác.");
                c.UpdateProfile(req.FullName, req.Phone);
                await db.SaveChangesAsync(ct);
                return Results.Ok(c.ToDto());
            })
            .RequireAuthorization(Policies.Authenticated)
            .WithTags("Me");
}
```

#### `src/Services/Customer.Api/Program.cs`

```csharp
using BuildingBlocks;
using BuildingBlocks.Persistence;
using Customer.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddShopLabDbContext<CustomerDbContext>("customerdb");

var app = builder.Build();

await app.InitializeDatabaseAsync<CustomerDbContext>(SeedData.RunAsync);
app.UseShopLabApi(typeof(Customer.Api.ApiMarker).Assembly);

app.Run();
```

#### `src/Services/Customer.Api/appsettings.json`

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*"
}
```

#### `src/Services/Customer.Api/Properties/launchSettings.json`

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5102",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

#### `http/customer.http`

```http
@gateway = http://localhost:5000
@token = paste-customer-token
@admin = paste-admin-token

### Tạo hồ sơ lần đầu (gọi 2 lần vẫn 1 bản ghi)
POST {{gateway}}/api/customer/me/ensure
Authorization: Bearer {{token}}

### Hồ sơ
GET {{gateway}}/api/customer/me
Authorization: Bearer {{token}}

### Thêm địa chỉ
POST {{gateway}}/api/customer/me/addresses
Authorization: Bearer {{token}}
Content-Type: application/json

{ "line1": "99 Lê Lợi", "ward": "Bến Thành", "district": "Quận 1", "city": "TP.HCM", "isDefault": true }

### Admin: danh sách khách
GET {{gateway}}/api/admin/customer/customers?q=an
Authorization: Bearer {{admin}}

### Admin: khoá an.nguyen
POST {{gateway}}/api/admin/customer/customers/00000000-0000-0000-0000-0000000000c1/ban
Authorization: Bearer {{admin}}

### Admin: mở khoá
POST {{gateway}}/api/admin/customer/customers/00000000-0000-0000-0000-0000000000c1/unban
Authorization: Bearer {{admin}}
```

#### `tests/Customer.Tests/Customer.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\TestKit\TestKit.csproj" />
    <ProjectReference Include="..\..\src\Services\Customer.Api\Customer.Api.csproj" />
  </ItemGroup>
</Project>
```

#### `tests/Customer.Tests/CustomerApiTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using Customer.Api;
using Customer.Api.Features;
using TestKit;

namespace Customer.Tests;

/// <summary>Chạy Customer.Api trong bộ nhớ trên Postgres container, có seed 3 khách mẫu.</summary>
public sealed class CustomerApiTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<HttpClient> ClientAsync()
    {
        var factory = new ShopLabApiFactory<ApiMarker>();
        factory.Settings["ConnectionStrings:customerdb"] = await pg.CreateDatabaseAsync("customer");
        return factory.CreateClient();
    }

    [Fact]
    public async Task EnsureMe_twice_creates_one_profile()
    {
        var newUser = Guid.NewGuid();
        var client = (await ClientAsync()).As(newUser, "customer");

        var first = await client.PostAsync("/me/ensure", null, TestContext.Current.CancellationToken);
        var second = await client.PostAsync("/me/ensure", null, TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var a = await first.Content.ReadFromJsonAsync<CustomerDto>(TestContext.Current.CancellationToken);
        var b = await second.Content.ReadFromJsonAsync<CustomerDto>(TestContext.Current.CancellationToken);
        a!.Id.ShouldBe(newUser);
        b!.Id.ShouldBe(newUser);
    }

    [Fact]
    public async Task Only_one_address_is_default()
    {
        var client = (await ClientAsync()).As(Guid.NewGuid(), "customer");
        var ct = TestContext.Current.CancellationToken;
        await client.PostAsync("/me/ensure", null, ct);

        var first = await (await client.PostAsJsonAsync("/me/addresses", new { line1 = "1 A", ward = "W", district = "D", city = "C" }, ct))
            .Content.ReadFromJsonAsync<AddressDto>(ct);
        var second = await (await client.PostAsJsonAsync("/me/addresses", new { line1 = "2 B", ward = "W", district = "D", city = "C", isDefault = true }, ct))
            .Content.ReadFromJsonAsync<AddressDto>(ct);

        first!.IsDefault.ShouldBeTrue(); // địa chỉ đầu tiên tự là mặc định
        var list = await client.GetFromJsonAsync<List<AddressDto>>("/me/addresses", ct);
        list!.Count(a => a.IsDefault).ShouldBe(1);
        list.Single(a => a.IsDefault).Id.ShouldBe(second!.Id);
    }

    [Fact]
    public async Task Seeded_banned_customer_reports_banned_status()
    {
        var client = await ClientAsync();
        var status = await client.GetFromJsonAsync<Customer.Api.Features.Internal.CustomerStatusDto>(
            $"/internal/customers/{TestUsers.Chi}/status", TestContext.Current.CancellationToken);
        status!.Status.ShouldBe("Banned");
    }

    [Fact]
    public async Task Customer_cannot_call_admin_api()
    {
        var client = (await ClientAsync()).As(TestUsers.An, "customer");
        var res = await client.GetAsync("/admin/customers", TestContext.Current.CancellationToken);
        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_can_ban_and_unban()
    {
        var client = (await ClientAsync()).As(TestUsers.Admin, "admin");
        var ct = TestContext.Current.CancellationToken;
        (await client.PostAsync($"/admin/customers/{TestUsers.An}/ban", null, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var detail = await client.GetFromJsonAsync<CustomerDto>($"/admin/customers/{TestUsers.An}", ct);
        detail!.Status.ShouldBe("Banned");
        (await client.PostAsync($"/admin/customers/{TestUsers.An}/unban", null, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
```

**Kiểm tra cuối buổi**

```bash
dotnet test tests/Customer.Tests   # 5 test: EnsureMe_twice…, Only_one_address_is_default, Seeded_banned…, Customer_cannot_call_admin_api, Admin_can_ban_and_unban
```

### Buổi 3 — Inventory domain: sổ cái + giữ hàng

**Việc cần làm**

1. `Location` (kho WH-HCM, WH-HN bán online; cửa hàng ST-Q1, ST-CG), `StockLevel` (OnHand, Reserved, Available, xmin).
2. `StockMovement`: Receipt/Adjustment/TransferIn/Out/Sale/…; unique (RefType, RefId, Sku, Type, Location) — gửi lại cùng phiếu bị DB chặn.
3. `StockLedger`: Receive, Adjust, Transfer, Reserve (chọn kho bán online còn hàng), Commit, Release; `SaveAsync` retry khi xung đột xmin.
4. Seed `seed/locations.json`, `seed/stock.json`.

**Lệnh**

```bash
python3 tools/new-service.py Inventory 5104 && python3 tools/sln-sync.py
```

**File**

- `src/Services/Inventory.Api/Inventory.Api.csproj`
- `src/Services/Inventory.Api/ApiMarker.cs`
- `src/Services/Inventory.Api/Domain/Location.cs`
- `src/Services/Inventory.Api/Domain/Reservation.cs`
- `src/Services/Inventory.Api/Domain/StockLedger.cs`
- `src/Services/Inventory.Api/Domain/StockLevel.cs`
- `src/Services/Inventory.Api/Domain/StockMovement.cs`
- `src/Services/Inventory.Api/Data/InventoryDbContext.cs`
- `src/Services/Inventory.Api/Data/SeedData.cs`
- `seed/locations.json`
- `seed/stock.json`

#### `src/Services/Inventory.Api/Inventory.Api.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../../contracts/openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOptions>--file-name Inventory.Api</OpenApiGenerateDocumentsOptions>
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
    <None Include="..\..\..\seed\**\*.json" Link="seed\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Inventory.Tests" />
  </ItemGroup>
</Project>
```

#### `src/Services/Inventory.Api/ApiMarker.cs`

```csharp
namespace Inventory.Api;

/// <summary>Kiểu đánh dấu assembly: dùng cho WebApplicationFactory&lt;ApiMarker&gt; trong test và quét endpoint.</summary>
public sealed class ApiMarker;
```

#### `src/Services/Inventory.Api/Domain/Location.cs`

```csharp
using BuildingBlocks.Persistence;

namespace Inventory.Api.Domain;

public enum LocationType { Warehouse = 0, Store = 1 }

/// <summary>Kho tổng hoặc cửa hàng. Cửa hàng cũng là một location để POS trừ đúng chỗ.</summary>
public sealed class Location : Auditable
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public LocationType Type { get; private set; }
    /// <summary>Kho được dùng để giữ hàng cho đơn online.</summary>
    public bool SellableOnline { get; private set; }

    private Location() { }

    public static Location Create(string code, string name, LocationType type, bool sellableOnline, Guid? id = null) =>
        new() { Id = id ?? Guid.CreateVersion7(), Code = code.ToUpperInvariant(), Name = name, Type = type, SellableOnline = sellableOnline };
}
```

#### `src/Services/Inventory.Api/Domain/Reservation.cs`

```csharp
using BuildingBlocks.Persistence;

namespace Inventory.Api.Domain;

public enum ReservationStatus { Active = 0, Committed = 1, Released = 2 }

/// <summary>Giữ hàng cho một dòng của đơn online chưa thanh toán.</summary>
public sealed class Reservation : Auditable
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrderId { get; private set; }
    public string Sku { get; private set; } = default!;
    public Guid LocationId { get; private set; }
    public int Qty { get; private set; }
    public ReservationStatus Status { get; private set; } = ReservationStatus.Active;
    public DateTimeOffset ExpiresAt { get; private set; }

    private Reservation() { }

    public static Reservation Create(Guid orderId, string sku, Guid locationId, int qty, DateTimeOffset expiresAt) =>
        new() { OrderId = orderId, Sku = sku, LocationId = locationId, Qty = qty, ExpiresAt = expiresAt };

    public void MarkCommitted() => Status = ReservationStatus.Committed;
    public void MarkReleased() => Status = ReservationStatus.Released;
}
```

#### `src/Services/Inventory.Api/Domain/StockLedger.cs`

```csharp
using BuildingBlocks.Results;
using Inventory.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Domain;

public sealed record ReserveLine(string Sku, int Qty);

/// <summary>
/// Mọi thay đổi tồn kho đi qua đây: cập nhật StockLevel và ghi StockMovement trong cùng một SaveChanges.
/// Không gọi SaveChanges bên trong; handler gọi một lần ở cuối để mọi thay đổi nằm trong một transaction.
/// </summary>
public sealed class StockLedger(InventoryDbContext db, TimeProvider clock)
{
    public async Task<Location> GetLocationAsync(string code, CancellationToken ct) =>
        await db.Locations.FirstOrDefaultAsync(l => l.Code == code.ToUpperInvariant(), ct)
        ?? throw new BadRequestException("LOCATION_NOT_FOUND", $"Không có location '{code}'.");

    public async Task<StockLevel> GetOrCreateLevelAsync(string sku, Guid locationId, CancellationToken ct)
    {
        sku = sku.ToUpperInvariant();
        var level = db.StockLevels.Local.FirstOrDefault(s => s.Sku == sku && s.LocationId == locationId)
                    ?? await db.StockLevels.FirstOrDefaultAsync(s => s.Sku == sku && s.LocationId == locationId, ct);
        if (level is null)
        {
            level = StockLevel.Create(sku, locationId);
            db.StockLevels.Add(level);
        }
        return level;
    }

    /// <summary>Ghi một biến động OnHand. Qty dương là nhập, âm là xuất.</summary>
    public async Task<StockMovement> RecordAsync(
        string sku, Guid locationId, int qty, MovementType type, string refType, string refId, string by,
        string? note, CancellationToken ct, bool allowNegative = false)
    {
        if (qty == 0) throw new BadRequestException("INVALID_QTY", "Số lượng thay đổi phải khác 0.");
        var level = await GetOrCreateLevelAsync(sku, locationId, ct);
        level.ApplyMovement(qty, allowNegative);
        var movement = StockMovement.Create(level.Sku, locationId, qty, type, refType, refId, by, note);
        db.Movements.Add(movement);
        return movement;
    }

    public async Task ReceiveAsync(string sku, string locationCode, int qty, string refId, string by, string? note, CancellationToken ct)
    {
        if (qty <= 0) throw new BadRequestException("INVALID_QTY", "Số lượng nhập phải lớn hơn 0.");
        var location = await GetLocationAsync(locationCode, ct);
        await RecordAsync(sku, location.Id, qty, MovementType.Receipt, "Receipt", refId, by, note, ct);
    }

    /// <summary>Kiểm kê: đếm được countedQty, ghi movement Adjustment bằng chênh lệch.</summary>
    public async Task<int> AdjustAsync(string sku, string locationCode, int countedQty, string refId, string by, string? reason, CancellationToken ct)
    {
        var location = await GetLocationAsync(locationCode, ct);
        var level = await GetOrCreateLevelAsync(sku, location.Id, ct);
        var delta = countedQty - level.OnHand;
        if (delta != 0)
            await RecordAsync(sku, location.Id, delta, MovementType.Adjustment, "Stocktake", refId, by, reason, ct);
        return delta;
    }

    public async Task TransferAsync(string sku, string fromCode, string toCode, int qty, string refId, string by, CancellationToken ct)
    {
        if (qty <= 0) throw new BadRequestException("INVALID_QTY", "Số lượng chuyển phải lớn hơn 0.");
        var from = await GetLocationAsync(fromCode, ct);
        var to = await GetLocationAsync(toCode, ct);
        if (from.Id == to.Id) throw new BadRequestException("SAME_LOCATION", "Kho đi và kho đến phải khác nhau.");
        await RecordAsync(sku, from.Id, -qty, MovementType.TransferOut, "Transfer", refId, by, $"→ {to.Code}", ct);
        await RecordAsync(sku, to.Id, qty, MovementType.TransferIn, "Transfer", refId, by, $"← {from.Code}", ct);
    }

    /// <summary>
    /// Giữ hàng cho đơn online: mỗi dòng lấy từ một kho bán online còn đủ hàng (ưu tiên theo mã kho).
    /// Gọi lại với cùng orderId không giữ thêm (idempotent).
    /// </summary>
    public async Task<IReadOnlyList<Reservation>> ReserveAsync(Guid orderId, IReadOnlyList<ReserveLine> lines, TimeSpan ttl, CancellationToken ct)
    {
        var existing = await db.Reservations.Where(r => r.OrderId == orderId).ToListAsync(ct);
        if (existing.Count > 0) return existing;

        var sellable = await db.Locations.Where(l => l.SellableOnline && l.IsActive).OrderBy(l => l.Code).ToListAsync(ct);
        var expiresAt = clock.GetUtcNow().Add(ttl);
        var created = new List<Reservation>();

        foreach (var line in lines.GroupBy(l => l.Sku.ToUpperInvariant()).Select(g => new ReserveLine(g.Key, g.Sum(x => x.Qty))))
        {
            var ids = sellable.Select(l => l.Id).ToList();
            var levels = await db.StockLevels.Where(s => s.Sku == line.Sku && ids.Contains(s.LocationId)).ToListAsync(ct);
            var level = sellable.Select(l => levels.FirstOrDefault(s => s.LocationId == l.Id))
                .FirstOrDefault(s => s is not null && s.Available >= line.Qty);
            if (level is null)
            {
                var total = levels.Sum(s => s.Available);
                throw new ConflictException("OUT_OF_STOCK", $"{line.Sku} chỉ còn {total}, không đủ {line.Qty}.");
            }

            level.Reserve(line.Qty);
            var reservation = Reservation.Create(orderId, line.Sku, level.LocationId, line.Qty, expiresAt);
            db.Reservations.Add(reservation);
            created.Add(reservation);
        }
        return created;
    }

    /// <summary>Đơn đã thanh toán: hàng giữ thành hàng bán, ghi movement Sale.</summary>
    public async Task<int> CommitAsync(Guid orderId, string by, CancellationToken ct)
    {
        var reservations = await db.Reservations.Where(r => r.OrderId == orderId && r.Status == ReservationStatus.Active).ToListAsync(ct);
        foreach (var r in reservations)
        {
            var level = await GetOrCreateLevelAsync(r.Sku, r.LocationId, ct);
            level.Commit(r.Qty);
            db.Movements.Add(StockMovement.Create(r.Sku, r.LocationId, -r.Qty, MovementType.Sale, "Order", orderId.ToString(), by));
            r.MarkCommitted();
        }
        return reservations.Count;
    }

    /// <summary>Đơn bị huỷ: trả lại phần đã giữ. Không có movement vì OnHand không đổi.</summary>
    public async Task<int> ReleaseAsync(Guid orderId, CancellationToken ct)
    {
        var reservations = await db.Reservations.Where(r => r.OrderId == orderId && r.Status == ReservationStatus.Active).ToListAsync(ct);
        foreach (var r in reservations)
        {
            var level = await GetOrCreateLevelAsync(r.Sku, r.LocationId, ct);
            level.Release(r.Qty);
            r.MarkReleased();
        }
        return reservations.Count;
    }
}

public static class ConcurrencyRetry
{
    /// <summary>
    /// Chạy lại toàn bộ thao tác khi xmin của StockLevel đã đổi (người khác vừa ghi). Tối đa 3 lần.
    /// Mỗi lần thử đọc lại dữ liệu mới từ DB.
    /// </summary>
    public static async Task<T> RunAsync<T>(DbContext db, Func<Task<T>> action, int attempts = 3)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                var result = await action();
                await db.SaveChangesAsync();
                return result;
            }
            catch (DbUpdateConcurrencyException) when (i < attempts)
            {
                db.ChangeTracker.Clear();
                await Task.Delay(Random.Shared.Next(5, 25) * i);
            }
        }
    }
}
```

#### `src/Services/Inventory.Api/Domain/StockLevel.cs`

```csharp
using BuildingBlocks.Persistence;
using BuildingBlocks.Results;

namespace Inventory.Api.Domain;

/// <summary>
/// Tồn của một SKU tại một location. Đây là projection của sổ cái: OnHand luôn bằng tổng Qty các StockMovement.
/// Reserved là phần đã giữ cho đơn chưa thanh toán (không phải movement vì hàng chưa rời kho).
/// Version map vào cột hệ thống xmin của Postgres → optimistic concurrency.
/// </summary>
public sealed class StockLevel : Auditable
{
    public string Sku { get; private set; } = default!;
    public Guid LocationId { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }
    public uint Version { get; private set; }

    public int Available => OnHand - Reserved;

    private StockLevel() { }

    public static StockLevel Create(string sku, Guid locationId) => new() { Sku = sku, LocationId = locationId };

    public void Reserve(int qty)
    {
        EnsurePositive(qty);
        if (qty > Available)
            throw new ConflictException("OUT_OF_STOCK", $"{Sku} chỉ còn {Available}, không đủ {qty}.");
        Reserved += qty;
    }

    public void Release(int qty)
    {
        EnsurePositive(qty);
        if (qty > Reserved)
            throw new InvalidOperationException($"Release {qty} nhưng {Sku} chỉ đang giữ {Reserved}.");
        Reserved -= qty;
    }

    /// <summary>Hàng đã giữ được bán thật: giảm cả Reserved và OnHand. Gọi kèm một movement Sale.</summary>
    public void Commit(int qty)
    {
        EnsurePositive(qty);
        if (qty > Reserved)
            throw new InvalidOperationException($"Commit {qty} nhưng {Sku} chỉ đang giữ {Reserved}.");
        Reserved -= qty;
        OnHand -= qty;
    }

    /// <summary>Chỉ StockLedger gọi, luôn đi kèm một StockMovement có cùng qty.</summary>
    internal void ApplyMovement(int qty, bool allowNegative = false)
    {
        if (!allowNegative && OnHand + qty < Reserved)
            throw new ConflictException("OUT_OF_STOCK", $"{Sku}: còn {OnHand} (đang giữ {Reserved}), không thể trừ {-qty}.");
        OnHand += qty;
    }

    private void EnsurePositive(int qty)
    {
        if (qty <= 0) throw new BadRequestException("INVALID_QTY", "Số lượng phải lớn hơn 0.");
    }
}
```

#### `src/Services/Inventory.Api/Domain/StockMovement.cs`

```csharp
using BuildingBlocks.Persistence;

namespace Inventory.Api.Domain;

public enum MovementType
{
    Receipt, Sale, Return, TransferOut, TransferIn, Adjustment, FlashAllocation, FlashReturn, PosSale
}

/// <summary>
/// Sổ cái append-only: mỗi dòng là một biến động OnHand (Qty có dấu). Không sửa, không xoá;
/// muốn huỷ thì ghi bút toán đảo. Unique (RefType, RefId, Sku, Type) để DB chặn ghi trùng khi retry.
/// </summary>
public sealed class StockMovement : IHasCreatedAt
{
    public long Id { get; private set; }
    public string Sku { get; private set; } = default!;
    public Guid LocationId { get; private set; }
    public int Qty { get; private set; }
    public MovementType Type { get; private set; }
    public string RefType { get; private set; } = default!;
    public string RefId { get; private set; } = default!;
    public string By { get; private set; } = default!;
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }

    private StockMovement() { }

    public static StockMovement Create(string sku, Guid locationId, int qty, MovementType type, string refType, string refId, string by, string? note = null) =>
        new() { Sku = sku, LocationId = locationId, Qty = qty, Type = type, RefType = refType, RefId = refId, By = by, Note = note };
}
```

#### `src/Services/Inventory.Api/Data/InventoryDbContext.cs`

```csharp
using BuildingBlocks.Persistence;
using Inventory.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Data;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<StockMovement> Movements => Set<StockMovement>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("inventory");

        b.Entity<Location>(e =>
        {
            e.ToTable("locations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Code).IsUnique().HasFilter("is_deleted = false");
        });

        b.Entity<StockLevel>(e =>
        {
            e.ToTable("stock_levels");
            e.HasKey(x => new { x.Sku, x.LocationId });
            e.Property(x => x.Sku).HasMaxLength(64);
            // uint + IsRowVersion → Npgsql map vào cột hệ thống xmin: optimistic concurrency không cần cột riêng
            e.Property(x => x.Version).IsRowVersion();
            e.Ignore(x => x.Available);
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId);
        });

        b.Entity<StockMovement>(e =>
        {
            e.ToTable("stock_movements");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Sku).HasMaxLength(64).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.RefType).HasMaxLength(30).IsRequired();
            e.Property(x => x.RefId).HasMaxLength(64).IsRequired();
            e.Property(x => x.By).HasMaxLength(100).IsRequired();
            e.Property(x => x.Note).HasMaxLength(300);
            // retry gửi lại cùng phiếu sẽ bị DB chặn thay vì nhân đôi tồn kho
            e.HasIndex(x => new { x.RefType, x.RefId, x.Sku, x.Type, x.LocationId }).IsUnique();
            e.HasIndex(x => new { x.Sku, x.LocationId, x.Id });
        });

        b.Entity<Reservation>(e =>
        {
            e.ToTable("reservations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.OrderId, x.Sku }).IsUnique();
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
        });

        b.ApplySoftDeleteFilters();
    }
}

public sealed class InventoryDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<InventoryDbContext>()
        .UseNpgsql("Host=localhost;Database=inventorydb;Username=postgres;Password=postgres")
        .UseSnakeCaseNamingConvention().Options);
}
```

#### `src/Services/Inventory.Api/Data/SeedData.cs`

```csharp
using System.Text.Json;
using Inventory.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Api.Data;

/// <summary>Tồn ban đầu được ghi thành movement Receipt với RefType=Seed, để sổ cái khớp ngay từ đầu.</summary>
public static class SeedData
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record LocationSeed(Guid Id, string Code, string Name, string Type, bool SellableOnline);
    private sealed record StockSeed(string Sku, Dictionary<string, int> Qty);

    public static async Task RunAsync(InventoryDbContext db, IServiceProvider sp)
    {
        if (await db.Locations.IgnoreQueryFilters().AnyAsync()) return;

        var dir = Path.Combine(AppContext.BaseDirectory, "seed");
        var locations = JsonSerializer.Deserialize<List<LocationSeed>>(await File.ReadAllTextAsync(Path.Combine(dir, "locations.json")), Json)!;
        foreach (var l in locations)
            db.Locations.Add(Location.Create(l.Code, l.Name, Enum.Parse<LocationType>(l.Type), l.SellableOnline, l.Id));
        await db.SaveChangesAsync();

        var ledger = sp.GetRequiredService<StockLedger>();
        var stock = JsonSerializer.Deserialize<List<StockSeed>>(await File.ReadAllTextAsync(Path.Combine(dir, "stock.json")), Json)!;
        foreach (var s in stock)
        {
            foreach (var (code, qty) in s.Qty)
            {
                var loc = locations.Single(l => l.Code == code);
                await ledger.RecordAsync(s.Sku, loc.Id, qty, MovementType.Receipt, "Seed", "initial", "seed", null, CancellationToken.None);
            }
        }
        await db.SaveChangesAsync();
    }
}
```

#### `seed/locations.json`

```json
[
  { "id": "10000000-0000-0000-0000-000000000001", "code": "WH-HCM", "name": "Kho tổng TP.HCM", "type": "Warehouse", "sellableOnline": true },
  { "id": "10000000-0000-0000-0000-000000000002", "code": "WH-HN", "name": "Kho tổng Hà Nội", "type": "Warehouse", "sellableOnline": true },
  { "id": "10000000-0000-0000-0000-000000000003", "code": "ST-Q1", "name": "SL Store Quận 1", "type": "Store", "sellableOnline": false },
  { "id": "10000000-0000-0000-0000-000000000004", "code": "ST-CG", "name": "SL Store Cầu Giấy", "type": "Store", "sellableOnline": false }
]
```

#### `seed/stock.json`

```json
[
  { "sku": "SL-BUDS-PRO-BLK", "qty": { "WH-HCM": 250, "WH-HN": 120, "ST-Q1": 15, "ST-CG": 10 } },
  { "sku": "SL-CHG-65W-WHT", "qty": { "WH-HCM": 80, "WH-HN": 400, "ST-Q1": 12, "ST-CG": 8 } },
  { "sku": "SL-MOUSE-WL", "qty": { "WH-HCM": 200, "WH-HN": 150, "ST-Q1": 20, "ST-CG": 15 } },
  { "sku": "SL-BOTTLE-500-SLV", "qty": { "WH-HCM": 800, "WH-HN": 300, "ST-Q1": 40, "ST-CG": 30 } },
  { "sku": "SL-BACKPACK-20L", "qty": { "WH-HCM": 150, "WH-HN": 100, "ST-Q1": 10, "ST-CG": 8 } },
  { "sku": "SL-TEE-BASIC-M", "qty": { "WH-HCM": 500, "WH-HN": 300, "ST-Q1": 60, "ST-CG": 50 } },
  { "sku": "SL-NOTE-A5", "qty": { "WH-HCM": 1000, "WH-HN": 800, "ST-Q1": 200, "ST-CG": 150 } }
]
```

**Kiểm tra cuối buổi**

```bash
dotnet build src/Services/Inventory.Api
```

### Buổi 4 — Inventory API

**Việc cần làm**

1. Admin: locations, nhập (receipts), kiểm kê (adjustments), chuyển kho (transfers), tồn thấp, lịch sử movement.
2. Công khai: `/stock/{sku}` (còn hàng online hay không).
3. Nội bộ: `/internal/reservations` giữ hàng theo orderId (gọi lại cùng orderId không giữ thêm), commit, release.

**File**

- `src/Services/Inventory.Api/Features/Admin/AdminStock.cs`
- `src/Services/Inventory.Api/Features/Internal/Reservations.cs`
- `src/Services/Inventory.Api/Features/InventoryDtos.cs`
- `src/Services/Inventory.Api/Features/Stock/GetStockBySku.cs`
- `src/Services/Inventory.Api/Program.cs`
- `src/Services/Inventory.Api/appsettings.json`
- `src/Services/Inventory.Api/Properties/launchSettings.json`
- `http/inventory.http`

#### `src/Services/Inventory.Api/Features/Admin/AdminStock.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using Inventory.Api.Data;
using Inventory.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Features.Admin;

public sealed record ReceiveStockRequest([property: Required] string Sku, [property: Required] string LocationCode,
    [property: Range(1, 1_000_000)] int Qty, string? RefId, string? Note);

public sealed record AdjustStockRequest([property: Required] string Sku, [property: Required] string LocationCode,
    [property: Range(0, 1_000_000)] int CountedQty, string? Reason);

public sealed record TransferStockRequest([property: Required] string Sku, [property: Required] string FromCode,
    [property: Required] string ToCode, [property: Range(1, 1_000_000)] int Qty);

/// <summary>Nhập hàng, kiểm kê, chuyển kho, xem tồn, xem lịch sử movement.</summary>
public sealed class AdminStock : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization(Policies.Admin).WithTags("Admin.Inventory");

        group.MapGet("/locations", async (InventoryDbContext db, CancellationToken ct) =>
            (await db.Locations.AsNoTracking().OrderBy(l => l.Code).ToListAsync(ct)).Select(l => l.ToDto()));

        // ReceiveStock: phiếu nhập; RefId trùng (gửi lại cùng phiếu) bị unique index chặn
        group.MapPost("/stock/receipts", async (ReceiveStockRequest req, StockLedger ledger, InventoryDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var refId = string.IsNullOrWhiteSpace(req.RefId) ? Guid.CreateVersion7().ToString() : req.RefId;
            await ConcurrencyRetry.RunAsync(db, async () =>
            {
                await ledger.ReceiveAsync(req.Sku, req.LocationCode, req.Qty, refId, user.Username ?? "admin", req.Note, ct);
                return 0;
            });
            return Results.Created($"/admin/stock/{req.Sku}/movements", new { refId });
        });

        // AdjustStock: kiểm kê
        group.MapPost("/stock/adjustments", async (AdjustStockRequest req, StockLedger ledger, InventoryDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var delta = await ConcurrencyRetry.RunAsync(db, () =>
                ledger.AdjustAsync(req.Sku, req.LocationCode, req.CountedQty, Guid.CreateVersion7().ToString(), user.Username ?? "admin", req.Reason, ct));
            return Results.Ok(new { delta });
        });

        // TransferStock: TransferOut + TransferIn trong một transaction
        group.MapPost("/stock/transfers", async (TransferStockRequest req, StockLedger ledger, InventoryDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var refId = Guid.CreateVersion7().ToString();
            await ConcurrencyRetry.RunAsync(db, async () =>
            {
                await ledger.TransferAsync(req.Sku, req.FromCode, req.ToCode, req.Qty, refId, user.Username ?? "admin", ct);
                return 0;
            });
            return Results.Ok(new { refId });
        });

        // ListStock: tồn theo location, lọc tồn thấp
        group.MapGet("/stock", async (string? location, int? lowThreshold, InventoryDbContext db, CancellationToken ct) =>
        {
            var query = from s in db.StockLevels.AsNoTracking()
                        join l in db.Locations.AsNoTracking() on s.LocationId equals l.Id
                        select new { s, l };
            if (!string.IsNullOrWhiteSpace(location))
                query = query.Where(x => x.l.Code == location.ToUpper());
            if (lowThreshold is { } t)
                query = query.Where(x => x.s.OnHand - x.s.Reserved <= t);
            var rows = await query.OrderBy(x => x.s.Sku).ThenBy(x => x.l.Code).ToListAsync(ct);
            return rows.Select(x => new StockLevelDto(x.s.Sku, x.l.Id, x.l.Code, x.s.OnHand, x.s.Reserved, x.s.OnHand - x.s.Reserved));
        });

        // ListMovements: lịch sử của một SKU, mới nhất trước
        group.MapGet("/stock/{sku}/movements", async (string sku, long? beforeId, int? size, InventoryDbContext db, CancellationToken ct) =>
        {
            var take = Math.Clamp(size ?? 50, 1, 200);
            var query = from m in db.Movements.AsNoTracking()
                        join l in db.Locations.AsNoTracking() on m.LocationId equals l.Id
                        where m.Sku == sku.ToUpper()
                        select new { m, l.Code };
            if (beforeId is { } b) query = query.Where(x => x.m.Id < b);
            var rows = await query.OrderByDescending(x => x.m.Id).Take(take).ToListAsync(ct);
            return rows.Select(x => new MovementDto(x.m.Id, x.m.Sku, x.Code, x.m.Qty, x.m.Type.ToString(), x.m.RefType, x.m.RefId, x.m.By, x.m.Note, x.m.CreatedAt));
        });
    }
}
```

#### `src/Services/Inventory.Api/Features/Internal/Reservations.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using BuildingBlocks.Endpoints;
using Inventory.Api.Data;
using Inventory.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Features.Internal;

public sealed record CreateReservationRequest(Guid OrderId, [property: Required, MinLength(1)] List<ReserveLine> Lines, int? TtlMinutes);

/// <summary>
/// API nội bộ cho Ordering (S2 gọi HTTP đồng bộ; từ S3 thay bằng event, các endpoint này vẫn giữ để test).
/// Cả ba thao tác đều idempotent theo OrderId.
/// </summary>
public sealed class Reservations : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/reservations").AllowAnonymous().WithTags("Internal");

        // CreateReservation
        group.MapPost("/", async (CreateReservationRequest req, StockLedger ledger, InventoryDbContext db, CancellationToken ct) =>
        {
            var created = await ConcurrencyRetry.RunAsync(db, () =>
                ledger.ReserveAsync(req.OrderId, req.Lines, TimeSpan.FromMinutes(req.TtlMinutes ?? 15), ct));
            return Results.Ok(created.Select(r => r.ToDto()));
        });

        // CommitReservation
        group.MapPost("/{orderId:guid}/commit", async (Guid orderId, StockLedger ledger, InventoryDbContext db, CancellationToken ct) =>
        {
            var count = await ConcurrencyRetry.RunAsync(db, () => ledger.CommitAsync(orderId, "ordering", ct));
            return Results.Ok(new { committed = count });
        });

        // ReleaseReservation
        group.MapPost("/{orderId:guid}/release", async (Guid orderId, StockLedger ledger, InventoryDbContext db, CancellationToken ct) =>
        {
            var count = await ConcurrencyRetry.RunAsync(db, () => ledger.ReleaseAsync(orderId, ct));
            return Results.Ok(new { released = count });
        });

        group.MapGet("/{orderId:guid}", async (Guid orderId, InventoryDbContext db, CancellationToken ct) =>
            (await db.Reservations.AsNoTracking().Where(r => r.OrderId == orderId).ToListAsync(ct)).Select(r => r.ToDto()));
    }
}
```

#### `src/Services/Inventory.Api/Features/InventoryDtos.cs`

```csharp
using Inventory.Api.Domain;

namespace Inventory.Api.Features;

public sealed record LocationDto(Guid Id, string Code, string Name, string Type, bool SellableOnline);
public sealed record StockLevelDto(string Sku, Guid LocationId, string LocationCode, int OnHand, int Reserved, int Available);
public sealed record MovementDto(long Id, string Sku, string LocationCode, int Qty, string Type, string RefType, string RefId, string By, string? Note, DateTimeOffset CreatedAt);
public sealed record ReservationDto(Guid Id, Guid OrderId, string Sku, Guid LocationId, int Qty, string Status, DateTimeOffset ExpiresAt);

public static class InventoryMapping
{
    public static LocationDto ToDto(this Location l) => new(l.Id, l.Code, l.Name, l.Type.ToString(), l.SellableOnline);
    public static ReservationDto ToDto(this Reservation r) => new(r.Id, r.OrderId, r.Sku, r.LocationId, r.Qty, r.Status.ToString(), r.ExpiresAt);
}
```

#### `src/Services/Inventory.Api/Features/Stock/GetStockBySku.cs`

```csharp
using BuildingBlocks.Endpoints;
using Inventory.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Features.Stock;

public sealed record SkuAvailabilityDto(string Sku, int AvailableOnline, IReadOnlyList<StockLevelDto> Locations);

/// <summary>GET /stock/{sku}: tồn theo từng location + tổng còn bán được online.</summary>
public sealed class GetStockBySku : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/stock/{sku}", async (string sku, InventoryDbContext db, CancellationToken ct) =>
            {
                sku = sku.ToUpperInvariant();
                var rows = await (from s in db.StockLevels.AsNoTracking()
                                  join l in db.Locations.AsNoTracking() on s.LocationId equals l.Id
                                  where s.Sku == sku
                                  orderby l.Code
                                  select new { s, l }).ToListAsync(ct);
                var list = rows.Select(x => new StockLevelDto(x.s.Sku, x.l.Id, x.l.Code, x.s.OnHand, x.s.Reserved, x.s.OnHand - x.s.Reserved)).ToList();
                var online = rows.Where(x => x.l.SellableOnline).Sum(x => x.s.OnHand - x.s.Reserved);
                return new SkuAvailabilityDto(sku, online, list);
            })
            .AllowAnonymous()
            .WithTags("Stock");
}
```

#### `src/Services/Inventory.Api/Program.cs`

```csharp
using BuildingBlocks;
using BuildingBlocks.Chaos;
using BuildingBlocks.Persistence;
using Inventory.Api.Data;
using Inventory.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddChaos();
builder.AddShopLabDbContext<InventoryDbContext>("inventorydb");
builder.Services.AddScoped<StockLedger>();

var app = builder.Build();

await app.InitializeDatabaseAsync<InventoryDbContext>(SeedData.RunAsync);
app.UseChaos(app.Environment);
app.UseShopLabApi(typeof(Inventory.Api.ApiMarker).Assembly);

app.Run();
```

#### `src/Services/Inventory.Api/appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Chaos": {
    "DelayMs": 0,
    "FailRate": 0,
    "PathPrefix": "/internal"
  }
}
```

#### `src/Services/Inventory.Api/Properties/launchSettings.json`

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5104",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

#### `http/inventory.http`

```http
@gateway = http://localhost:5000
@admin = paste-admin-token
@inventory = http://localhost:5104

### Công khai: tồn của một SKU
GET {{gateway}}/api/inventory/stock/SL-BUDS-PRO-BLK

### Admin: location
GET {{gateway}}/api/admin/inventory/locations
Authorization: Bearer {{admin}}

### Admin: nhập 10 cái
POST {{gateway}}/api/admin/inventory/stock/receipts
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "sku": "SL-TEE-BASIC-M", "locationCode": "WH-HCM", "qty": 10, "refId": "PN-0001", "note": "Phiếu nhập 0001" }

### Gửi lại cùng refId → 409 DUPLICATE (unique index của sổ cái)
POST {{gateway}}/api/admin/inventory/stock/receipts
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "sku": "SL-TEE-BASIC-M", "locationCode": "WH-HCM", "qty": 10, "refId": "PN-0001" }

### Admin: chuyển 5 cái về cửa hàng Q1
POST {{gateway}}/api/admin/inventory/stock/transfers
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "sku": "SL-BUDS-PRO-BLK", "fromCode": "WH-HCM", "toCode": "ST-Q1", "qty": 5 }

### Admin: kiểm kê, đếm được 245
POST {{gateway}}/api/admin/inventory/stock/adjustments
Authorization: Bearer {{admin}}
Content-Type: application/json

{ "sku": "SL-BUDS-PRO-BLK", "locationCode": "WH-HCM", "countedQty": 245, "reason": "Kiểm kê tháng" }

### Admin: lịch sử movement
GET {{gateway}}/api/admin/inventory/stock/SL-BUDS-PRO-BLK/movements
Authorization: Bearer {{admin}}

### Admin: tồn thấp
GET {{gateway}}/api/admin/inventory/stock?lowThreshold=20
Authorization: Bearer {{admin}}

### Nội bộ (gọi thẳng Inventory, không qua gateway): giữ hàng cho một order
POST {{inventory}}/internal/reservations
Content-Type: application/json

{ "orderId": "11111111-1111-1111-1111-111111111111", "lines": [ { "sku": "SL-NOTE-A5", "qty": 2 } ] }

### Gọi lại cùng orderId → không giữ thêm
POST {{inventory}}/internal/reservations
Content-Type: application/json

{ "orderId": "11111111-1111-1111-1111-111111111111", "lines": [ { "sku": "SL-NOTE-A5", "qty": 2 } ] }

### Release
POST {{inventory}}/internal/reservations/11111111-1111-1111-1111-111111111111/release
```

**Kiểm tra cuối buổi**

```bash
# chạy http/inventory.http: nhập 10 → 201; gửi lại cùng refId → 409 DUPLICATE
```

### Buổi 5 — Test Inventory: đồng thời và đối soát

**Việc cần làm**

1. `InventoryDb` helper tạo DB + location cho test.
2. 50 task song song giữ 1 đơn vị trên 10 tồn → đúng 10 thắng (xmin + retry).
3. Cách thứ hai: `UPDATE … WHERE on_hand - reserved >= qty` (conditional update) cũng không bán vượt.
4. Đối soát: thao tác ngẫu nhiên, tổng movement luôn bằng OnHand.

**Lệnh**

```bash
mkdir -p tests/Inventory.Tests && python3 tools/sln-sync.py && dotnet test tests/Inventory.Tests
```

**File**

- `tests/Inventory.Tests/ConditionalUpdateTests.cs`
- `tests/Inventory.Tests/Inventory.Tests.csproj`
- `tests/Inventory.Tests/InventoryConcurrencyTests.cs`
- `tests/Inventory.Tests/InventoryDb.cs`
- `tests/Inventory.Tests/ReconcileTests.cs`
- `tests/Inventory.Tests/StockLevelTests.cs`

#### `tests/Inventory.Tests/ConditionalUpdateTests.cs`

```csharp
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TestKit;

namespace Inventory.Tests;

/// <summary>
/// So sánh cách thứ hai: một câu UPDATE có điều kiện, không đọc trước, không retry.
/// Kết quả in ra Output của test runner; chép vào docs/notes/s2-concurrency.md.
/// </summary>
public sealed class ConditionalUpdateTests(PostgresFixture pg, ITestOutputHelper output) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Conditional_update_also_never_oversells()
    {
        var newDb = await InventoryDb.CreateAsync(pg);
        await InventoryDb.ReceiveAsync(newDb, "HOT-SKU", "WH-HCM", 10);

        var sw = Stopwatch.StartNew();
        var affected = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            await using var db = newDb();
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.stock_levels
                   SET reserved = reserved + 1
                 WHERE sku = 'HOT-SKU' AND location_id = {InventoryDb.WhHcm} AND on_hand - reserved >= 1
                """);
        }));
        sw.Stop();

        affected.Sum().ShouldBe(10);
        output.WriteLine($"UPDATE có điều kiện: 50 request, {affected.Sum()} thành công, {sw.ElapsedMilliseconds} ms, 0 retry");

        await using var check = newDb();
        (await check.StockLevels.SingleAsync(s => s.Sku == "HOT-SKU")).Reserved.ShouldBe(10);
    }
}
```

#### `tests/Inventory.Tests/Inventory.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\TestKit\TestKit.csproj" />
    <ProjectReference Include="..\..\src\Services\Inventory.Api\Inventory.Api.csproj" />
  </ItemGroup>
</Project>
```

#### `tests/Inventory.Tests/InventoryConcurrencyTests.cs`

```csharp
using Inventory.Api.Domain;
using Microsoft.EntityFrameworkCore;
using TestKit;

namespace Inventory.Tests;

/// <summary>50 request cùng giữ 1 SKU còn 10: đúng 10 thành công, không bán vượt, không Reserved âm.</summary>
public sealed class InventoryConcurrencyTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Fifty_parallel_reservations_on_ten_units_give_exactly_ten_winners()
    {
        var newDb = await InventoryDb.CreateAsync(pg);
        await InventoryDb.ReceiveAsync(newDb, "HOT-SKU", "WH-HCM", 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            await using var db = newDb();
            var ledger = new StockLedger(db, TimeProvider.System);
            try
            {
                // xmin + retry tối đa 3 lần: ai đọc trễ sẽ thử lại với số liệu mới
                await ConcurrencyRetry.RunAsync(db, () =>
                    ledger.ReserveAsync(Guid.NewGuid(), [new ReserveLine("HOT-SKU", 1)], TimeSpan.FromMinutes(15), default), attempts: 50);
                return true;
            }
            catch (BuildingBlocks.Results.ConflictException)
            {
                return false;
            }
        }));

        results.Count(ok => ok).ShouldBe(10);
        await using var check = newDb();
        var level = await check.StockLevels.SingleAsync(s => s.Sku == "HOT-SKU");
        level.Reserved.ShouldBe(10);
        level.Available.ShouldBe(0);
        (await check.Reservations.CountAsync()).ShouldBe(10);
    }
}
```

#### `tests/Inventory.Tests/InventoryDb.cs`

```csharp
using BuildingBlocks.Persistence;
using Inventory.Api.Data;
using Inventory.Api.Domain;
using Microsoft.EntityFrameworkCore;
using TestKit;

namespace Inventory.Tests;

internal static class InventoryDb
{
    public static readonly Guid WhHcm = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid WhHn = Guid.Parse("10000000-0000-0000-0000-000000000002");

    /// <summary>Database mới + 2 kho bán online; trả về hàm tạo DbContext mới (mỗi "request" một context).</summary>
    public static async Task<Func<InventoryDbContext>> CreateAsync(PostgresFixture pg)
    {
        var cs = await pg.CreateDatabaseAsync("inventory");
        InventoryDbContext New() => new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(cs).UseSnakeCaseNamingConvention()
            .AddInterceptors(new AuditInterceptor(TimeProvider.System)).Options);

        await using var db = New();
        await db.Database.EnsureCreatedAsync();
        db.Locations.Add(Location.Create("WH-HCM", "Kho HCM", LocationType.Warehouse, true, WhHcm));
        db.Locations.Add(Location.Create("WH-HN", "Kho HN", LocationType.Warehouse, true, WhHn));
        await db.SaveChangesAsync();
        return New;
    }

    public static async Task ReceiveAsync(Func<InventoryDbContext> newDb, string sku, string location, int qty)
    {
        await using var db = newDb();
        var ledger = new StockLedger(db, TimeProvider.System);
        await ledger.ReceiveAsync(sku, location, qty, Guid.NewGuid().ToString(), "test", null, CancellationToken.None);
        await db.SaveChangesAsync();
    }
}
```

#### `tests/Inventory.Tests/ReconcileTests.cs`

```csharp
using Inventory.Api.Domain;
using Microsoft.EntityFrameworkCore;
using TestKit;

namespace Inventory.Tests;

/// <summary>
/// Bất biến quan trọng nhất của sổ cái: với mọi (SKU, location), tổng Qty của movement = OnHand.
/// Chạy 200 thao tác ngẫu nhiên (nhập, chuyển, kiểm kê, giữ, commit, release) rồi kiểm tra.
/// </summary>
public sealed class ReconcileTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Sum_of_movements_equals_on_hand_after_random_operations()
    {
        var newDb = await InventoryDb.CreateAsync(pg);
        var skus = new[] { "SKU-A", "SKU-B", "SKU-C" };
        var locations = new[] { "WH-HCM", "WH-HN" };
        foreach (var sku in skus)
            foreach (var loc in locations)
                await InventoryDb.ReceiveAsync(newDb, sku, loc, 100);

        var rnd = new Random(42); // cố định seed để lỗi tái hiện được
        var orders = new List<Guid>();
        for (var i = 0; i < 200; i++)
        {
            await using var db = newDb();
            var ledger = new StockLedger(db, TimeProvider.System);
            var sku = skus[rnd.Next(skus.Length)];
            try
            {
                switch (rnd.Next(6))
                {
                    case 0:
                        await ledger.ReceiveAsync(sku, locations[rnd.Next(2)], rnd.Next(1, 20), Guid.NewGuid().ToString(), "t", null, default);
                        break;
                    case 1:
                        await ledger.TransferAsync(sku, "WH-HCM", "WH-HN", rnd.Next(1, 10), Guid.NewGuid().ToString(), "t", default);
                        break;
                    case 2:
                        await ledger.AdjustAsync(sku, locations[rnd.Next(2)], rnd.Next(50, 150), Guid.NewGuid().ToString(), "t", "kiểm kê", default);
                        break;
                    case 3:
                        var orderId = Guid.NewGuid();
                        await ledger.ReserveAsync(orderId, [new ReserveLine(sku, rnd.Next(1, 5))], TimeSpan.FromMinutes(15), default);
                        orders.Add(orderId);
                        break;
                    case 4 when orders.Count > 0:
                        await ledger.CommitAsync(orders[rnd.Next(orders.Count)], "t", default);
                        break;
                    case 5 when orders.Count > 0:
                        await ledger.ReleaseAsync(orders[rnd.Next(orders.Count)], default);
                        break;
                }
                await db.SaveChangesAsync();
            }
            catch (BuildingBlocks.Results.ConflictException)
            {
                // hết hàng là kết quả hợp lệ, bỏ qua
            }
        }

        await using var check = newDb();
        var sums = await check.Movements.GroupBy(m => new { m.Sku, m.LocationId })
            .Select(g => new { g.Key.Sku, g.Key.LocationId, Qty = g.Sum(m => m.Qty) }).ToListAsync();
        var levels = await check.StockLevels.ToListAsync();

        foreach (var level in levels)
        {
            var sum = sums.Single(s => s.Sku == level.Sku && s.LocationId == level.LocationId).Qty;
            level.OnHand.ShouldBe(sum, $"{level.Sku}@{level.LocationId}");
            level.Reserved.ShouldBeInRange(0, level.OnHand);
        }

        var activeReserved = await check.Reservations.Where(r => r.Status == ReservationStatus.Active).SumAsync(r => r.Qty);
        levels.Sum(l => l.Reserved).ShouldBe(activeReserved);
    }
}
```

#### `tests/Inventory.Tests/StockLevelTests.cs`

```csharp
using BuildingBlocks.Results;
using Inventory.Api.Domain;

namespace Inventory.Tests;

/// <summary>Unit test thuần cho luật của StockLevel, không cần Docker.</summary>
public sealed class StockLevelTests
{
    private static StockLevel WithOnHand(int onHand)
    {
        var level = StockLevel.Create("SKU-1", Guid.NewGuid());
        // ApplyMovement là internal; test đi qua đúng đường của StockLedger bằng InternalsVisibleTo
        level.ApplyMovement(onHand);
        return level;
    }

    [Fact]
    public void Reserve_within_available_increases_reserved()
    {
        var level = WithOnHand(10);
        level.Reserve(4);
        level.Reserved.ShouldBe(4);
        level.Available.ShouldBe(6);
        level.OnHand.ShouldBe(10);
    }

    [Fact]
    public void Reserve_more_than_available_throws_out_of_stock()
    {
        var level = WithOnHand(3);
        var ex = Should.Throw<ConflictException>(() => level.Reserve(4));
        ex.Code.ShouldBe("OUT_OF_STOCK");
        level.Reserved.ShouldBe(0);
    }

    [Fact]
    public void Release_never_makes_reserved_negative()
    {
        var level = WithOnHand(10);
        level.Reserve(2);
        Should.Throw<InvalidOperationException>(() => level.Release(3));
        level.Reserved.ShouldBe(2);
    }

    [Fact]
    public void Commit_reduces_both_reserved_and_on_hand()
    {
        var level = WithOnHand(10);
        level.Reserve(3);
        level.Commit(3);
        level.Reserved.ShouldBe(0);
        level.OnHand.ShouldBe(7);
    }

    [Fact]
    public void Outbound_movement_cannot_take_reserved_stock()
    {
        var level = WithOnHand(10);
        level.Reserve(8);
        Should.Throw<ConflictException>(() => level.ApplyMovement(-3)); // còn tự do 2
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_quantities_are_rejected(int qty)
    {
        var level = WithOnHand(10);
        Should.Throw<BadRequestException>(() => level.Reserve(qty));
    }
}
```

**Kiểm tra cuối buổi**

```bash
dotnet test tests/Inventory.Tests
```

**Xong tuần 1 khi:**

- [ ] Customer + Inventory chạy trong AppHost
- [ ] Test Customer/Inventory xanh
- [ ] Ghi kết quả 2 cách chống bán vượt vào `docs/notes/s2-concurrency.md` (Buổi 13)

---

## Tuần 2 — Cart, Payment giả, Ordering checkout đồng bộ

**Mục tiêu tuần**

- Cart trên Redis: giỏ khách vãng lai (header X-Cart-Id) và giỏ user, TTL 48h, gộp khi đăng nhập.
- Payment giả có độ trễ/tỉ lệ lỗi cấu hình được + Chaos middleware để tái hiện sự cố.
- Ordering: checkout đồng bộ gọi lần lượt các service; Idempotency-Key tuỳ chọn.
- Test minh hoạ 3 kịch bản gãy (đỏ có chủ đích).

### Buổi 6 — Catalog internal variant + Cart (Redis)

**Việc cần làm**

1. Catalog: `/internal/variants/{sku}` và `/internal/variants/batch` (giá + còn bán không).
2. `ServiceResponse`: đọc response service khác, ProblemDetails → AppException cùng mã, lỗi mạng → 503.
3. Cart: `ShoppingCart` (snapshot giá lúc thêm), `RedisCartStore` (JSON, TTL 48h), endpoint giỏ + nội bộ cho Ordering.
4. Test `CartStoreTests` với `RedisFixture`.

**Lệnh**

```bash
python3 tools/new-service.py Cart 5103 --no-ef
mkdir -p tests/Cart.Tests && python3 tools/sln-sync.py
dotnet test tests/Cart.Tests
```

**File**

- `src/Services/Catalog.Api/Features/Internal/GetVariant.cs`
- `src/BuildingBlocks/Http/ServiceResponse.cs`
- `src/Services/Cart.Api/ApiMarker.cs`
- `src/Services/Cart.Api/Cart.Api.csproj`
- `src/Services/Cart.Api/Clients/CatalogClient.cs`
- `src/Services/Cart.Api/Data/RedisCartStore.cs`
- `src/Services/Cart.Api/Domain/Cart.cs`
- `src/Services/Cart.Api/Features/CartDtos.cs`
- `src/Services/Cart.Api/Features/CartEndpoints.cs`
- `src/Services/Cart.Api/Features/CartKey.cs`
- `src/Services/Cart.Api/Features/InternalCarts.cs`
- `src/Services/Cart.Api/Program.cs`
- `src/Services/Cart.Api/Properties/launchSettings.json`
- `src/Services/Cart.Api/appsettings.json`
- `http/cart.http`
- `tests/TestKit/RedisFixture.cs`
- `tests/TestKit/TestKit.csproj` *(sửa)*
- `tests/Cart.Tests/Cart.Tests.csproj`
- `tests/Cart.Tests/CartStoreTests.cs`

#### `src/Services/Catalog.Api/Features/Internal/GetVariant.cs`

```csharp
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Catalog.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Internal;

/// <summary>Thông tin một SKU cho Cart/Ordering: tên + giá hiện tại + còn bán không.</summary>
public sealed record VariantInfoDto(
    string Sku,
    string Name,
    string ProductName,
    string ProductSlug,
    Guid CategoryId,
    string CategorySlug,
    long Price,
    long? CompareAtPrice,
    bool IsSellable);

public sealed class GetVariant : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/variants/{sku}", async (string sku, CatalogDbContext db, CancellationToken ct) =>
                await Query(db, [sku.ToUpperInvariant()], ct) is [var v] ? Results.Ok(v) : throw NotFoundException.For("sku", sku))
            .AllowAnonymous()
            .WithTags("Internal");

        // Lấy nhiều SKU một lần (checkout, POS)
        app.MapPost("/internal/variants/batch", async (string[] skus, CatalogDbContext db, CancellationToken ct) =>
                await Query(db, skus.Select(s => s.ToUpperInvariant()).Distinct().ToArray(), ct))
            .AllowAnonymous()
            .WithTags("Internal");
    }

    private static Task<List<VariantInfoDto>> Query(CatalogDbContext db, string[] skus, CancellationToken ct) =>
        (from v in db.Variants.AsNoTracking()
         join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
         join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
         where skus.Contains(v.Sku)
         select new VariantInfoDto(
             v.Sku, v.Name, p.Name, p.Slug, c.Id, c.Slug, v.Price, v.CompareAtPrice,
             p.Status == ProductStatus.Active && p.IsActive))
        .ToListAsync(ct);
}
```

#### `src/BuildingBlocks/Http/ServiceResponse.cs`

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Results;

namespace BuildingBlocks.Http;

/// <summary>
/// Đọc response từ service khác: lỗi ProblemDetails được ném lại thành AppException cùng mã,
/// để lỗi nghiệp vụ (vd OUT_OF_STOCK) đi xuyên qua nhiều service mà không mất ý nghĩa.
/// </summary>
public static class ServiceResponse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<T> ReadAsync<T>(this Task<HttpResponseMessage> call, string service, CancellationToken ct = default)
    {
        using var res = await SendAsync(call, service);
        await EnsureSuccessAsync(res, service, ct);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    public static async Task<T?> ReadOrDefaultAsync<T>(this Task<HttpResponseMessage> call, string service, CancellationToken ct = default)
    {
        using var res = await SendAsync(call, service);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return default;
        await EnsureSuccessAsync(res, service, ct);
        return await res.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    public static async Task EnsureAsync(this Task<HttpResponseMessage> call, string service, CancellationToken ct = default)
    {
        using var res = await SendAsync(call, service);
        await EnsureSuccessAsync(res, service, ct);
    }

    private static async Task<HttpResponseMessage> SendAsync(Task<HttpResponseMessage> call, string service)
    {
        try
        {
            return await call;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException
                                       or Polly.Timeout.TimeoutRejectedException)
        {
            throw new DependencyException($"{service.ToUpperInvariant()}_UNAVAILABLE", $"Không gọi được {service}: {ex.Message}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage res, string service, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;

        string code = $"{service.ToUpperInvariant()}_ERROR", title = $"{service} trả về {(int)res.StatusCode}";
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString() ?? code;
            if (doc.RootElement.TryGetProperty("title", out var t)) title = t.GetString() ?? title;
        }
        catch (JsonException)
        {
            // body không phải JSON (vd 503 của chaos middleware)
        }

        var status = (int)res.StatusCode;
        throw status >= 500
            ? new DependencyException(code, title)
            : new AppException(code, title, status);
    }
}
```

#### `src/Services/Cart.Api/ApiMarker.cs`

```csharp
namespace Cart.Api;

/// <summary>Kiểu đánh dấu assembly: dùng cho WebApplicationFactory&lt;ApiMarker&gt; trong test và quét endpoint.</summary>
public sealed class ApiMarker;
```

#### `src/Services/Cart.Api/Cart.Api.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../../contracts/openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOptions>--file-name Cart.Api</OpenApiGenerateDocumentsOptions>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.ApiDescription.Server">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Aspire.StackExchange.Redis" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\ServiceDefaults\ServiceDefaults.csproj" />
    <ProjectReference Include="..\..\BuildingBlocks\BuildingBlocks.csproj" />
    <ProjectReference Include="..\..\Contracts\Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="..\..\..\seed\**\*.json" Link="seed\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

#### `src/Services/Cart.Api/Clients/CatalogClient.cs`

```csharp
using BuildingBlocks.Http;

namespace Cart.Api.Clients;

public sealed record VariantInfo(string Sku, string Name, string ProductName, string ProductSlug, Guid CategoryId,
    string CategorySlug, long Price, long? CompareAtPrice, bool IsSellable);

/// <summary>Typed HttpClient, BaseAddress "https+http://catalog" được service discovery phân giải.</summary>
public sealed class CatalogClient(HttpClient http)
{
    public Task<VariantInfo?> GetVariantAsync(string sku, CancellationToken ct) =>
        http.GetAsync($"/internal/variants/{Uri.EscapeDataString(sku)}", ct).ReadOrDefaultAsync<VariantInfo>("catalog", ct);
}
```

#### `src/Services/Cart.Api/Data/RedisCartStore.cs`

```csharp
using System.Text.Json;
using Cart.Api.Domain;
using StackExchange.Redis;

namespace Cart.Api.Data;

public interface ICartStore
{
    Task<ShoppingCart> GetAsync(string key);
    Task SaveAsync(ShoppingCart cart);
    Task DeleteAsync(string key);
}

/// <summary>Mỗi lần ghi gia hạn TTL 48 giờ; giỏ bỏ quên tự biến mất.</summary>
public sealed class RedisCartStore(IConnectionMultiplexer redis, TimeProvider clock) : ICartStore
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(48);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IDatabase Db => redis.GetDatabase();

    public static string UserKey(Guid customerId) => $"cart:{customerId}";
    public static string GuestKey(string cartId) => $"cart:guest:{cartId}";

    public async Task<ShoppingCart> GetAsync(string key)
    {
        var raw = await Db.StringGetAsync(key);
        return raw.IsNullOrEmpty
            ? new ShoppingCart { Key = key, UpdatedAt = clock.GetUtcNow() }
            : JsonSerializer.Deserialize<ShoppingCart>(raw.ToString(), Json)!;
    }

    public async Task SaveAsync(ShoppingCart cart)
    {
        cart.UpdatedAt = clock.GetUtcNow();
        if (cart.Lines.Count == 0 && cart.CouponCode is null)
        {
            await Db.KeyDeleteAsync(cart.Key);
            return;
        }
        await Db.StringSetAsync(cart.Key, JsonSerializer.Serialize(cart, Json), Ttl);
    }

    public Task DeleteAsync(string key) => Db.KeyDeleteAsync(key);
}
```

#### `src/Services/Cart.Api/Domain/Cart.cs`

```csharp
using BuildingBlocks.Results;

namespace Cart.Api.Domain;

/// <summary>
/// Giỏ hàng lưu nguyên khối JSON trên Redis (key cart:{customerId} hoặc cart:guest:{id}).
/// Giá là snapshot tại lúc thêm vào giỏ; checkout luôn lấy lại giá mới từ Catalog.
/// </summary>
public sealed class ShoppingCart
{
    public const int MaxQtyPerLine = 99;

    public string Key { get; init; } = default!;
    public string? CouponCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<CartLine> Lines { get; init; } = [];

    public long Subtotal => Lines.Sum(l => l.UnitPriceSnapshot * l.Qty);
    public int ItemCount => Lines.Sum(l => l.Qty);

    public void Add(string sku, int qty, string name, string productSlug, long price)
    {
        if (qty <= 0) throw new BadRequestException("INVALID_QTY", "Số lượng phải lớn hơn 0.");
        var line = Lines.FirstOrDefault(l => l.Sku == sku);
        if (line is null)
        {
            Lines.Add(new CartLine { Sku = sku, Qty = Math.Min(qty, MaxQtyPerLine), NameSnapshot = name, ProductSlug = productSlug, UnitPriceSnapshot = price });
        }
        else
        {
            line.Qty = Math.Min(line.Qty + qty, MaxQtyPerLine);
            line.UnitPriceSnapshot = price;
            line.NameSnapshot = name;
        }
    }

    public void SetQty(string sku, int qty)
    {
        var line = Lines.FirstOrDefault(l => l.Sku == sku) ?? throw NotFoundException.For("cart line", sku);
        if (qty <= 0) Lines.Remove(line);
        else line.Qty = Math.Min(qty, MaxQtyPerLine);
    }

    public void Remove(string sku) => Lines.RemoveAll(l => l.Sku == sku);

    /// <summary>Gộp giỏ vãng lai vào giỏ của user khi đăng nhập: cùng SKU thì cộng dồn số lượng.</summary>
    public void Merge(ShoppingCart other)
    {
        foreach (var l in other.Lines)
            Add(l.Sku, l.Qty, l.NameSnapshot, l.ProductSlug, l.UnitPriceSnapshot);
        CouponCode ??= other.CouponCode;
    }
}

public sealed class CartLine
{
    public string Sku { get; init; } = default!;
    public int Qty { get; set; }
    public long UnitPriceSnapshot { get; set; }
    public string NameSnapshot { get; set; } = default!;
    public string ProductSlug { get; set; } = "";
}
```

#### `src/Services/Cart.Api/Features/CartDtos.cs`

```csharp
using Cart.Api.Domain;

namespace Cart.Api.Features;

public sealed record CartLineDto(string Sku, string Name, string ProductSlug, int Qty, long UnitPrice, long LineTotal);

public sealed record CartDto(string? CouponCode, IReadOnlyList<CartLineDto> Lines, int ItemCount, long Subtotal, DateTimeOffset UpdatedAt);

public static class CartMapping
{
    public static CartDto ToDto(this ShoppingCart c) => new(
        c.CouponCode,
        c.Lines.Select(l => new CartLineDto(l.Sku, l.NameSnapshot, l.ProductSlug, l.Qty, l.UnitPriceSnapshot, l.UnitPriceSnapshot * l.Qty)).ToList(),
        c.ItemCount, c.Subtotal, c.UpdatedAt);
}
```

#### `src/Services/Cart.Api/Features/CartEndpoints.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Cart.Api.Clients;
using Cart.Api.Data;

namespace Cart.Api.Features;

public sealed record AddItemRequest([property: Required] string Sku, [property: Range(1, 99)] int Qty = 1);
public sealed record UpdateQtyRequest([property: Range(0, 99)] int Qty);
public sealed record MergeCartRequest(Guid GuestCartId);

/// <summary>
/// Gateway: /api/cart/* → Cart /*.
/// GetCart, AddItem, UpdateQty, RemoveItem, ClearCart, MergeCart.
/// </summary>
public sealed class CartEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/").AllowAnonymous().WithTags("Cart");

        group.MapGet("/", async (HttpContext http, ICurrentUser user, ICartStore store) =>
            (await store.GetAsync(CartKey.Resolve(http, user))).ToDto());

        group.MapPost("/items", async (AddItemRequest req, HttpContext http, ICurrentUser user, ICartStore store, CatalogClient catalog, CancellationToken ct) =>
        {
            var sku = req.Sku.Trim().ToUpperInvariant();
            var variant = await catalog.GetVariantAsync(sku, ct);
            if (variant is null || !variant.IsSellable)
                throw new BadRequestException("SKU_NOT_SELLABLE", $"{sku} không còn bán.");

            var cart = await store.GetAsync(CartKey.Resolve(http, user));
            cart.Add(sku, req.Qty, $"{variant.ProductName} - {variant.Name}", variant.ProductSlug, variant.Price);
            await store.SaveAsync(cart);
            return cart.ToDto();
        });

        group.MapPut("/items/{sku}", async (string sku, UpdateQtyRequest req, HttpContext http, ICurrentUser user, ICartStore store) =>
        {
            var cart = await store.GetAsync(CartKey.Resolve(http, user));
            cart.SetQty(sku.ToUpperInvariant(), req.Qty);
            await store.SaveAsync(cart);
            return cart.ToDto();
        });

        group.MapDelete("/items/{sku}", async (string sku, HttpContext http, ICurrentUser user, ICartStore store) =>
        {
            var cart = await store.GetAsync(CartKey.Resolve(http, user));
            cart.Remove(sku.ToUpperInvariant());
            await store.SaveAsync(cart);
            return cart.ToDto();
        });

        group.MapDelete("/", async (HttpContext http, ICurrentUser user, ICartStore store) =>
        {
            await store.DeleteAsync(CartKey.Resolve(http, user));
            return Results.NoContent();
        });

        group.MapPost("/merge", async (MergeCartRequest req, ICurrentUser user, ICartStore store) =>
        {
            var target = await store.GetAsync(RedisCartStore.UserKey(user.Id));
            var guestKey = RedisCartStore.GuestKey(req.GuestCartId.ToString());
            var guest = await store.GetAsync(guestKey);
            target.Merge(guest);
            await store.SaveAsync(target);
            await store.DeleteAsync(guestKey);
            return target.ToDto();
        }).RequireAuthorization(Policies.Authenticated);
    }
}
```

#### `src/Services/Cart.Api/Features/CartKey.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Results;
using Cart.Api.Data;
using Microsoft.AspNetCore.Http;

namespace Cart.Api.Features;

/// <summary>Đã đăng nhập → giỏ theo customerId; chưa đăng nhập → giỏ vãng lai theo header X-Cart-Id.</summary>
public static class CartKey
{
    public const string GuestHeader = "X-Cart-Id";

    public static string Resolve(HttpContext http, ICurrentUser user)
    {
        if (user.IsAuthenticated) return RedisCartStore.UserKey(user.Id);
        var guest = http.Request.Headers[GuestHeader].ToString();
        if (!Guid.TryParse(guest, out _))
            throw new BadRequestException("CART_ID_REQUIRED", $"Khách vãng lai cần gửi header {GuestHeader} (một UUID).");
        return RedisCartStore.GuestKey(guest);
    }
}
```

#### `src/Services/Cart.Api/Features/InternalCarts.cs`

```csharp
using BuildingBlocks.Endpoints;
using Cart.Api.Data;

namespace Cart.Api.Features;

/// <summary>Ordering đọc và xoá giỏ của khách khi checkout.</summary>
public sealed class InternalCarts : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/carts").AllowAnonymous().WithTags("Internal");

        group.MapGet("/{customerId:guid}", async (Guid customerId, ICartStore store) =>
            (await store.GetAsync(RedisCartStore.UserKey(customerId))).ToDto());

        group.MapDelete("/{customerId:guid}", async (Guid customerId, ICartStore store) =>
        {
            await store.DeleteAsync(RedisCartStore.UserKey(customerId));
            return Results.NoContent();
        });
    }
}
```

#### `src/Services/Cart.Api/Program.cs`

```csharp
using BuildingBlocks;
using Cart.Api.Clients;
using Cart.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddRedisClient("redis");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICartStore, RedisCartStore>();
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("https+http://catalog"));

var app = builder.Build();

app.UseShopLabApi(typeof(Cart.Api.ApiMarker).Assembly);

app.Run();
```

#### `src/Services/Cart.Api/Properties/launchSettings.json`

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5103",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

#### `src/Services/Cart.Api/appsettings.json`

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*"
}
```

#### `http/cart.http`

```http
@gateway = http://localhost:5000
@token = paste-customer-token
@guest = 7d3f8a2e-0000-4000-8000-000000000001

### Giỏ vãng lai: thêm món
POST {{gateway}}/api/cart/items
X-Cart-Id: {{guest}}
Content-Type: application/json

{ "sku": "SL-TEE-BASIC-M", "qty": 2 }

### Giỏ vãng lai: xem
GET {{gateway}}/api/cart
X-Cart-Id: {{guest}}

### Đăng nhập rồi gộp giỏ vãng lai
POST {{gateway}}/api/cart/merge
Authorization: Bearer {{token}}
Content-Type: application/json

{ "guestCartId": "{{guest}}" }

### Giỏ của user
GET {{gateway}}/api/cart
Authorization: Bearer {{token}}

### Sửa số lượng (0 = xoá dòng)
PUT {{gateway}}/api/cart/items/SL-TEE-BASIC-M
Authorization: Bearer {{token}}
Content-Type: application/json

{ "qty": 3 }
```

#### `tests/TestKit/RedisFixture.cs`

```csharp
using StackExchange.Redis;
using Testcontainers.Redis;

namespace TestKit;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();
    public IConnectionMultiplexer Connection { get; private set; } = default!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString + ",allowAdmin=true");
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }

    public Task FlushAsync() => Connection.GetServer(Connection.GetEndPoints()[0]).FlushAllDatabasesAsync();
}
```

#### `tests/TestKit/TestKit.csproj` *(sửa — nội dung đầy đủ sau khi sửa)*

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
    <PackageReference Include="StackExchange.Redis" />
  </ItemGroup>
</Project>
```

#### `tests/Cart.Tests/Cart.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\TestKit\TestKit.csproj" />
    <ProjectReference Include="..\..\src\Services\Cart.Api\Cart.Api.csproj" />
  </ItemGroup>
</Project>
```

#### `tests/Cart.Tests/CartStoreTests.cs`

```csharp
using Cart.Api.Data;
using Cart.Api.Domain;
using Microsoft.Extensions.Time.Testing;
using TestKit;

namespace Cart.Tests;

/// <summary>Giỏ hàng trên Redis thật (Testcontainers).</summary>
public sealed class CartStoreTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private RedisCartStore Store() => new(redis.Connection, new FakeTimeProvider(DateTimeOffset.UtcNow));

    [Fact]
    public async Task Saving_sets_48h_ttl_and_each_save_renews_it()
    {
        var store = Store();
        var key = RedisCartStore.UserKey(Guid.NewGuid());
        var cart = await store.GetAsync(key);
        cart.Add("SKU-1", 1, "Áo", "ao", 199_000);
        await store.SaveAsync(cart);

        var db = redis.Connection.GetDatabase();
        await db.KeyExpireAsync(key, TimeSpan.FromMinutes(1)); // giả lập đã gần hết hạn
        cart = await store.GetAsync(key);
        cart.SetQty("SKU-1", 2);
        await store.SaveAsync(cart);

        var ttl = await db.KeyTimeToLiveAsync(key);
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeGreaterThan(TimeSpan.FromHours(47));
    }

    [Fact]
    public async Task Merge_adds_quantities_of_the_same_sku()
    {
        var user = new ShoppingCart { Key = "u" };
        user.Add("SKU-1", 2, "Áo", "ao", 199_000);
        var guest = new ShoppingCart { Key = "g" };
        guest.Add("SKU-1", 3, "Áo", "ao", 199_000);
        guest.Add("SKU-2", 1, "Sổ", "so", 49_000);

        user.Merge(guest);

        user.Lines.Single(l => l.Sku == "SKU-1").Qty.ShouldBe(5);
        user.Lines.Single(l => l.Sku == "SKU-2").Qty.ShouldBe(1);
        user.Subtotal.ShouldBe(5 * 199_000 + 49_000);
    }

    [Fact]
    public async Task Empty_cart_is_deleted_instead_of_stored()
    {
        var store = Store();
        var key = RedisCartStore.UserKey(Guid.NewGuid());
        var cart = await store.GetAsync(key);
        cart.Add("SKU-1", 1, "Áo", "ao", 199_000);
        await store.SaveAsync(cart);
        cart.Remove("SKU-1");
        await store.SaveAsync(cart);

        (await redis.Connection.GetDatabase().KeyExistsAsync(key)).ShouldBeFalse();
    }

    [Fact]
    public void Quantity_is_capped_at_99()
    {
        var cart = new ShoppingCart { Key = "x" };
        cart.Add("SKU-1", 60, "A", "a", 1);
        cart.Add("SKU-1", 60, "A", "a", 1);
        cart.Lines.Single().Qty.ShouldBe(ShoppingCart.MaxQtyPerLine);
    }
}
```

**Kiểm tra cuối buổi**

```bash
dotnet test tests/Cart.Tests   # TTL 48h, merge cộng dồn, giỏ rỗng bị xoá
```

### Buổi 7 — Payment giả + Chaos middleware

**Việc cần làm**

1. `Payment` entity, `FakePaymentProvider` (MinLatencyMs/MaxLatencyMs/FailureRate trong appsettings).
2. `/internal/payments` charge theo orderId (gọi lại cùng orderId trả lại payment cũ).
3. `ChaosMiddleware` (BuildingBlocks): cấu hình `Chaos:DelayMs`, `Chaos:FailRate`, `Chaos:PathPrefix`.

**Lệnh**

```bash
python3 tools/new-service.py Payment 5106 && python3 tools/sln-sync.py
```

**File**

- `src/BuildingBlocks/Chaos/ChaosMiddleware.cs`
- `src/Services/Payment.Api/ApiMarker.cs`
- `src/Services/Payment.Api/Data/PaymentDbContext.cs`
- `src/Services/Payment.Api/Domain/Payment.cs`
- `src/Services/Payment.Api/FakeGateway/FakePaymentProvider.cs`
- `src/Services/Payment.Api/Features/Internal/Payments.cs`
- `src/Services/Payment.Api/Payment.Api.csproj`
- `src/Services/Payment.Api/Program.cs`
- `src/Services/Payment.Api/Properties/launchSettings.json`
- `src/Services/Payment.Api/appsettings.json`

#### `src/BuildingBlocks/Chaos/ChaosMiddleware.cs`

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Chaos;

/// <summary>
/// Cấu hình trong appsettings.Development.json của từng service, ví dụ:
///   "Chaos": { "DelayMs": 3000, "FailRate": 0.2, "PathPrefix": "/internal" }
/// Dùng ở S2 để tái hiện kịch bản gãy, ở S8 cho chaos test.
/// </summary>
public sealed class ChaosOptions
{
    public int DelayMs { get; set; }
    public double FailRate { get; set; }
    public string PathPrefix { get; set; } = "/";
}

public sealed class ChaosMiddleware(RequestDelegate next, IOptionsMonitor<ChaosOptions> options, ILogger<ChaosMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var o = options.CurrentValue;
        if (!ctx.Request.Path.StartsWithSegments(o.PathPrefix) && o.PathPrefix != "/")
        {
            await next(ctx);
            return;
        }

        if (o.DelayMs > 0)
        {
            logger.LogWarning("Chaos: trễ {Delay} ms cho {Path}", o.DelayMs, ctx.Request.Path);
            await Task.Delay(o.DelayMs, ctx.RequestAborted);
        }

        if (o.FailRate > 0 && Random.Shared.NextDouble() < o.FailRate)
        {
            logger.LogWarning("Chaos: trả 503 cho {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsync("chaos");
            return;
        }

        await next(ctx);
    }
}

public static class ChaosExtensions
{
    public static IHostApplicationBuilder AddChaos(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<ChaosOptions>(builder.Configuration.GetSection("Chaos"));
        return builder;
    }

    /// <summary>Chỉ bật ở Development; ở môi trường khác là no-op.</summary>
    public static IApplicationBuilder UseChaos(this IApplicationBuilder app, IHostEnvironment env) =>
        env.IsDevelopment() ? app.UseMiddleware<ChaosMiddleware>() : app;
}
```

#### `src/Services/Payment.Api/ApiMarker.cs`

```csharp
namespace Payment.Api;

/// <summary>Kiểu đánh dấu assembly: dùng cho WebApplicationFactory&lt;ApiMarker&gt; trong test và quét endpoint.</summary>
public sealed class ApiMarker;
```

#### `src/Services/Payment.Api/Data/PaymentDbContext.cs`

```csharp
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Payment.Api.Data;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Payment> Payments => Set<Domain.Payment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("payment");
        b.Entity<Domain.Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OrderId).IsUnique();
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProviderRef).HasMaxLength(100);
            e.Property(x => x.FailureReason).HasMaxLength(300);
        });
        b.ApplySoftDeleteFilters();
    }
}

public sealed class PaymentDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PaymentDbContext>()
        .UseNpgsql("Host=localhost;Database=paymentdb;Username=postgres;Password=postgres")
        .UseSnakeCaseNamingConvention().Options);
}
```

#### `src/Services/Payment.Api/Domain/Payment.cs`

```csharp
using BuildingBlocks.Persistence;

namespace Payment.Api.Domain;

public enum PaymentMethod { Fake = 0, Cash = 1, Card = 2, VietQr = 3 }

public enum PaymentStatus { Pending = 0, Succeeded = 1, Failed = 2, Refunded = 3 }

/// <summary>Một order chỉ có một payment (OrderId unique) → không bao giờ trừ tiền hai lần.</summary>
public sealed class Payment : Auditable
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid OrderId { get; private set; }
    public long Amount { get; private set; }
    public PaymentMethod Method { get; private set; }
    public PaymentStatus Status { get; private set; } = PaymentStatus.Pending;
    public string? ProviderRef { get; private set; }
    public string? FailureReason { get; private set; }

    private Payment() { }

    public static Payment Create(Guid orderId, long amount, PaymentMethod method) =>
        new() { OrderId = orderId, Amount = amount, Method = method };

    public void Succeed(string providerRef)
    {
        Status = PaymentStatus.Succeeded;
        ProviderRef = providerRef;
    }

    public void Fail(string reason)
    {
        Status = PaymentStatus.Failed;
        FailureReason = reason;
    }

    public bool Refund()
    {
        if (Status != PaymentStatus.Succeeded) return false;
        Status = PaymentStatus.Refunded;
        return true;
    }
}
```

#### `src/Services/Payment.Api/FakeGateway/FakePaymentProvider.cs`

```csharp
using Microsoft.Extensions.Options;

namespace Payment.Api.FakeGateway;

/// <summary>
/// Cấu hình ở appsettings: "FakePayment": { "FailureRate": 0.1, "MinLatencyMs": 200, "MaxLatencyMs": 2000 }.
/// Đổi lúc đang chạy được (IOptionsMonitor), tiện cho diễn tập lỗi thanh toán.
/// </summary>
public sealed class FakePaymentOptions
{
    public double FailureRate { get; set; } = 0.0;
    public int MinLatencyMs { get; set; } = 200;
    public int MaxLatencyMs { get; set; } = 2000;
}

public sealed record ChargeResult(bool Success, string? ProviderRef, string? Error);

public sealed class FakePaymentProvider(IOptionsMonitor<FakePaymentOptions> options, ILogger<FakePaymentProvider> logger)
{
    public async Task<ChargeResult> ChargeAsync(Guid orderId, long amount, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var latency = Random.Shared.Next(o.MinLatencyMs, Math.Max(o.MinLatencyMs + 1, o.MaxLatencyMs));
        await Task.Delay(latency, ct);

        if (Random.Shared.NextDouble() < o.FailureRate)
        {
            logger.LogInformation("FakePayment từ chối order {OrderId} sau {Latency} ms", orderId, latency);
            return new ChargeResult(false, null, "CARD_DECLINED");
        }

        return new ChargeResult(true, $"FAKE-{Guid.NewGuid():N}"[..20], null);
    }
}
```

#### `src/Services/Payment.Api/Features/Internal/Payments.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Microsoft.EntityFrameworkCore;
using Payment.Api.Data;
using Payment.Api.Domain;
using Payment.Api.FakeGateway;

namespace Payment.Api.Features.Internal;

public sealed record ChargeRequest(Guid OrderId, [property: Range(1, long.MaxValue)] long Amount, PaymentMethod Method = PaymentMethod.Fake);
public sealed record PaymentDto(Guid Id, Guid OrderId, long Amount, string Method, string Status, string? ProviderRef, string? FailureReason);

/// <summary>
/// S2: Ordering gọi đồng bộ. Idempotent theo OrderId: gọi lại trả về kết quả cũ, không trừ tiền lần hai.
/// Payment Failed trả 402 để Ordering biết mà huỷ đơn.
/// </summary>
public sealed class Payments : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/payments").AllowAnonymous().WithTags("Internal");

        group.MapPost("/", async (ChargeRequest req, PaymentDbContext db, FakePaymentProvider provider, CancellationToken ct) =>
        {
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == req.OrderId, ct);
            if (payment is null)
            {
                payment = Domain.Payment.Create(req.OrderId, req.Amount, req.Method);
                db.Payments.Add(payment);
                await db.SaveChangesAsync(ct); // ghi Pending trước để request trùng thấy được

                // Cố ý không dùng ct của request: client có timeout thì payment vẫn chạy tới cùng (giống cổng thật)
                var result = await provider.ChargeAsync(req.OrderId, req.Amount, CancellationToken.None);
                if (result.Success) payment.Succeed(result.ProviderRef!);
                else payment.Fail(result.Error!);
                await db.SaveChangesAsync(CancellationToken.None);
            }

            return payment.Status switch
            {
                PaymentStatus.Succeeded => Results.Ok(payment.ToDto()),
                PaymentStatus.Pending => throw new ConflictException("PAYMENT_PENDING", "Thanh toán đang xử lý."),
                _ => throw new AppException("PAYMENT_FAILED", $"Thanh toán thất bại: {payment.FailureReason}", 402)
            };
        });

        group.MapGet("/{orderId:guid}", async (Guid orderId, PaymentDbContext db, CancellationToken ct) =>
        {
            var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
            return p is null ? Results.NotFound() : Results.Ok(p.ToDto());
        });

        group.MapPost("/{orderId:guid}/refund", async (Guid orderId, PaymentDbContext db, CancellationToken ct) =>
        {
            var p = await db.Payments.FirstOrDefaultAsync(x => x.OrderId == orderId, ct) ?? throw NotFoundException.For("payment", orderId);
            p.Refund();
            await db.SaveChangesAsync(ct);
            return Results.Ok(p.ToDto());
        });
    }
}

public static class PaymentMapping
{
    public static PaymentDto ToDto(this Domain.Payment p) =>
        new(p.Id, p.OrderId, p.Amount, p.Method.ToString(), p.Status.ToString(), p.ProviderRef, p.FailureReason);
}
```

#### `src/Services/Payment.Api/Payment.Api.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../../contracts/openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOptions>--file-name Payment.Api</OpenApiGenerateDocumentsOptions>
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
    <None Include="..\..\..\seed\**\*.json" Link="seed\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

#### `src/Services/Payment.Api/Program.cs`

```csharp
using BuildingBlocks;
using BuildingBlocks.Chaos;
using BuildingBlocks.Persistence;
using Payment.Api.Data;
using Payment.Api.FakeGateway;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddChaos();
builder.AddShopLabDbContext<PaymentDbContext>("paymentdb");
builder.Services.Configure<FakePaymentOptions>(builder.Configuration.GetSection("FakePayment"));
builder.Services.AddSingleton<FakePaymentProvider>();

var app = builder.Build();

await app.InitializeDatabaseAsync<PaymentDbContext>();
app.UseChaos(app.Environment);
app.UseShopLabApi(typeof(Payment.Api.ApiMarker).Assembly);

app.Run();
```

#### `src/Services/Payment.Api/Properties/launchSettings.json`

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5107",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

#### `src/Services/Payment.Api/appsettings.json`

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "FakePayment": { "FailureRate": 0.0, "MinLatencyMs": 200, "MaxLatencyMs": 2000 },
  "Chaos": { "DelayMs": 0, "FailRate": 0, "PathPrefix": "/internal" }
}
```

### Buổi 8 — Ordering checkout đồng bộ + AppHost/Gateway đầy đủ

**Việc cần làm**

1. `Order`/`OrderLine`, số đơn `SL-yyMMdd-000123` từ sequence Postgres.
2. Typed HttpClient cho Cart/Customer/Catalog/Inventory/Payment (BaseAddress `https+http://<tên>`).
3. `PlaceOrder`: đọc giỏ → kiểm khách → giá mới → giữ hàng → thanh toán → chốt → xoá giỏ; lỗi giữa chừng thì cố release.
4. AppHost bản đầy đủ của S2 (Redis, 5 database, profile LoadTest), gateway route cho các service mới.

**Lệnh**

```bash
python3 tools/new-service.py Ordering 5105 && python3 tools/sln-sync.py
aspire run --project src/AppHost
```

**File**

- `src/Services/Ordering.Api/ApiMarker.cs`
- `src/Services/Ordering.Api/Clients/ServiceClients.cs`
- `src/Services/Ordering.Api/Data/OrderNumberGenerator.cs`
- `src/Services/Ordering.Api/Data/OrderingDbContext.cs`
- `src/Services/Ordering.Api/Domain/Order.cs`
- `src/Services/Ordering.Api/Features/Admin/AdminOrders.cs`
- `src/Services/Ordering.Api/Features/Checkout/PlaceOrder.cs`
- `src/Services/Ordering.Api/Features/OrderDtos.cs`
- `src/Services/Ordering.Api/Features/Orders/MyOrders.cs`
- `src/Services/Ordering.Api/Ordering.Api.csproj`
- `src/Services/Ordering.Api/Program.cs`
- `src/Services/Ordering.Api/Properties/launchSettings.json`
- `src/Services/Ordering.Api/appsettings.json`
- `src/AppHost/AppHost.cs` *(sửa)*
- `src/AppHost/AppHost.csproj` *(sửa)*
- `src/AppHost/Properties/launchSettings.json`
- `src/AppHost/ShopLabResourceExtensions.cs` *(sửa)*
- `src/Gateways/Gateway.Web/Program.cs` *(sửa)*
- `src/Gateways/Gateway.Web/yarp.json` *(sửa)*
- `src/BuildingBlocks/Results/ProblemDetailsMapping.cs` *(sửa)*
- `shoplab.slnx` *(sửa)*
- `http/checkout.http`

#### `src/Services/Ordering.Api/ApiMarker.cs`

```csharp
namespace Ordering.Api;

/// <summary>Kiểu đánh dấu assembly: dùng cho WebApplicationFactory&lt;ApiMarker&gt; trong test và quét endpoint.</summary>
public sealed class ApiMarker;
```

#### `src/Services/Ordering.Api/Clients/ServiceClients.cs`

```csharp
using System.Net.Http.Json;
using BuildingBlocks.Http;

namespace Ordering.Api.Clients;

// ---- DTO nhận về từ service khác (chỉ những trường Ordering cần) ----
public sealed record CartLineInfo(string Sku, string Name, string ProductSlug, int Qty, long UnitPrice, long LineTotal);
public sealed record CartInfo(string? CouponCode, List<CartLineInfo> Lines, int ItemCount, long Subtotal);
public sealed record CustomerStatusInfo(Guid Id, string Status);
public sealed record AddressInfo(Guid Id, string Line1, string Ward, string District, string City, bool IsDefault)
{
    public override string ToString() => $"{Line1}, {Ward}, {District}, {City}";
}
public sealed record VariantInfo(string Sku, string Name, string ProductName, string ProductSlug, Guid CategoryId,
    string CategorySlug, long Price, long? CompareAtPrice, bool IsSellable);
public sealed record ReservationInfo(Guid Id, Guid OrderId, string Sku, Guid LocationId, int Qty, string Status, DateTimeOffset ExpiresAt);
public sealed record ReserveLine(string Sku, int Qty);
public sealed record PaymentInfo(Guid Id, Guid OrderId, long Amount, string Method, string Status, string? ProviderRef, string? FailureReason);

// ---- Typed HttpClient. BaseAddress đặt trong Program.cs bằng tên service của AppHost ----

public sealed class CartClient(HttpClient http)
{
    public Task<CartInfo> GetAsync(Guid customerId, CancellationToken ct) =>
        http.GetAsync($"/internal/carts/{customerId}", ct).ReadAsync<CartInfo>("cart", ct);

    public Task ClearAsync(Guid customerId, CancellationToken ct) =>
        http.DeleteAsync($"/internal/carts/{customerId}", ct).EnsureAsync("cart", ct);
}

public sealed class CustomerClient(HttpClient http)
{
    public Task<CustomerStatusInfo> GetStatusAsync(Guid customerId, CancellationToken ct) =>
        http.GetAsync($"/internal/customers/{customerId}/status", ct).ReadAsync<CustomerStatusInfo>("customer", ct);

    public Task<AddressInfo?> GetAddressAsync(Guid customerId, Guid? addressId, CancellationToken ct) =>
        http.GetAsync(addressId is null
                ? $"/internal/customers/{customerId}/addresses/default"
                : $"/internal/customers/{customerId}/addresses/{addressId}", ct)
            .ReadOrDefaultAsync<AddressInfo>("customer", ct);
}

public sealed class CatalogClient(HttpClient http)
{
    public Task<List<VariantInfo>> GetVariantsAsync(IEnumerable<string> skus, CancellationToken ct) =>
        http.PostAsJsonAsync("/internal/variants/batch", skus.ToArray(), ct).ReadAsync<List<VariantInfo>>("catalog", ct);
}

public sealed class InventoryClient(HttpClient http)
{
    public Task<List<ReservationInfo>> ReserveAsync(Guid orderId, IEnumerable<ReserveLine> lines, CancellationToken ct) =>
        http.PostAsJsonAsync("/internal/reservations", new { orderId, lines = lines.ToList(), ttlMinutes = 15 }, ct)
            .ReadAsync<List<ReservationInfo>>("inventory", ct);

    public Task CommitAsync(Guid orderId, CancellationToken ct) =>
        http.PostAsync($"/internal/reservations/{orderId}/commit", null, ct).EnsureAsync("inventory", ct);

    public Task ReleaseAsync(Guid orderId, CancellationToken ct) =>
        http.PostAsync($"/internal/reservations/{orderId}/release", null, ct).EnsureAsync("inventory", ct);
}

public sealed class PaymentClient(HttpClient http)
{
    public Task<PaymentInfo> ChargeAsync(Guid orderId, long amount, CancellationToken ct) =>
        http.PostAsJsonAsync("/internal/payments", new { orderId, amount, method = "Fake" }, ct).ReadAsync<PaymentInfo>("payment", ct);
}
```

#### `src/Services/Ordering.Api/Data/OrderNumberGenerator.cs`

```csharp
using Microsoft.EntityFrameworkCore;

namespace Ordering.Api.Data;

/// <summary>SO-20261027-000123: ngày (giờ VN) + số tăng dần từ sequence Postgres (không trùng kể cả nhiều instance).</summary>
public sealed class OrderNumberGenerator(OrderingDbContext db, TimeProvider clock)
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public async Task<string> NextAsync(string prefix = "SO", CancellationToken ct = default)
    {
        var seq = await db.Database
            .SqlQueryRaw<long>("SELECT nextval('ordering.order_number_seq') AS \"Value\"")
            .SingleAsync(ct);
        var day = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Vietnam);
        return $"{prefix}-{day:yyyyMMdd}-{seq % 1_000_000:D6}";
    }
}
```

#### `src/Services/Ordering.Api/Data/OrderingDbContext.cs`

```csharp
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Ordering.Api.Domain;

namespace Ordering.Api.Data;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("ordering");
        b.HasSequence<long>("order_number_seq");

        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Number).HasMaxLength(30).IsRequired();
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CouponCode).HasMaxLength(50);
            e.Property(x => x.ShippingAddress).HasMaxLength(500);
            e.Property(x => x.CancelReason).HasMaxLength(300);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            e.HasIndex(x => new { x.CustomerId, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);
            e.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines");
            e.HasKey(x => new { x.OrderId, x.Sku });
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(300);
        });

        b.ApplySoftDeleteFilters();
    }
}

public sealed class OrderingDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<OrderingDbContext>
{
    public OrderingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<OrderingDbContext>()
        .UseNpgsql("Host=localhost;Database=orderingdb;Username=postgres;Password=postgres")
        .UseSnakeCaseNamingConvention().Options);
}
```

#### `src/Services/Ordering.Api/Domain/Order.cs`

```csharp
using BuildingBlocks.Persistence;
using BuildingBlocks.Results;

namespace Ordering.Api.Domain;

public enum SalesChannel { Online = 0, Pos = 1, FlashSale = 2 }

/// <summary>
/// PendingStock → PendingPayment → Paid → Completed, hoặc → Cancelled (hết hàng / hết hạn thanh toán / thanh toán lỗi).
/// </summary>
public enum OrderStatus { PendingStock = 0, PendingPayment = 1, Paid = 2, Completed = 3, Cancelled = 4 }

public sealed class Order : Auditable
{
    private readonly List<OrderLine> _lines = [];

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Number { get; private set; } = default!;
    public Guid? CustomerId { get; private set; }
    public SalesChannel Channel { get; private set; }
    public Guid? StoreId { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.PendingStock;
    public long Subtotal { get; private set; }
    public long DiscountTotal { get; private set; }
    public long Total { get; private set; }
    public string? CouponCode { get; private set; }
    public string? ShippingAddress { get; private set; }
    public string? CancelReason { get; private set; }
    /// <summary>Từ S3: header Idempotency-Key; flash sale dùng reservationId; POS dùng ClientTxnId.</summary>
    public string? IdempotencyKey { get; private set; }
    /// <summary>Tăng mỗi lần đổi trạng thái; projection sang Mongo (S5) dùng để bỏ qua event cũ.</summary>
    public long Version { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    private Order() { }

    public static Order Create(string number, Guid? customerId, SalesChannel channel, IEnumerable<OrderLine> lines,
        string? shippingAddress = null, string? idempotencyKey = null, Guid? storeId = null, string? couponCode = null,
        long discountTotal = 0)
    {
        var order = new Order
        {
            Number = number,
            CustomerId = customerId,
            Channel = channel,
            StoreId = storeId,
            ShippingAddress = shippingAddress,
            IdempotencyKey = idempotencyKey,
            CouponCode = couponCode
        };
        order._lines.AddRange(lines.Select(l => l.WithOrder(order.Id)));
        if (order._lines.Count == 0) throw new BadRequestException("CART_EMPTY", "Đơn hàng không có sản phẩm.");
        order.Subtotal = order._lines.Sum(l => l.UnitPrice * l.Qty);
        order.DiscountTotal = discountTotal + order._lines.Sum(l => l.LineDiscount);
        order.Total = Math.Max(0, order.Subtotal - order.DiscountTotal);
        order.Version = 1;
        return order;
    }

    public void MarkPendingPayment() => Transition(OrderStatus.PendingPayment, OrderStatus.PendingStock);

    public void MarkPaid() => Transition(OrderStatus.Paid, OrderStatus.PendingPayment);

    public void MarkCompleted() => Transition(OrderStatus.Completed, OrderStatus.Paid, OrderStatus.PendingStock);

    /// <summary>Huỷ được khi chưa thanh toán. Trả false nếu đã ở trạng thái cuối (để handler bỏ qua an toàn).</summary>
    public bool Cancel(string reason)
    {
        if (Status is OrderStatus.Cancelled or OrderStatus.Paid or OrderStatus.Completed) return false;
        Status = OrderStatus.Cancelled;
        CancelReason = reason;
        Version++;
        return true;
    }

    private void Transition(OrderStatus to, params OrderStatus[] allowedFrom)
    {
        if (Status == to) return; // idempotent
        if (!allowedFrom.Contains(Status))
            throw new ConflictException("INVALID_ORDER_STATE", $"Không thể chuyển đơn {Number} từ {Status} sang {to}.");
        Status = to;
        Version++;
    }
}

/// <summary>Dòng hàng của order; ghi một lần khi tạo order rồi không đổi (append-only).</summary>
public sealed class OrderLine : IHasCreatedAt
{
    public Guid OrderId { get; private set; }
    public string Sku { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public int Qty { get; private set; }
    public long UnitPrice { get; private set; }
    public long LineDiscount { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }

    private OrderLine() { }

    public static OrderLine Create(string sku, string name, int qty, long unitPrice, long lineDiscount = 0)
    {
        if (qty <= 0) throw new BadRequestException("INVALID_QTY", "Số lượng phải lớn hơn 0.");
        return new OrderLine { Sku = sku, Name = name, Qty = qty, UnitPrice = unitPrice, LineDiscount = lineDiscount };
    }

    internal OrderLine WithOrder(Guid orderId)
    {
        OrderId = orderId;
        return this;
    }
}
```

#### `src/Services/Ordering.Api/Features/Admin/AdminOrders.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Paging;
using Microsoft.EntityFrameworkCore;
using Ordering.Api.Data;
using Ordering.Api.Domain;

namespace Ordering.Api.Features.Admin;

public sealed class AdminOrders : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/admin/orders", async (string? status, string? channel, string? cursor, int? size, OrderingDbContext db, CancellationToken ct) =>
            {
                var take = PagingDefaults.Clamp(size);
                var query = db.Orders.AsNoTracking().Include(o => o.Lines).AsQueryable();
                if (Enum.TryParse<OrderStatus>(status, true, out var s)) query = query.Where(o => o.Status == s);
                if (Enum.TryParse<SalesChannel>(channel, true, out var c)) query = query.Where(o => o.Channel == c);
                var after = KeysetCursor.Decode(cursor);
                if (after is not null)
                {
                    var at = DateTimeOffset.Parse(after.Key);
                    query = query.Where(o => o.CreatedAt < at || (o.CreatedAt == at && o.Id.CompareTo(after.Id) < 0));
                }
                var rows = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(take + 1).ToListAsync(ct);
                string? next = null;
                if (rows.Count > take)
                {
                    rows.RemoveAt(take);
                    next = new KeysetCursor(rows[^1].CreatedAt.ToString("O"), rows[^1].Id).Encode();
                }
                return new PagedResult<OrderSummaryDto>(rows.Select(o => o.ToSummary()).ToList(), next);
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Orders");
}
```

#### `src/Services/Ordering.Api/Features/Checkout/PlaceOrder.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Ordering.Api.Clients;
using Ordering.Api.Data;
using Ordering.Api.Domain;

namespace Ordering.Api.Features.Checkout;

public sealed record PlaceOrderRequest(Guid? AddressId);

/// <summary>
/// S2 — CHECKOUT ĐỒNG BỘ, CỐ Ý VIẾT ĐƠN GIẢN ĐỂ THẤY NÓ GÃY Ở ĐÂU.
/// Một request gọi lần lượt: Cart → Customer → Catalog → Inventory (giữ hàng) → Payment → Inventory (commit) → Cart (xoá).
/// Chưa có Idempotency-Key, bù trừ chỉ "làm hết sức" trong catch. Ba chỗ gãy đã biết (xem docs/notes/s2-failures.md):
///   1. Inventory chết giữa chừng → order kẹt PendingStock, có thể còn Reserved không ai trả.
///   2. Payment chậm hơn timeout → order bị huỷ trong khi tiền vẫn bị trừ.
///   3. Bấm "Đặt hàng" hai lần → hai order, giữ hàng hai lần.
/// S3 viết lại thành saga.
/// </summary>
public sealed class PlaceOrder : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/checkout", Handle)
            .RequireAuthorization(Policies.Authenticated)
            .WithTags("Checkout");

    private static async Task<IResult> Handle(
        PlaceOrderRequest req, ICurrentUser user, OrderingDbContext db, OrderNumberGenerator numbers,
        CartClient cart, CustomerClient customers, CatalogClient catalog, InventoryClient inventory, PaymentClient payments,
        ILogger<PlaceOrder> logger, CancellationToken ct)
    {
        var customerId = user.Id;

        // 1. Giỏ hàng
        var cartInfo = await cart.GetAsync(customerId, ct);
        if (cartInfo.Lines.Count == 0) throw new BadRequestException("CART_EMPTY", "Giỏ hàng trống.");

        // 2. Khách có bị khoá không
        var status = await customers.GetStatusAsync(customerId, ct);
        if (status.Status == "Banned") throw new ForbiddenException("CUSTOMER_BANNED", "Tài khoản đang bị khoá.");
        var address = await customers.GetAddressAsync(customerId, req.AddressId, ct);

        // 3. Giá mới nhất từ Catalog (không tin giá snapshot trong giỏ)
        var variants = (await catalog.GetVariantsAsync(cartInfo.Lines.Select(l => l.Sku), ct)).ToDictionary(v => v.Sku);
        var lines = cartInfo.Lines.Select(l =>
        {
            if (!variants.TryGetValue(l.Sku, out var v) || !v.IsSellable)
                throw new BadRequestException("SKU_NOT_SELLABLE", $"{l.Sku} không còn bán, hãy xoá khỏi giỏ.");
            return OrderLine.Create(v.Sku, $"{v.ProductName} - {v.Name}", l.Qty, v.Price);
        }).ToList();

        // 4. Tạo order PendingStock
        var order = Order.Create(await numbers.NextAsync(ct: ct), customerId, SalesChannel.Online, lines, address?.ToString());
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        // 5. Giữ hàng
        try
        {
            await inventory.ReserveAsync(order.Id, lines.Select(l => new ReserveLine(l.Sku, l.Qty)), ct);
        }
        catch (AppException ex) when (ex.Code == "OUT_OF_STOCK")
        {
            order.Cancel(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        order.MarkPendingPayment();
        await db.SaveChangesAsync(ct);

        // 6. Thanh toán
        try
        {
            await payments.ChargeAsync(order.Id, order.Total, ct);
        }
        catch (AppException ex)
        {
            // Bù trừ "làm hết sức": nếu Inventory cũng đang lỗi thì hàng giữ bị treo.
            logger.LogWarning("Thanh toán order {Number} lỗi {Code}, huỷ đơn", order.Number, ex.Code);
            try { await inventory.ReleaseAsync(order.Id, CancellationToken.None); }
            catch (Exception releaseError) { logger.LogError(releaseError, "Không trả được hàng giữ cho {Number}", order.Number); }
            order.Cancel($"Thanh toán lỗi: {ex.Code}");
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        // 7. Commit kho, đơn Paid, xoá giỏ
        await inventory.CommitAsync(order.Id, ct);
        order.MarkPaid();
        await db.SaveChangesAsync(ct);
        await cart.ClearAsync(customerId, ct);

        return Results.Created($"/orders/{order.Id}", order.ToDto());
    }
}
```

#### `src/Services/Ordering.Api/Features/OrderDtos.cs`

```csharp
using Ordering.Api.Domain;

namespace Ordering.Api.Features;

public sealed record OrderLineDto(string Sku, string Name, int Qty, long UnitPrice, long LineDiscount, long LineTotal);

public sealed record OrderDto(
    Guid Id, string Number, Guid? CustomerId, string Channel, Guid? StoreId, string Status,
    long Subtotal, long DiscountTotal, long Total, string? CouponCode, string? ShippingAddress, string? CancelReason,
    IReadOnlyList<OrderLineDto> Lines, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record OrderSummaryDto(Guid Id, string Number, Guid? CustomerId, string Channel, string Status, long Total, int ItemCount, DateTimeOffset CreatedAt);

public static class OrderMapping
{
    public static OrderDto ToDto(this Order o) => new(
        o.Id, o.Number, o.CustomerId, o.Channel.ToString(), o.StoreId, o.Status.ToString(),
        o.Subtotal, o.DiscountTotal, o.Total, o.CouponCode, o.ShippingAddress, o.CancelReason,
        o.Lines.Select(l => new OrderLineDto(l.Sku, l.Name, l.Qty, l.UnitPrice, l.LineDiscount, l.UnitPrice * l.Qty - l.LineDiscount)).ToList(),
        o.CreatedAt, o.UpdatedAt);

    public static OrderSummaryDto ToSummary(this Order o) =>
        new(o.Id, o.Number, o.CustomerId, o.Channel.ToString(), o.Status.ToString(), o.Total, o.Lines.Sum(l => l.Qty), o.CreatedAt);
}
```

#### `src/Services/Ordering.Api/Features/Orders/MyOrders.cs`

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Paging;
using BuildingBlocks.Results;
using Microsoft.EntityFrameworkCore;
using Ordering.Api.Data;

namespace Ordering.Api.Features.Orders;

/// <summary>GET /orders (đơn của tôi, mới nhất trước, keyset theo CreatedAt) và GET /orders/{id}.</summary>
public sealed class MyOrders : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/orders", async (string? cursor, int? size, ICurrentUser user, OrderingDbContext db, CancellationToken ct) =>
            {
                var take = PagingDefaults.Clamp(size);
                var query = db.Orders.AsNoTracking().Include(o => o.Lines).Where(o => o.CustomerId == user.Id);
                var after = KeysetCursor.Decode(cursor);
                if (after is not null)
                {
                    var at = DateTimeOffset.Parse(after.Key);
                    query = query.Where(o => o.CreatedAt < at || (o.CreatedAt == at && o.Id.CompareTo(after.Id) < 0));
                }
                var rows = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(take + 1).ToListAsync(ct);
                string? next = null;
                if (rows.Count > take)
                {
                    rows.RemoveAt(take);
                    next = new KeysetCursor(rows[^1].CreatedAt.ToString("O"), rows[^1].Id).Encode();
                }
                return new PagedResult<OrderSummaryDto>(rows.Select(o => o.ToSummary()).ToList(), next);
            })
            .RequireAuthorization(Policies.Authenticated)
            .WithTags("Orders");

        app.MapGet("/orders/{id:guid}", async (Guid id, ICurrentUser user, OrderingDbContext db, CancellationToken ct) =>
            {
                var order = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct)
                            ?? throw NotFoundException.For("order", id);
                if (order.CustomerId != user.Id && !user.IsInRole(Roles.Admin))
                    throw NotFoundException.For("order", id); // không tiết lộ đơn của người khác
                return Results.Ok(order.ToDto());
            })
            .RequireAuthorization(Policies.Authenticated)
            .WithTags("Orders");
    }
}
```

#### `src/Services/Ordering.Api/Ordering.Api.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../../contracts/openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOptions>--file-name Ordering.Api</OpenApiGenerateDocumentsOptions>
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
    <None Include="..\..\..\seed\**\*.json" Link="seed\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

#### `src/Services/Ordering.Api/Program.cs`

```csharp
using BuildingBlocks;
using BuildingBlocks.Persistence;
using Ordering.Api.Clients;
using Ordering.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddShopLabDbContext<OrderingDbContext>("orderingdb");
builder.Services.AddScoped<OrderNumberGenerator>();

// S2: gọi HTTP đồng bộ tới 5 service. Resilience handler mặc định (ServiceDefaults) có retry + timeout:
// timeout mỗi lần thử 10 giây, tổng 30 giây — chính là thứ gây ra kịch bản gãy số 2.
builder.Services.AddHttpClient<CartClient>(c => c.BaseAddress = new Uri("https+http://cart"));
builder.Services.AddHttpClient<CustomerClient>(c => c.BaseAddress = new Uri("https+http://customer"));
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("https+http://catalog"));
builder.Services.AddHttpClient<InventoryClient>(c => c.BaseAddress = new Uri("https+http://inventory"));
builder.Services.AddHttpClient<PaymentClient>(c => c.BaseAddress = new Uri("https+http://payment"));

var app = builder.Build();

await app.InitializeDatabaseAsync<OrderingDbContext>();
app.UseShopLabApi(typeof(Ordering.Api.ApiMarker).Assembly);

app.Run();
```

#### `src/Services/Ordering.Api/Properties/launchSettings.json`

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5106",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

#### `src/Services/Ordering.Api/appsettings.json`

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*"
}
```

#### `src/AppHost/AppHost.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using AppHost;

var builder = DistributedApplication.CreateBuilder(args);
var env = builder.Configuration["ShopLab:Environment"] ?? "Development";

// ---------- Hạ tầng ----------
var pg = builder.AddPostgres("pg")
    .WithDataVolume("shoplab-pg")
    .WithPgAdmin();

var redis = builder.AddRedis("redis")          // S2: giỏ hàng
    .WithDataVolume("shoplab-redis")
    .WithRedisInsight();

var keycloakAdmin = builder.AddParameter("keycloak-admin", "admin");
var keycloakPassword = builder.AddParameter("keycloak-password", "admin", secret: true);
var keycloak = builder.AddKeycloak("keycloak", 8080, keycloakAdmin, keycloakPassword)
    .WithDataVolume("shoplab-keycloak")
    .WithRealmImport("../../docker/keycloak");

// ---------- Database: mỗi service một database trên cùng container Postgres ----------
var catalogDb = pg.AddDatabase("catalogdb");
var customerDb = pg.AddDatabase("customerdb");
var inventoryDb = pg.AddDatabase("inventorydb");
var orderingDb = pg.AddDatabase("orderingdb");
var paymentDb = pg.AddDatabase("paymentdb");

// ---------- Service ----------
var catalog = builder.AddProject<Projects.Catalog_Api>("catalog")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(catalogDb);

var customer = builder.AddProject<Projects.Customer_Api>("customer")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(customerDb);

var inventory = builder.AddProject<Projects.Inventory_Api>("inventory")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(inventoryDb);

var cart = builder.AddProject<Projects.Cart_Api>("cart")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak)
    .WithReference(redis).WaitFor(redis)
    .WithReference(catalog);

var payment = builder.AddProject<Projects.Payment_Api>("payment")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(paymentDb);

var ordering = builder.AddProject<Projects.Ordering_Api>("ordering")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(orderingDb)
    .WithReference(cart).WithReference(customer).WithReference(catalog)
    .WithReference(inventory).WithReference(payment);

var gateway = builder.AddProject<Projects.Gateway_Web>("gateway")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak)
    .WithReference(catalog).WithReference(customer).WithReference(cart)
    .WithReference(inventory).WithReference(ordering)
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

#### `src/AppHost/AppHost.csproj` *(sửa — nội dung đầy đủ sau khi sửa)*

```xml
<Project Sdk="Aspire.AppHost.Sdk/13.5.0">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UserSecretsId>shoplab-apphost</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.PostgreSQL" />
    <PackageReference Include="Aspire.Hosting.Redis" />
    <PackageReference Include="Aspire.Hosting.Keycloak" />
    <PackageReference Include="Aspire.Hosting.JavaScript" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Gateways\Gateway.Web\Gateway.Web.csproj" />
    <ProjectReference Include="..\Services\Catalog.Api\Catalog.Api.csproj" />
    <ProjectReference Include="..\Services\Customer.Api\Customer.Api.csproj" />
    <ProjectReference Include="..\Services\Cart.Api\Cart.Api.csproj" />
    <ProjectReference Include="..\Services\Inventory.Api\Inventory.Api.csproj" />
    <ProjectReference Include="..\Services\Ordering.Api\Ordering.Api.csproj" />
    <ProjectReference Include="..\Services\Payment.Api\Payment.Api.csproj" />
  </ItemGroup>
</Project>
```

#### `src/AppHost/Properties/launchSettings.json`

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "http://localhost:15000",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true",
        "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:19000",
        "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "http://localhost:20000",
        "ShopLab__Environment": "Development"
      }
    },
    "LoadTest": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "http://localhost:15000",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true",
        "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:19000",
        "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "http://localhost:20000",
        "ShopLab__Environment": "LoadTest"
      }
    }
  }
}
```

#### `src/AppHost/ShopLabResourceExtensions.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

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

    /// <summary>
    /// Môi trường chạy của service: Development (mặc định) hoặc LoadTest
    /// (aspire run --launch-profile LoadTest) để bật header X-Test-User cho k6.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithShopLabEnvironment(
        this IResourceBuilder<ProjectResource> project, string environment)
    {
        return project.WithEnvironment("ASPNETCORE_ENVIRONMENT", environment);
    }

    public static IResourceBuilder<ProjectResource> WithDatabase(
        this IResourceBuilder<ProjectResource> project, IResourceBuilder<PostgresDatabaseResource> db)
    {
        return project.WithReference(db).WaitFor(db);
    }
}
```

#### `src/Gateways/Gateway.Web/Program.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

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

// Route nội bộ (/internal/...) chỉ dành cho service gọi nhau, không mở ra ngoài qua gateway.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Value?.Contains("/internal", StringComparison.OrdinalIgnoreCase) == true)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// Header Authorization được YARP chuyển tiếp nguyên vẹn; service phía sau tự xác thực lại.
app.MapReverseProxy();

app.Run();
```

#### `src/Gateways/Gateway.Web/yarp.json` *(sửa — nội dung đầy đủ sau khi sửa)*

```json
{
  "ReverseProxy": {
    "Routes": {
      "ping": {
        "ClusterId": "catalog",
        "AuthorizationPolicy": "authenticated",
        "Match": {
          "Path": "/api/ping"
        },
        "Transforms": [
          {
            "PathSet": "/ping"
          }
        ]
      },
      "catalog": {
        "ClusterId": "catalog",
        "AuthorizationPolicy": "Anonymous",
        "Match": {
          "Path": "/api/catalog/{**rest}"
        },
        "Transforms": [
          {
            "PathPattern": "/{**rest}"
          }
        ]
      },
      "catalog-admin": {
        "ClusterId": "catalog",
        "AuthorizationPolicy": "authenticated",
        "Match": {
          "Path": "/api/admin/catalog/{**rest}"
        },
        "Transforms": [
          {
            "PathPattern": "/admin/{**rest}"
          }
        ]
      },
      "customer": {
        "ClusterId": "customer",
        "AuthorizationPolicy": "authenticated",
        "Match": {
          "Path": "/api/customer/{**rest}"
        },
        "Transforms": [
          {
            "PathPattern": "/{**rest}"
          }
        ]
      },
      "customer-admin": {
        "ClusterId": "customer",
        "AuthorizationPolicy": "authenticated",
        "Match": {
          "Path": "/api/admin/customer/{**rest}"
        },
        "Transforms": [
          {
            "PathPattern": "/admin/{**rest}"
          }
        ]
      },
      "cart": {
        "ClusterId": "cart",
        "AuthorizationPolicy": "Anonymous",
        "Match": {
          "Path": "/api/cart/{**rest}"
        },
        "Transforms": [
          {
            "PathPattern": "/{**rest}"
          }
        ]
      },
      "inventory": {
        "ClusterId": "inventory",
        "AuthorizationPolicy": "Anonymous",
        "Match": {
          "Path": "/api/inventory/{**rest}"
        },
        "Transforms": [
          {
            "PathPattern": "/{**rest}"
          }
        ]
      },
      "inventory-admin": {
        "ClusterId": "inventory",
        "AuthorizationPolicy": "authenticated",
        "Match": {
          "Path": "/api/admin/inventory/{**rest}"
        },
        "Transforms": [
          {
            "PathPattern": "/admin/{**rest}"
          }
        ]
      },
      "ordering": {
        "ClusterId": "ordering",
        "AuthorizationPolicy": "authenticated",
        "Match": {
          "Path": "/api/ordering/{**rest}"
        },
        "Transforms": [
          {
            "PathPattern": "/{**rest}"
          }
        ]
      },
      "ordering-admin": {
        "ClusterId": "ordering",
        "AuthorizationPolicy": "authenticated",
        "Match": {
          "Path": "/api/admin/ordering/{**rest}"
        },
        "Transforms": [
          {
            "PathPattern": "/admin/{**rest}"
          }
        ]
      }
    },
    "Clusters": {
      "cart": {
        "Destinations": {
          "d1": {
            "Address": "https+http://cart"
          }
        }
      },
      "catalog": {
        "Destinations": {
          "d1": {
            "Address": "https+http://catalog"
          }
        }
      },
      "customer": {
        "Destinations": {
          "d1": {
            "Address": "https+http://customer"
          }
        }
      },
      "inventory": {
        "Destinations": {
          "d1": {
            "Address": "https+http://inventory"
          }
        }
      },
      "ordering": {
        "Destinations": {
          "d1": {
            "Address": "https+http://ordering"
          }
        }
      }
    }
  }
}
```

#### `src/BuildingBlocks/Results/ProblemDetailsMapping.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

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
            DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } pg } =>
                (409, "DUPLICATE", $"Dữ liệu bị trùng ({pg.ConstraintName})."),
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
    <Project Path="src/Services/Cart.Api/Cart.Api.csproj" />
    <Project Path="src/Services/Catalog.Api/Catalog.Api.csproj" />
    <Project Path="src/Services/Customer.Api/Customer.Api.csproj" />
    <Project Path="src/Services/Inventory.Api/Inventory.Api.csproj" />
    <Project Path="src/Services/Ordering.Api/Ordering.Api.csproj" />
    <Project Path="src/Services/Payment.Api/Payment.Api.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Cart.Tests/Cart.Tests.csproj" />
    <Project Path="tests/Catalog.Tests/Catalog.Tests.csproj" />
    <Project Path="tests/Customer.Tests/Customer.Tests.csproj" />
    <Project Path="tests/Inventory.Tests/Inventory.Tests.csproj" />
    <Project Path="tests/Ordering.Tests/Ordering.Tests.csproj" />
    <Project Path="tests/TestKit/TestKit.csproj" />
  </Folder>
</Solution>
```

#### `http/checkout.http`

```http
@gateway = http://localhost:5000
@token = paste-customer-token

### 1. Thêm vào giỏ
POST {{gateway}}/api/cart/items
Authorization: Bearer {{token}}
Content-Type: application/json

{ "sku": "SL-MOUSE-WL", "qty": 1 }

### 2. Đặt hàng (S2: 201 + order Paid; từ S3: 202 + order PendingStock, có Idempotency-Key)
POST {{gateway}}/api/ordering/checkout
Authorization: Bearer {{token}}
Idempotency-Key: 6b1c2f2a-1111-4c4c-9999-000000000001
Content-Type: application/json

{ }

### 3. Đơn của tôi
GET {{gateway}}/api/ordering/orders
Authorization: Bearer {{token}}

### 4. Chi tiết (dán id)
GET {{gateway}}/api/ordering/orders/paste-order-id
Authorization: Bearer {{token}}
```

**Kiểm tra cuối buổi**

```bash
TOKEN=$(./tools/get-token.sh an.nguyen)
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"sku":"SL-MOUSE-WL","qty":1}' http://localhost:5000/api/cart/items
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{}' http://localhost:5000/api/ordering/checkout   # 201, Paid
```

### Buổi 9 — Test kịch bản gãy + double submit

**Việc cần làm**

1. `StubHandler` giả các service; `SyncCheckoutFailureTests`: happy path xanh; inventory chết sau khi giữ, payment chậm, double submit — **đỏ có chủ đích**.
2. `tools/double-submit.sh`: gửi cùng checkout 2 lần gần như đồng thời.

**Lệnh**

```bash
mkdir -p tests/Ordering.Tests && python3 tools/sln-sync.py
dotnet test tests/Ordering.Tests     # 1 pass, 3 fail — đúng kỳ vọng của S2
./tools/double-submit.sh             # 2 đơn
```

**File**

- `tests/Ordering.Tests/Ordering.Tests.csproj`
- `tests/Ordering.Tests/StubHandler.cs`
- `tests/Ordering.Tests/SyncCheckoutFailureTests.cs`
- `tools/double-submit.sh`

#### `tests/Ordering.Tests/Ordering.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\TestKit\TestKit.csproj" />
    <ProjectReference Include="..\..\src\Services\Ordering.Api\Ordering.Api.csproj" />
  </ItemGroup>
</Project>
```

#### `tests/Ordering.Tests/StubHandler.cs`

```csharp
using System.Net;
using System.Text;
using System.Text.Json;

namespace Ordering.Tests;

/// <summary>
/// Giả lập các service mà Ordering gọi trong checkout đồng bộ (S2).
/// Mỗi route trả JSON cố định; có thể cấu hình độ trễ hoặc lỗi để tái hiện kịch bản gãy.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> _routes = new();
    public List<string> Calls { get; } = [];

    public StubHandler On(string method, string pathStartsWith, Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _routes[$"{method} {pathStartsWith}"] = handler;
        return this;
    }

    public StubHandler OnJson(string method, string pathStartsWith, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, pathStartsWith, _ => Task.FromResult(Json(body, status)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
        Calls.Add(key);
        var match = _routes.Where(r => key.StartsWith(r.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Key.Length).FirstOrDefault();
        return match.Value is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : await match.Value(request);
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
}
```

#### `tests/Ordering.Tests/SyncCheckoutFailureTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Api;
using Ordering.Api.Clients;
using Ordering.Api.Data;
using Ordering.Api.Domain;
using TestKit;

namespace Ordering.Tests;

/// <summary>
/// Tái hiện 3 chỗ gãy của checkout đồng bộ S2. Các test đang Skip vì ở S2 chúng ĐỎ đúng như mô tả;
/// S3 viết lại chúng cho luồng saga và bỏ Skip.
/// Muốn xem chúng đỏ ở S2: xoá tham số Skip rồi chạy dotnet test --filter SyncCheckoutFailureTests.
/// </summary>
public sealed class SyncCheckoutFailureTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly object Cart = new
    {
        couponCode = (string?)null,
        lines = new[] { new { sku = "SL-TEE-BASIC-M", name = "Áo", productSlug = "ao", qty = 1, unitPrice = 199000, lineTotal = 199000 } },
        itemCount = 1, subtotal = 199000
    };

    private async Task<(HttpClient Client, IServiceProvider Services)> CreateAsync(StubHandler stub)
    {
        var factory = new ShopLabApiFactory<ApiMarker>();
        factory.Settings["ConnectionStrings:orderingdb"] = await pg.CreateDatabaseAsync("ordering");
        // service discovery cần một địa chỉ cho mỗi tên; request không đi ra mạng vì StubHandler trả lời trước
        foreach (var svc in new[] { "cart", "customer", "catalog", "inventory", "payment" })
            factory.Settings[$"services:{svc}:http:0"] = $"http://{svc}.test";
        factory.ConfigureServicesHook = services =>
        {
            foreach (var name in new[] { nameof(CartClient), nameof(CustomerClient), nameof(CatalogClient), nameof(InventoryClient), nameof(PaymentClient) })
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        };
        return (factory.CreateClient().As(TestUsers.An, "customer"), factory.Services);
    }

    private static StubHandler HappyPath() => new StubHandler()
        .OnJson("GET", "/internal/carts/", Cart)
        .OnJson("DELETE", "/internal/carts/", new { })
        .OnJson("GET", "/internal/customers/", new { id = TestUsers.An, status = "Active" })
        .OnJson("POST", "/internal/variants/batch", new[] { new { sku = "SL-TEE-BASIC-M", name = "M", productName = "Áo", productSlug = "ao", categoryId = Guid.NewGuid(), categorySlug = "thoi-trang", price = 199000, compareAtPrice = (long?)null, isSellable = true } })
        .OnJson("POST", "/internal/reservations", new object[0])
        .OnJson("POST", "/internal/payments", new { id = Guid.NewGuid(), orderId = Guid.NewGuid(), amount = 199000, method = "Fake", status = "Succeeded" });

    [Fact]
    public async Task Happy_path_creates_one_paid_order()
    {
        var (client, sp) = await CreateAsync(HappyPath());
        var res = await client.PostAsJsonAsync("/checkout", new { }, TestContext.Current.CancellationToken);
        res.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await db.Orders.SingleAsync()).Status.ShouldBe(OrderStatus.Paid);
    }

    [Fact(Skip = "Gãy số 3 ở S2: gửi đôi tạo 2 order. S3 thêm Idempotency-Key rồi bỏ Skip.")]
    public async Task Double_submit_creates_only_one_order()
    {
        var (client, sp) = await CreateAsync(HappyPath());
        var ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(client.PostAsJsonAsync("/checkout", new { }, ct), client.PostAsJsonAsync("/checkout", new { }, ct));

        using var scope = sp.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.CountAsync(ct)).ShouldBe(1);
    }

    [Fact(Skip = "Gãy số 1 ở S2: Inventory lỗi sau khi giữ hàng → order kẹt. S3 dùng saga rồi bỏ Skip.")]
    public async Task Inventory_failure_after_reserve_never_leaves_order_stuck()
    {
        var stub = HappyPath().On("POST", "/internal/reservations/", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var (client, sp) = await CreateAsync(stub);
        await client.PostAsJsonAsync("/checkout", new { }, TestContext.Current.CancellationToken);

        using var scope = sp.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.SingleAsync();
        order.Status.ShouldBeOneOf(OrderStatus.Paid, OrderStatus.Cancelled);
    }

    [Fact(Skip = "Gãy số 2 ở S2: Payment chậm hơn timeout → order huỷ nhưng tiền đã trừ. S3 bỏ Skip.")]
    public async Task Slow_payment_never_cancels_an_order_that_was_charged()
    {
        var charged = false;
        var stub = HappyPath().On("POST", "/internal/payments", async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(35));
            charged = true;
            return StubHandler.Json(new { status = "Succeeded" });
        });
        var (client, sp) = await CreateAsync(stub);
        await client.PostAsJsonAsync("/checkout", new { }, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);

        using var scope = sp.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.SingleAsync();
        if (charged) order.Status.ShouldNotBe(OrderStatus.Cancelled);
    }
}
```

#### `tools/double-submit.sh`

```bash
#!/usr/bin/env bash
# Kịch bản gãy số 3: gửi cùng một checkout hai lần gần như đồng thời.
#   ./tools/double-submit.sh          # checkout thường (S2: ra 2 đơn; từ S3: 1 đơn nhờ Idempotency-Key)
#   ./tools/double-submit.sh flash    # flash sale (S6): cùng Idempotency-Key 3 lần
set -euo pipefail
GATEWAY="${GATEWAY:-http://localhost:5000}"
USER_HDR="X-Test-User: loadtest-double"
MODE="${1:-checkout}"
KEY="$(python3 -c 'import uuid; print(uuid.uuid4())')"

if [[ "$MODE" == "flash" ]]; then
  CAMP="${CAMP:-LAB-1111}"
  for i in 1 2 3; do
    curl -s -o /dev/null -w "lần $i: %{http_code}\n" -X POST "$GATEWAY/api/flashsale/flash/$CAMP/buy" \
      -H "$USER_HDR" -H "Idempotency-Key: $KEY" -H "Content-Type: application/json" \
      -d '{"sku":"SL-BOTTLE-500-SLV","qty":1}' &
  done
  wait
  exit 0
fi

curl -sf -X POST "$GATEWAY/api/cart/items" -H "$USER_HDR" -H "Content-Type: application/json" \
  -d '{"sku":"SL-TEE-BASIC-M","qty":1}' > /dev/null
for i in 1 2; do
  curl -s -o /dev/null -w "lần $i: %{http_code}\n" -X POST "$GATEWAY/api/ordering/checkout" \
    -H "$USER_HDR" -H "Idempotency-Key: $KEY" -H "Content-Type: application/json" -d '{}' &
done
wait
echo "Kiểm tra: SELECT number, status FROM ordering.orders ORDER BY created_at DESC LIMIT 5;"
```

**Xong tuần 2 khi:**

- [ ] Checkout bằng curl ra đơn Paid
- [ ] Hiểu vì sao 3 test đỏ

---

## Tuần 3 — UI mua hàng, admin vận hành, đo tải

**Mục tiêu tuần**

- Storefront: giỏ (badge, sửa số lượng), checkout chọn địa chỉ, kết quả đơn, đơn của tôi, hồ sơ.
- Admin: khách hàng (khoá/mở), tồn kho (nhập, chuyển kho, lịch sử), đơn hàng.
- k6 baseline checkout + ghi chú lỗi của cách đồng bộ.

### Buổi 10 — api-client + giỏ hàng storefront

**Việc cần làm**

1. `shop.models.ts` + Customer/Cart/Ordering/Inventory API.
2. `CartStore` (signal) dùng chung header/trang sản phẩm/trang giỏ; nút Thêm vào giỏ ở trang chi tiết.
3. App: đăng nhập xong gọi `/me/ensure` rồi gộp giỏ vãng lai.

**File**

- `src/Web/libs/api-client/src/index.ts` *(sửa)*
- `src/Web/libs/api-client/src/lib/cart.api.ts`
- `src/Web/libs/api-client/src/lib/customer.api.ts`
- `src/Web/libs/api-client/src/lib/inventory.api.ts`
- `src/Web/libs/api-client/src/lib/ordering.api.ts`
- `src/Web/libs/api-client/src/lib/shop.models.ts`
- `src/Web/apps/storefront/src/app/state/cart.store.ts`
- `src/Web/apps/storefront/src/app/pages/cart/cart.component.ts`
- `src/Web/apps/storefront/src/app/pages/product-detail/product-detail.component.html` *(sửa)*
- `src/Web/apps/storefront/src/app/pages/product-detail/product-detail.component.ts` *(sửa)*
- `src/Web/apps/storefront/src/app/layout/header.component.ts` *(sửa)*
- `src/Web/apps/storefront/src/app/app.component.ts` *(sửa)*

#### `src/Web/libs/api-client/src/index.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
export * from './lib/http-params';
export * from './lib/system.api';
export * from './lib/catalog.models';
export * from './lib/catalog.api';
export * from './lib/shop.models';
export * from './lib/customer.api';
export * from './lib/cart.api';
export * from './lib/ordering.api';
export * from './lib/inventory.api';
```

#### `src/Web/libs/api-client/src/lib/cart.api.ts`

```typescript
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Cart } from './shop.models';

const GUEST_KEY = 'shoplab.cartId';

/** Khách chưa đăng nhập: giỏ theo UUID lưu ở localStorage, gửi qua header X-Cart-Id. */
export function guestCartId(): string {
  let id: string | null = null;
  try {
    id = localStorage.getItem(GUEST_KEY);
    if (!id) {
      id = crypto.randomUUID();
      localStorage.setItem(GUEST_KEY, id);
    }
  } catch {
    id = id ?? crypto.randomUUID();
  }
  return id;
}

export function forgetGuestCart(): void {
  try {
    localStorage.removeItem(GUEST_KEY);
  } catch {
    /* bỏ qua */
  }
}

@Injectable({ providedIn: 'root' })
export class CartApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/cart';

  private headers(): HttpHeaders {
    return new HttpHeaders({ 'X-Cart-Id': guestCartId() });
  }

  get() {
    return this.http.get<Cart>(this.base, { headers: this.headers() });
  }
  add(sku: string, qty = 1) {
    return this.http.post<Cart>(`${this.base}/items`, { sku, qty }, { headers: this.headers() });
  }
  setQty(sku: string, qty: number) {
    return this.http.put<Cart>(`${this.base}/items/${encodeURIComponent(sku)}`, { qty }, { headers: this.headers() });
  }
  remove(sku: string) {
    return this.http.delete<Cart>(`${this.base}/items/${encodeURIComponent(sku)}`, { headers: this.headers() });
  }
  clear() {
    return this.http.delete<void>(this.base, { headers: this.headers() });
  }
  merge(guestCartId: string) {
    return this.http.post<Cart>(`${this.base}/merge`, { guestCartId });
  }
}
```

#### `src/Web/libs/api-client/src/lib/customer.api.ts`

```typescript
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Paged } from './catalog.models';
import { toParams } from './http-params';
import { Address, CustomerProfile, CustomerRow, SaveAddress } from './shop.models';

@Injectable({ providedIn: 'root' })
export class CustomerApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/customer';

  ensureMe() {
    return this.http.post<CustomerProfile>(`${this.base}/me/ensure`, {});
  }
  me() {
    return this.http.get<CustomerProfile>(`${this.base}/me`);
  }
  updateMe(body: { fullName: string; phone: string | null }) {
    return this.http.put<CustomerProfile>(`${this.base}/me`, body);
  }
  addresses() {
    return this.http.get<Address[]>(`${this.base}/me/addresses`);
  }
  addAddress(body: SaveAddress) {
    return this.http.post<Address>(`${this.base}/me/addresses`, body);
  }
  updateAddress(id: string, body: SaveAddress) {
    return this.http.put<Address>(`${this.base}/me/addresses/${id}`, body);
  }
  setDefaultAddress(id: string) {
    return this.http.post<void>(`${this.base}/me/addresses/${id}/default`, {});
  }
  deleteAddress(id: string) {
    return this.http.delete<void>(`${this.base}/me/addresses/${id}`);
  }
}

@Injectable({ providedIn: 'root' })
export class AdminCustomerApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/admin/customer/customers';

  list(query: { q?: string; cursor?: string | null; size?: number }) {
    return this.http.get<Paged<CustomerRow>>(this.base, { params: toParams(query) });
  }
  get(id: string) {
    return this.http.get<CustomerProfile>(`${this.base}/${id}`);
  }
  ban(id: string) {
    return this.http.post<void>(`${this.base}/${id}/ban`, {});
  }
  unban(id: string) {
    return this.http.post<void>(`${this.base}/${id}/unban`, {});
  }
}
```

#### `src/Web/libs/api-client/src/lib/inventory.api.ts`

```typescript
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { toParams } from './http-params';
import { Movement, StockLevel, StockLocation } from './shop.models';

@Injectable({ providedIn: 'root' })
export class AdminInventoryApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/admin/inventory';

  locations() {
    return this.http.get<StockLocation[]>(`${this.base}/locations`);
  }
  stock(query: { location?: string; lowThreshold?: number | null }) {
    return this.http.get<StockLevel[]>(`${this.base}/stock`, { params: toParams(query) });
  }
  movements(sku: string, beforeId?: number | null) {
    return this.http.get<Movement[]>(`${this.base}/stock/${encodeURIComponent(sku)}/movements`, { params: toParams({ beforeId }) });
  }
  receive(body: { sku: string; locationCode: string; qty: number; note?: string }) {
    return this.http.post<{ refId: string }>(`${this.base}/stock/receipts`, body);
  }
  adjust(body: { sku: string; locationCode: string; countedQty: number; reason?: string }) {
    return this.http.post<{ delta: number }>(`${this.base}/stock/adjustments`, body);
  }
  transfer(body: { sku: string; fromCode: string; toCode: string; qty: number }) {
    return this.http.post<{ refId: string }>(`${this.base}/stock/transfers`, body);
  }
}
```

#### `src/Web/libs/api-client/src/lib/ordering.api.ts`

```typescript
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Paged } from './catalog.models';
import { toParams } from './http-params';
import { Order, OrderSummary } from './shop.models';

@Injectable({ providedIn: 'root' })
export class OrderingApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/ordering';

  /** idempotencyKey được dùng từ S3 (header Idempotency-Key). */
  checkout(body: { addressId?: string | null }, idempotencyKey?: string) {
    const headers = idempotencyKey ? new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) : undefined;
    return this.http.post<Order>(`${this.base}/checkout`, body, { headers });
  }
  myOrders(cursor?: string | null) {
    return this.http.get<Paged<OrderSummary>>(`${this.base}/orders`, { params: toParams({ cursor }) });
  }
  get(id: string) {
    return this.http.get<Order>(`${this.base}/orders/${id}`);
  }
}

@Injectable({ providedIn: 'root' })
export class AdminOrderingApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/admin/ordering/orders';

  list(query: { status?: string; channel?: string; cursor?: string | null; size?: number }) {
    return this.http.get<Paged<OrderSummary>>(this.base, { params: toParams(query) });
  }
  get(id: string) {
    return this.http.get<Order>(`/api/ordering/orders/${id}`);
  }
}
```

#### `src/Web/libs/api-client/src/lib/shop.models.ts`

```typescript
// ---- Customer ----
export interface Address {
  id: string;
  line1: string;
  ward: string;
  district: string;
  city: string;
  isDefault: boolean;
}
export interface SaveAddress {
  line1: string;
  ward: string;
  district: string;
  city: string;
  isDefault?: boolean;
}
export interface CustomerProfile {
  id: string;
  email: string;
  fullName: string;
  phone: string | null;
  status: 'Active' | 'Banned';
  points: number;
  tier: 'Member' | 'Silver' | 'Gold';
  addresses: Address[];
  createdAt: string;
}
export interface CustomerRow {
  id: string;
  email: string;
  fullName: string;
  phone: string | null;
  status: 'Active' | 'Banned';
  points: number;
  tier: string;
}

// ---- Cart ----
export interface CartLine {
  sku: string;
  name: string;
  productSlug: string;
  qty: number;
  unitPrice: number;
  lineTotal: number;
}
export interface Cart {
  couponCode: string | null;
  lines: CartLine[];
  itemCount: number;
  subtotal: number;
  updatedAt: string;
}

// ---- Ordering ----
export type OrderStatus = 'PendingStock' | 'PendingPayment' | 'Paid' | 'Completed' | 'Cancelled';
export interface OrderLine {
  sku: string;
  name: string;
  qty: number;
  unitPrice: number;
  lineDiscount: number;
  lineTotal: number;
}
export interface Order {
  id: string;
  number: string;
  customerId: string | null;
  channel: 'Online' | 'Pos' | 'FlashSale';
  storeId: string | null;
  status: OrderStatus;
  subtotal: number;
  discountTotal: number;
  total: number;
  couponCode: string | null;
  shippingAddress: string | null;
  cancelReason: string | null;
  lines: OrderLine[];
  createdAt: string;
  updatedAt: string;
}
export interface OrderSummary {
  id: string;
  number: string;
  customerId: string | null;
  channel: string;
  status: OrderStatus;
  total: number;
  itemCount: number;
  createdAt: string;
}

// ---- Inventory ----
export interface StockLocation {
  id: string;
  code: string;
  name: string;
  type: 'Warehouse' | 'Store';
  sellableOnline: boolean;
}
export interface StockLevel {
  sku: string;
  locationId: string;
  locationCode: string;
  onHand: number;
  reserved: number;
  available: number;
}
export interface Movement {
  id: number;
  sku: string;
  locationCode: string;
  qty: number;
  type: string;
  refType: string;
  refId: string;
  by: string;
  note: string | null;
  createdAt: string;
}

export const ORDER_STATUS_LABEL: Record<OrderStatus, string> = {
  PendingStock: 'Đang giữ hàng',
  PendingPayment: 'Chờ thanh toán',
  Paid: 'Đã thanh toán',
  Completed: 'Hoàn tất',
  Cancelled: 'Đã huỷ',
};
```

#### `src/Web/apps/storefront/src/app/state/cart.store.ts`

```typescript
import { Injectable, computed, inject, signal } from '@angular/core';
import { Cart, CartApi, forgetGuestCart, guestCartId, readProblem } from '@shoplab/api-client';
import { Observable, tap } from 'rxjs';

/** Trạng thái giỏ hàng dùng chung cho header (badge), trang sản phẩm và trang giỏ. */
@Injectable({ providedIn: 'root' })
export class CartStore {
  private readonly api = inject(CartApi);

  readonly cart = signal<Cart | null>(null);
  readonly count = computed(() => this.cart()?.itemCount ?? 0);
  readonly error = signal<string | null>(null);

  load(): void {
    this.api.get().subscribe({ next: (c) => this.cart.set(c), error: () => this.cart.set(null) });
  }

  add(sku: string, qty = 1): Observable<Cart> {
    return this.track(this.api.add(sku, qty));
  }

  setQty(sku: string, qty: number): Observable<Cart> {
    return this.track(this.api.setQty(sku, qty));
  }

  remove(sku: string): Observable<Cart> {
    return this.track(this.api.remove(sku));
  }

  /** Sau khi đăng nhập: gộp giỏ vãng lai vào giỏ của user rồi bỏ id vãng lai. */
  mergeGuestCart(): void {
    const guestId = guestCartId();
    this.api.merge(guestId).subscribe({
      next: (c) => {
        this.cart.set(c);
        forgetGuestCart();
      },
      error: () => this.load(),
    });
  }

  private track(obs: Observable<Cart>): Observable<Cart> {
    this.error.set(null);
    return obs.pipe(
      tap({
        next: (c) => this.cart.set(c),
        error: (e) => this.error.set(readProblem(e).title),
      }),
    );
  }
}
```

#### `src/Web/apps/storefront/src/app/pages/cart/cart.component.ts`

```typescript
import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '@shoplab/auth';
import { VndPipe } from '@shoplab/ui';
import { CartStore } from '../../state/cart.store';

@Component({
  selector: 'app-cart',
  imports: [RouterLink, VndPipe],
  template: `
    <h1>Giỏ hàng</h1>
    @if (store.cart(); as cart) {
      @if (cart.lines.length === 0) {
        <p class="muted">Giỏ hàng trống. <a routerLink="/">Tiếp tục mua sắm</a></p>
      } @else {
        <table>
          <thead><tr><th>Sản phẩm</th><th>Đơn giá</th><th>Số lượng</th><th>Thành tiền</th><th></th></tr></thead>
          <tbody>
            @for (l of cart.lines; track l.sku) {
              <tr>
                <td><a [routerLink]="['/p', l.productSlug]">{{ l.name }}</a><div class="muted small">{{ l.sku }}</div></td>
                <td>{{ l.unitPrice | vnd }}</td>
                <td><input type="number" min="0" max="99" [value]="l.qty" (change)="setQty(l.sku, $any($event.target).value)" style="width: 72px" /></td>
                <td>{{ l.lineTotal | vnd }}</td>
                <td><button class="danger" (click)="store.remove(l.sku).subscribe()">Xoá</button></td>
              </tr>
            }
          </tbody>
        </table>
        <!-- S4: ô nhập coupon và bảng giảm giá -->
        <div class="summary card">
          <div class="row"><span class="grow">Tạm tính</span><strong>{{ cart.subtotal | vnd }}</strong></div>
          <p class="muted small">Giá trong giỏ là giá lúc bạn thêm; khi đặt hàng hệ thống lấy giá mới nhất.</p>
          @if (auth.isLoggedIn()) {
            <a routerLink="/checkout"><button class="primary">Đặt hàng</button></a>
          } @else {
            <button class="primary" (click)="auth.login('/checkout')">Đăng nhập để đặt hàng</button>
          }
        </div>
      }
      @if (store.error()) { <p class="error">{{ store.error() }}</p> }
    } @else {
      <p class="muted">Đang tải…</p>
    }
  `,
  styles: `
    .summary { margin-top: 16px; max-width: 360px; margin-left: auto; display: grid; gap: 8px; }
    .small { font-size: 12px; }
  `,
})
export class CartComponent {
  protected readonly store = inject(CartStore);
  protected readonly auth = inject(AuthService);

  setQty(sku: string, value: string): void {
    this.store.setQty(sku, Math.max(0, Math.min(99, Number(value) || 0))).subscribe();
  }
}
```

#### `src/Web/apps/storefront/src/app/pages/product-detail/product-detail.component.html` *(sửa — nội dung đầy đủ sau khi sửa)*

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

      <div class="row buy">
        <input type="number" min="1" max="99" [value]="qty()" (change)="qty.set(+$any($event.target).value)" style="width: 72px" />
        <button class="primary" [disabled]="!variant()?.inStock || adding()" (click)="addToCart()">Thêm vào giỏ</button>
        @if (added()) { <span class="ok">Đã thêm vào giỏ</span> }
        @if (cart.error()) { <span class="error">{{ cart.error() }}</span> }
      </div>

      <h4>Mô tả</h4>
      <p>{{ p.description }}</p>
      <p class="muted">SKU: {{ variant()?.sku }}</p>
    </div>
  </div>
} @else {
  <p class="muted">Đang tải…</p>
}
```

#### `src/Web/apps/storefront/src/app/pages/product-detail/product-detail.component.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { CatalogApi, Product } from '@shoplab/api-client';
import { VndPipe } from '@shoplab/ui';
import { CartStore } from '../../state/cart.store';

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
    .buy { margin: 16px 0; }
    @media (max-width: 720px) { .layout { grid-template-columns: 1fr; } }
  `,
})
export class ProductDetailComponent {
  private readonly api = inject(CatalogApi);
  private readonly router = inject(Router);
  protected readonly cart = inject(CartStore);
  protected readonly qty = signal(1);
  protected readonly adding = signal(false);
  protected readonly added = signal(false);

  readonly slug = input.required<string>();

  protected readonly product = signal<Product | null>(null);
  protected readonly selectedSku = signal<string | null>(null);
  protected readonly variant = computed(() => {
    const p = this.product();
    return p?.variants.find((v) => v.sku === this.selectedSku()) ?? p?.variants[0] ?? null;
  });

  addToCart(): void {
    const v = this.variant();
    if (!v) return;
    this.adding.set(true);
    this.added.set(false);
    this.cart.add(v.sku, this.qty()).subscribe({
      next: () => {
        this.adding.set(false);
        this.added.set(true);
      },
      error: () => this.adding.set(false),
    });
  }

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

#### `src/Web/apps/storefront/src/app/layout/header.component.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '@shoplab/auth';
import { CartStore } from '../state/cart.store';

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
        <a routerLink="/cart" class="cart">Giỏ hàng @if (cart.count() > 0) { <span class="badge">{{ cart.count() }}</span> }</a>
        @if (auth.user(); as user) {
          <a routerLink="/orders">Đơn của tôi</a>
          <a routerLink="/profile" class="muted">{{ user.name }}</a>
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
    .cart { color: var(--ink); }
  `,
})
export class HeaderComponent {
  protected readonly auth = inject(AuthService);
  protected readonly cart = inject(CartStore);
  private readonly router = inject(Router);
  protected q = '';

  search(): void {
    const q = this.q.trim();
    this.router.navigate(['/'], { queryParams: q ? { q } : {} });
  }
}
```

#### `src/Web/apps/storefront/src/app/app.component.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
import { Component, computed, effect, inject, untracked } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AuthService } from '@shoplab/auth';
import { CustomerApi } from '@shoplab/api-client';
import { HeaderComponent } from './layout/header.component';
import { CartStore } from './state/cart.store';

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
export class AppComponent {
  private readonly auth = inject(AuthService);
  private readonly customers = inject(CustomerApi);
  private readonly cart = inject(CartStore);
  /** Chỉ phản ứng khi đổi người dùng, không phải mỗi lần token được làm mới. */
  private readonly userId = computed(() => this.auth.user()?.id ?? null);

  constructor() {
    // Đăng nhập xong: tạo hồ sơ khách (nếu chưa có) rồi gộp giỏ vãng lai. Chưa đăng nhập: tải giỏ vãng lai.
    effect(() => {
      const userId = this.userId();
      untracked(() => {
        if (userId) {
          this.customers.ensureMe().subscribe({ complete: () => this.cart.mergeGuestCart(), error: () => this.cart.load() });
        } else {
          this.cart.load();
        }
      });
    });
  }
}
```

### Buổi 11 — Checkout, đơn hàng, hồ sơ

**Việc cần làm**

1. Checkout: chọn địa chỉ, map mã lỗi (OUT_OF_STOCK, CUSTOMER_BANNED…) sang tiếng Việt.
2. Kết quả đơn, danh sách đơn của tôi (cursor), chi tiết đơn, hồ sơ + địa chỉ.

**File**

- `src/Web/apps/storefront/src/app/pages/checkout/checkout.component.ts`
- `src/Web/apps/storefront/src/app/pages/my-orders/my-orders.component.ts`
- `src/Web/apps/storefront/src/app/pages/order-detail/order-detail.component.ts`
- `src/Web/apps/storefront/src/app/pages/order-result/order-result.component.ts`
- `src/Web/apps/storefront/src/app/pages/profile/profile.component.ts`
- `src/Web/apps/storefront/src/app/app.routes.ts` *(sửa)*

#### `src/Web/apps/storefront/src/app/pages/checkout/checkout.component.ts`

```typescript
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Address, CustomerApi, OrderingApi, readProblem } from '@shoplab/api-client';
import { VndPipe } from '@shoplab/ui';
import { CartStore } from '../../state/cart.store';

/** Thông điệp theo mã lỗi nghiệp vụ trả về từ Ordering. */
const ERROR_TEXT: Record<string, string> = {
  OUT_OF_STOCK: 'Một sản phẩm trong giỏ vừa hết hàng.',
  CUSTOMER_BANNED: 'Tài khoản của bạn đang bị khoá, không thể đặt hàng.',
  PAYMENT_FAILED: 'Thanh toán không thành công, bạn chưa bị trừ tiền.',
  CART_EMPTY: 'Giỏ hàng trống.',
  SKU_NOT_SELLABLE: 'Có sản phẩm đã ngừng bán, hãy xoá khỏi giỏ.',
};

@Component({
  selector: 'app-checkout',
  imports: [FormsModule, RouterLink, VndPipe],
  template: `
    <h1>Đặt hàng</h1>
    <h3>Giao đến</h3>
    @if (addresses().length === 0) {
      <p class="muted">Bạn chưa có địa chỉ. <a routerLink="/profile">Thêm địa chỉ</a></p>
    }
    @for (a of addresses(); track a.id) {
      <label class="row"><input type="radio" name="addr" [value]="a.id" [(ngModel)]="addressId" /> {{ a.line1 }}, {{ a.ward }}, {{ a.district }}, {{ a.city }}</label>
    }

    @if (cart.cart(); as c) {
      <h3>Sản phẩm</h3>
      <ul>
        @for (l of c.lines; track l.sku) { <li>{{ l.qty }} × {{ l.name }} — {{ l.lineTotal | vnd }}</li> }
      </ul>
      <p>Tạm tính: <strong>{{ c.subtotal | vnd }}</strong></p>
    }

    @if (error()) { <p class="error">{{ error() }}</p> }
    <button class="primary" [disabled]="placing() || !cart.cart()?.lines?.length" (click)="place()">
      {{ placing() ? 'Đang đặt hàng…' : 'Xác nhận đặt hàng' }}
    </button>
  `,
})
export class CheckoutComponent {
  private readonly customers = inject(CustomerApi);
  private readonly ordering = inject(OrderingApi);
  private readonly router = inject(Router);
  protected readonly cart = inject(CartStore);

  protected readonly addresses = signal<Address[]>([]);
  protected readonly placing = signal(false);
  protected readonly error = signal<string | null>(null);
  protected addressId: string | null = null;

  constructor() {
    this.cart.load();
    this.customers.addresses().subscribe((list) => {
      this.addresses.set(list);
      this.addressId = list.find((a) => a.isDefault)?.id ?? list[0]?.id ?? null;
    });
  }

  place(): void {
    this.placing.set(true);
    this.error.set(null);
    // S2: không gửi Idempotency-Key — bấm hai lần sẽ ra hai đơn (kịch bản gãy số 3).
    this.ordering.checkout({ addressId: this.addressId }).subscribe({
      next: (order) => {
        this.cart.load();
        this.router.navigate(['/orders', order.id, 'result']);
      },
      error: (e) => {
        const p = readProblem(e);
        this.error.set((p.code && ERROR_TEXT[p.code]) || p.title);
        this.placing.set(false);
      },
    });
  }
}
```

#### `src/Web/apps/storefront/src/app/pages/my-orders/my-orders.component.ts`

```typescript
import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ORDER_STATUS_LABEL, OrderSummary, OrderingApi } from '@shoplab/api-client';
import { VndPipe } from '@shoplab/ui';

@Component({
  selector: 'app-my-orders',
  imports: [RouterLink, VndPipe, DatePipe],
  template: `
    <h1>Đơn của tôi</h1>
    <table>
      <thead><tr><th>Mã đơn</th><th>Ngày</th><th>Số món</th><th>Tổng</th><th>Trạng thái</th></tr></thead>
      <tbody>
        @for (o of orders(); track o.id) {
          <tr>
            <td><a [routerLink]="['/orders', o.id]">{{ o.number }}</a></td>
            <td>{{ o.createdAt | date: 'dd/MM/yyyy HH:mm' }}</td>
            <td>{{ o.itemCount }}</td>
            <td>{{ o.total | vnd }}</td>
            <td>{{ labels[o.status] }}</td>
          </tr>
        } @empty {
          <tr><td colspan="5" class="muted">Chưa có đơn nào.</td></tr>
        }
      </tbody>
    </table>
    @if (next()) { <button (click)="load(next())">Xem thêm</button> }
  `,
})
export class MyOrdersComponent {
  private readonly api = inject(OrderingApi);
  protected readonly orders = signal<OrderSummary[]>([]);
  protected readonly next = signal<string | null>(null);
  protected readonly labels = ORDER_STATUS_LABEL;

  constructor() {
    this.load(null);
  }

  load(cursor: string | null): void {
    this.api.myOrders(cursor).subscribe((page) => {
      this.orders.update((list) => (cursor ? [...list, ...page.items] : page.items));
      this.next.set(page.nextCursor);
    });
  }
}
```

#### `src/Web/apps/storefront/src/app/pages/order-detail/order-detail.component.ts`

```typescript
import { Component, effect, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ORDER_STATUS_LABEL, Order, OrderingApi } from '@shoplab/api-client';
import { VndPipe } from '@shoplab/ui';

@Component({
  selector: 'app-order-detail',
  imports: [VndPipe, DatePipe],
  template: `
    @if (order(); as o) {
      <h1>Đơn {{ o.number }}</h1>
      <p>{{ o.createdAt | date: 'dd/MM/yyyy HH:mm' }} · <strong>{{ labels[o.status] }}</strong></p>
      @if (o.cancelReason) { <p class="error">Lý do huỷ: {{ o.cancelReason }}</p> }
      <p class="muted">Giao đến: {{ o.shippingAddress ?? '—' }}</p>
      <table>
        <thead><tr><th>Sản phẩm</th><th>Đơn giá</th><th>SL</th><th>Giảm</th><th>Thành tiền</th></tr></thead>
        <tbody>
          @for (l of o.lines; track l.sku) {
            <tr><td>{{ l.name }}</td><td>{{ l.unitPrice | vnd }}</td><td>{{ l.qty }}</td><td>{{ l.lineDiscount | vnd }}</td><td>{{ l.lineTotal | vnd }}</td></tr>
          }
        </tbody>
      </table>
      <div class="totals">
        <div>Tạm tính: {{ o.subtotal | vnd }}</div>
        @if (o.discountTotal > 0) { <div>Giảm giá: −{{ o.discountTotal | vnd }} @if (o.couponCode) { ({{ o.couponCode }}) }</div> }
        <div><strong>Tổng: {{ o.total | vnd }}</strong></div>
      </div>
    }
  `,
  styles: `.totals { margin-top: 12px; text-align: right; display: grid; gap: 4px; }`,
})
export class OrderDetailComponent {
  private readonly api = inject(OrderingApi);
  readonly id = input.required<string>();
  protected readonly order = signal<Order | null>(null);
  protected readonly labels = ORDER_STATUS_LABEL;

  constructor() {
    effect(() => this.api.get(this.id()).subscribe((o) => this.order.set(o)));
  }
}
```

#### `src/Web/apps/storefront/src/app/pages/order-result/order-result.component.ts`

```typescript
import { Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ORDER_STATUS_LABEL, Order, OrderingApi } from '@shoplab/api-client';
import { VndPipe } from '@shoplab/ui';

@Component({
  selector: 'app-order-result',
  imports: [RouterLink, VndPipe],
  template: `
    @if (order(); as o) {
      <div class="card result">
        @switch (o.status) {
          @case ('Paid') { <h2 class="ok">Đặt hàng thành công</h2> }
          @case ('Cancelled') { <h2 class="error">Đơn đã bị huỷ</h2><p>{{ o.cancelReason }}</p> }
          @default { <h2>{{ labels[o.status] }}…</h2> }
        }
        <p>Mã đơn: <strong>{{ o.number }}</strong></p>
        <p>Tổng tiền: <strong>{{ o.total | vnd }}</strong></p>
        <a [routerLink]="['/orders', o.id]">Xem chi tiết đơn</a>
      </div>
    }
  `,
  styles: `.result { max-width: 480px; margin: 32px auto; text-align: center; }`,
})
export class OrderResultComponent {
  private readonly api = inject(OrderingApi);
  readonly id = input.required<string>();
  protected readonly order = signal<Order | null>(null);
  protected readonly labels = ORDER_STATUS_LABEL;

  constructor() {
    effect(() => this.api.get(this.id()).subscribe((o) => this.order.set(o)));
  }
}
```

#### `src/Web/apps/storefront/src/app/pages/profile/profile.component.ts`

```typescript
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Address, CustomerApi, CustomerProfile, readProblem } from '@shoplab/api-client';

@Component({
  selector: 'app-profile',
  imports: [ReactiveFormsModule],
  template: `
    <h1>Hồ sơ</h1>
    @if (profile(); as p) {
      <p>{{ p.email }} · Hạng <strong>{{ p.tier }}</strong> · {{ p.points }} điểm</p>
      @if (p.status === 'Banned') { <p class="error">Tài khoản đang bị khoá.</p> }
    }

    <h3>Địa chỉ giao hàng</h3>
    <table>
      <tbody>
        @for (a of addresses(); track a.id) {
          <tr>
            <td>{{ a.line1 }}, {{ a.ward }}, {{ a.district }}, {{ a.city }}</td>
            <td>@if (a.isDefault) { <span class="badge">Mặc định</span> } @else { <button (click)="setDefault(a)">Đặt mặc định</button> }</td>
            <td><button class="danger" (click)="remove(a)">Xoá</button></td>
          </tr>
        } @empty {
          <tr><td class="muted">Chưa có địa chỉ.</td></tr>
        }
      </tbody>
    </table>

    <form class="card form" [formGroup]="form" (ngSubmit)="add()">
      <h4>Thêm địa chỉ</h4>
      <label>Số nhà, đường <input formControlName="line1" /></label>
      <label>Phường/xã <input formControlName="ward" /></label>
      <label>Quận/huyện <input formControlName="district" /></label>
      <label>Tỉnh/thành <input formControlName="city" /></label>
      <label class="row"><input type="checkbox" formControlName="isDefault" /> Đặt làm mặc định</label>
      @if (error()) { <p class="error">{{ error() }}</p> }
      <button class="primary" [disabled]="form.invalid">Thêm</button>
    </form>
  `,
  styles: `form { margin-top: 16px; }`,
})
export class ProfileComponent {
  private readonly api = inject(CustomerApi);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly profile = signal<CustomerProfile | null>(null);
  protected readonly addresses = signal<Address[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly form = this.fb.group({
    line1: ['', Validators.required],
    ward: ['', Validators.required],
    district: ['', Validators.required],
    city: ['', Validators.required],
    isDefault: [false],
  });

  constructor() {
    this.reload();
  }

  reload(): void {
    this.api.me().subscribe((p) => this.profile.set(p));
    this.api.addresses().subscribe((a) => this.addresses.set(a));
  }

  add(): void {
    this.api.addAddress(this.form.getRawValue()).subscribe({
      next: () => {
        this.form.reset({ line1: '', ward: '', district: '', city: '', isDefault: false });
        this.reload();
      },
      error: (e) => this.error.set(readProblem(e).title),
    });
  }

  setDefault(a: Address): void {
    this.api.setDefaultAddress(a.id).subscribe(() => this.reload());
  }

  remove(a: Address): void {
    if (confirm('Xoá địa chỉ này?')) this.api.deleteAddress(a.id).subscribe(() => this.reload());
  }
}
```

#### `src/Web/apps/storefront/src/app/app.routes.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
import { Routes } from '@angular/router';
import { loggedInGuard } from '@shoplab/auth';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./pages/product-list/product-list.component').then((m) => m.ProductListComponent) },
  { path: 'c/:slug', loadComponent: () => import('./pages/product-list/product-list.component').then((m) => m.ProductListComponent) },
  { path: 'p/:slug', loadComponent: () => import('./pages/product-detail/product-detail.component').then((m) => m.ProductDetailComponent) },
  // S2
  { path: 'cart', loadComponent: () => import('./pages/cart/cart.component').then((m) => m.CartComponent) },
  { path: 'checkout', canActivate: [loggedInGuard], loadComponent: () => import('./pages/checkout/checkout.component').then((m) => m.CheckoutComponent) },
  { path: 'orders', canActivate: [loggedInGuard], loadComponent: () => import('./pages/my-orders/my-orders.component').then((m) => m.MyOrdersComponent) },
  { path: 'orders/:id/result', canActivate: [loggedInGuard], loadComponent: () => import('./pages/order-result/order-result.component').then((m) => m.OrderResultComponent) },
  { path: 'orders/:id', canActivate: [loggedInGuard], loadComponent: () => import('./pages/order-detail/order-detail.component').then((m) => m.OrderDetailComponent) },
  { path: 'profile', canActivate: [loggedInGuard], loadComponent: () => import('./pages/profile/profile.component').then((m) => m.ProfileComponent) },
  { path: 'dev/ping', loadComponent: () => import('./pages/home/home.component').then((m) => m.HomeComponent) },
  { path: 'forbidden', loadComponent: () => import('./pages/forbidden.component').then((m) => m.ForbiddenComponent) },
  { path: '**', loadComponent: () => import('./pages/not-found.component').then((m) => m.NotFoundComponent) },
];
```

**Kiểm tra cuối buổi**

```bash
# :4200 mua 1 sản phẩm từ đầu đến cuối bằng an.nguyen; chi.le (bị khoá) đặt → thông báo tài khoản bị khoá
```

### Buổi 12 — Admin: khách hàng, tồn kho, đơn hàng

**Việc cần làm**

1. Khách: tìm, chi tiết, ban/unban.
2. Tồn kho: danh sách theo SKU/kho, phiếu nhập, chuyển kho, lịch sử movement.
3. Đơn hàng: lọc theo trạng thái/kênh.

**File**

- `src/Web/apps/admin/src/app/app.routes.ts` *(sửa)*
- `src/Web/apps/admin/src/app/layout/shell.component.ts` *(sửa)*
- `src/Web/apps/admin/src/app/pages/customers/customer-detail.component.ts`
- `src/Web/apps/admin/src/app/pages/customers/customer-list.component.ts`
- `src/Web/apps/admin/src/app/pages/inventory/movements.component.ts`
- `src/Web/apps/admin/src/app/pages/inventory/receipt-form.component.ts`
- `src/Web/apps/admin/src/app/pages/inventory/stock-list.component.ts`
- `src/Web/apps/admin/src/app/pages/inventory/transfer-form.component.ts`
- `src/Web/apps/admin/src/app/pages/orders/order-list.component.ts`

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
      // S2
      { path: 'customers', loadComponent: () => import('./pages/customers/customer-list.component').then((m) => m.CustomerListComponent) },
      { path: 'customers/:id', loadComponent: () => import('./pages/customers/customer-detail.component').then((m) => m.CustomerDetailComponent) },
      { path: 'inventory', loadComponent: () => import('./pages/inventory/stock-list.component').then((m) => m.StockListComponent) },
      { path: 'inventory/:sku', loadComponent: () => import('./pages/inventory/movements.component').then((m) => m.MovementsComponent) },
      { path: 'orders', loadComponent: () => import('./pages/orders/order-list.component').then((m) => m.OrderListComponent) },
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
    { path: '/inventory', label: 'Tồn kho' },
    { path: '/orders', label: 'Đơn hàng' },
    { path: '/customers', label: 'Khách hàng' },
  ];
}
```

#### `src/Web/apps/admin/src/app/pages/customers/customer-detail.component.ts`

```typescript
import { Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminCustomerApi, CustomerProfile } from '@shoplab/api-client';

@Component({
  selector: 'app-customer-detail',
  imports: [RouterLink],
  template: `
    <a routerLink="/customers">← Khách hàng</a>
    @if (c(); as c) {
      <h1>{{ c.fullName }}</h1>
      <p>{{ c.email }} · {{ c.phone ?? 'chưa có SĐT' }} · {{ c.status }}</p>
      <p>Hạng {{ c.tier }} · {{ c.points }} điểm</p>
      <h3>Địa chỉ</h3>
      <ul>
        @for (a of c.addresses; track a.id) {
          <li>{{ a.line1 }}, {{ a.ward }}, {{ a.district }}, {{ a.city }} @if (a.isDefault) { <span class="badge">Mặc định</span> }</li>
        }
      </ul>
    }
  `,
})
export class CustomerDetailComponent {
  private readonly api = inject(AdminCustomerApi);
  readonly id = input.required<string>();
  protected readonly c = signal<CustomerProfile | null>(null);

  constructor() {
    effect(() => this.api.get(this.id()).subscribe((x) => this.c.set(x)));
  }
}
```

#### `src/Web/apps/admin/src/app/pages/customers/customer-list.component.ts`

```typescript
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminCustomerApi, CustomerRow } from '@shoplab/api-client';

@Component({
  selector: 'app-customer-list',
  imports: [FormsModule, RouterLink],
  template: `
    <h1>Khách hàng</h1>
    <form class="row" (ngSubmit)="load(null)">
      <input name="q" [(ngModel)]="q" placeholder="Email, tên hoặc SĐT" />
      <button>Tìm</button>
    </form>
    <table>
      <thead><tr><th>Email</th><th>Tên</th><th>SĐT</th><th>Hạng</th><th>Điểm</th><th>Trạng thái</th><th></th></tr></thead>
      <tbody>
        @for (c of rows(); track c.id) {
          <tr>
            <td><a [routerLink]="['/customers', c.id]">{{ c.email }}</a></td>
            <td>{{ c.fullName }}</td>
            <td>{{ c.phone ?? '—' }}</td>
            <td>{{ c.tier }}</td>
            <td>{{ c.points }}</td>
            <td><span class="badge" [class.off]="c.status === 'Banned'">{{ c.status }}</span></td>
            <td>
              @if (c.status === 'Banned') {
                <button (click)="unban(c)">Mở khoá</button>
              } @else {
                <button class="danger" (click)="ban(c)">Khoá</button>
              }
            </td>
          </tr>
        }
      </tbody>
    </table>
    @if (next()) { <button (click)="load(next())">Xem thêm</button> }
  `,
})
export class CustomerListComponent {
  private readonly api = inject(AdminCustomerApi);
  protected q = '';
  protected readonly rows = signal<CustomerRow[]>([]);
  protected readonly next = signal<string | null>(null);

  constructor() {
    this.load(null);
  }

  load(cursor: string | null): void {
    this.api.list({ q: this.q, cursor, size: 25 }).subscribe((page) => {
      this.rows.update((list) => (cursor ? [...list, ...page.items] : page.items));
      this.next.set(page.nextCursor);
    });
  }

  ban(c: CustomerRow): void {
    if (confirm(`Khoá ${c.email}? Khách sẽ không đặt hàng được.`)) this.api.ban(c.id).subscribe(() => this.load(null));
  }

  unban(c: CustomerRow): void {
    this.api.unban(c.id).subscribe(() => this.load(null));
  }
}
```

#### `src/Web/apps/admin/src/app/pages/inventory/movements.component.ts`

```typescript
import { Component, effect, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AdminInventoryApi, Movement } from '@shoplab/api-client';

/** Sổ cái của một SKU: mới nhất trước. Đây là chỗ trả lời "vì sao tồn kho lệch". */
@Component({
  selector: 'app-movements',
  imports: [DatePipe, RouterLink],
  template: `
    <a routerLink="/inventory">← Tồn kho</a>
    <h1>Lịch sử {{ sku() }}</h1>
    <table>
      <thead><tr><th>#</th><th>Thời gian</th><th>Location</th><th>Loại</th><th>SL</th><th>Tham chiếu</th><th>Bởi</th><th>Ghi chú</th></tr></thead>
      <tbody>
        @for (m of rows(); track m.id) {
          <tr>
            <td>{{ m.id }}</td>
            <td>{{ m.createdAt | date: 'dd/MM HH:mm:ss' }}</td>
            <td>{{ m.locationCode }}</td>
            <td>{{ m.type }}</td>
            <td [class.ok]="m.qty > 0" [class.error]="m.qty < 0">{{ m.qty > 0 ? '+' : '' }}{{ m.qty }}</td>
            <td class="muted">{{ m.refType }}:{{ m.refId }}</td>
            <td>{{ m.by }}</td>
            <td>{{ m.note }}</td>
          </tr>
        }
      </tbody>
    </table>
    @if (rows().length && rows().length % 50 === 0) { <button (click)="more()">Xem cũ hơn</button> }
  `,
})
export class MovementsComponent {
  private readonly api = inject(AdminInventoryApi);
  readonly sku = input.required<string>();
  protected readonly rows = signal<Movement[]>([]);

  constructor() {
    effect(() => this.api.movements(this.sku()).subscribe((r) => this.rows.set(r)));
  }

  more(): void {
    const last = this.rows().at(-1);
    this.api.movements(this.sku(), last?.id).subscribe((r) => this.rows.update((list) => [...list, ...r]));
  }
}
```

#### `src/Web/apps/admin/src/app/pages/inventory/receipt-form.component.ts`

```typescript
import { Component, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AdminInventoryApi, StockLocation, readProblem } from '@shoplab/api-client';

/** Nhập hàng hoặc kiểm kê (chọn chế độ). */
@Component({
  selector: 'app-receipt-form',
  imports: [ReactiveFormsModule],
  template: `
    <form class="card form" [formGroup]="form" (ngSubmit)="submit()">
      <h3>{{ form.value.mode === 'receipt' ? 'Nhập hàng' : 'Kiểm kê' }}</h3>
      <label>Loại
        <select formControlName="mode"><option value="receipt">Nhập hàng</option><option value="adjust">Kiểm kê (số đếm được)</option></select>
      </label>
      <label>SKU <input formControlName="sku" /></label>
      <label>Location
        <select formControlName="locationCode">
          @for (l of locations(); track l.id) { <option [value]="l.code">{{ l.code }}</option> }
        </select>
      </label>
      <label>{{ form.value.mode === 'receipt' ? 'Số lượng nhập' : 'Số đếm được' }} <input type="number" formControlName="qty" /></label>
      <label>Ghi chú <input formControlName="note" /></label>
      @if (message()) { <p [class.error]="isError()" [class.ok]="!isError()">{{ message() }}</p> }
      <button class="primary" [disabled]="form.invalid">Ghi</button>
    </form>
  `,
})
export class ReceiptFormComponent {
  private readonly api = inject(AdminInventoryApi);
  readonly locations = input<StockLocation[]>([]);
  readonly done = output<void>();
  protected readonly message = signal<string | null>(null);
  protected readonly isError = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    mode: ['receipt' as 'receipt' | 'adjust'],
    sku: ['', Validators.required],
    locationCode: ['WH-HCM', Validators.required],
    qty: [1, [Validators.required, Validators.min(0)]],
    note: [''],
  });

  submit(): void {
    const v = this.form.getRawValue();
    const req =
      v.mode === 'receipt'
        ? this.api.receive({ sku: v.sku, locationCode: v.locationCode, qty: v.qty, note: v.note })
        : this.api.adjust({ sku: v.sku, locationCode: v.locationCode, countedQty: v.qty, reason: v.note });
    req.subscribe({
      next: (r) => {
        this.isError.set(false);
        this.message.set('delta' in r ? `Đã điều chỉnh ${r.delta >= 0 ? '+' : ''}${r.delta}` : 'Đã nhập hàng');
        this.done.emit();
      },
      error: (e) => {
        this.isError.set(true);
        this.message.set(readProblem(e).title);
      },
    });
  }
}
```

#### `src/Web/apps/admin/src/app/pages/inventory/stock-list.component.ts`

```typescript
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminInventoryApi, StockLevel, StockLocation } from '@shoplab/api-client';
import { ReceiptFormComponent } from './receipt-form.component';
import { TransferFormComponent } from './transfer-form.component';

@Component({
  selector: 'app-stock-list',
  imports: [FormsModule, RouterLink, ReceiptFormComponent, TransferFormComponent],
  template: `
    <h1>Tồn kho</h1>
    <div class="row">
      <select [(ngModel)]="location" (change)="load()">
        <option value="">Mọi location</option>
        @for (l of locations(); track l.id) { <option [value]="l.code">{{ l.code }} — {{ l.name }}</option> }
      </select>
      <label class="row"><input type="checkbox" [(ngModel)]="lowOnly" (change)="load()" /> Chỉ tồn thấp (≤ {{ threshold }})</label>
    </div>
    <div class="layout">
      <table>
        <thead><tr><th>SKU</th><th>Location</th><th>Tồn</th><th>Đang giữ</th><th>Còn bán</th></tr></thead>
        <tbody>
          @for (s of rows(); track s.sku + s.locationId) {
            <tr [class.low]="s.available <= threshold">
              <td><a [routerLink]="['/inventory', s.sku]">{{ s.sku }}</a></td>
              <td>{{ s.locationCode }}</td>
              <td>{{ s.onHand }}</td>
              <td>{{ s.reserved }}</td>
              <td>{{ s.available }}</td>
            </tr>
          }
        </tbody>
      </table>
      <div class="forms">
        <app-receipt-form [locations]="locations()" (done)="load()" />
        <app-transfer-form [locations]="locations()" (done)="load()" />
      </div>
    </div>
  `,
  styles: `
    .layout { display: grid; grid-template-columns: 2fr 1fr; gap: 24px; align-items: start; margin-top: 12px; }
    .forms { display: grid; gap: 16px; }
    tr.low td { color: var(--danger); }
  `,
})
export class StockListComponent {
  private readonly api = inject(AdminInventoryApi);
  protected readonly locations = signal<StockLocation[]>([]);
  protected readonly rows = signal<StockLevel[]>([]);
  protected location = '';
  protected lowOnly = false;
  protected readonly threshold = 10;

  constructor() {
    this.api.locations().subscribe((l) => this.locations.set(l));
    this.load();
  }

  load(): void {
    this.api.stock({ location: this.location, lowThreshold: this.lowOnly ? this.threshold : null }).subscribe((r) => this.rows.set(r));
  }
}
```

#### `src/Web/apps/admin/src/app/pages/inventory/transfer-form.component.ts`

```typescript
import { Component, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AdminInventoryApi, StockLocation, readProblem } from '@shoplab/api-client';

@Component({
  selector: 'app-transfer-form',
  imports: [ReactiveFormsModule],
  template: `
    <form class="card form" [formGroup]="form" (ngSubmit)="submit()">
      <h3>Chuyển kho</h3>
      <label>SKU <input formControlName="sku" /></label>
      <div class="row">
        <label class="grow">Từ
          <select formControlName="fromCode">@for (l of locations(); track l.id) { <option [value]="l.code">{{ l.code }}</option> }</select>
        </label>
        <label class="grow">Đến
          <select formControlName="toCode">@for (l of locations(); track l.id) { <option [value]="l.code">{{ l.code }}</option> }</select>
        </label>
      </div>
      <label>Số lượng <input type="number" formControlName="qty" /></label>
      @if (message()) { <p [class.error]="isError()" [class.ok]="!isError()">{{ message() }}</p> }
      <button class="primary" [disabled]="form.invalid">Chuyển</button>
    </form>
  `,
})
export class TransferFormComponent {
  private readonly api = inject(AdminInventoryApi);
  readonly locations = input<StockLocation[]>([]);
  readonly done = output<void>();
  protected readonly message = signal<string | null>(null);
  protected readonly isError = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    sku: ['', Validators.required],
    fromCode: ['WH-HCM'],
    toCode: ['ST-Q1'],
    qty: [1, [Validators.required, Validators.min(1)]],
  });

  submit(): void {
    this.api.transfer(this.form.getRawValue()).subscribe({
      next: () => {
        this.isError.set(false);
        this.message.set('Đã chuyển kho');
        this.done.emit();
      },
      error: (e) => {
        this.isError.set(true);
        this.message.set(readProblem(e).title);
      },
    });
  }
}
```

#### `src/Web/apps/admin/src/app/pages/orders/order-list.component.ts`

```typescript
import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AdminOrderingApi, ORDER_STATUS_LABEL, Order, OrderSummary } from '@shoplab/api-client';
import { VndPipe } from '@shoplab/ui';

@Component({
  selector: 'app-order-list',
  imports: [FormsModule, DatePipe, VndPipe],
  template: `
    <h1>Đơn hàng</h1>
    <div class="row">
      <select [(ngModel)]="status" (change)="load(null)">
        <option value="">Mọi trạng thái</option>
        @for (s of statuses; track s) { <option [value]="s">{{ labels[s] }}</option> }
      </select>
      <select [(ngModel)]="channel" (change)="load(null)">
        <option value="">Mọi kênh</option><option>Online</option><option>Pos</option><option>FlashSale</option>
      </select>
    </div>
    <div class="layout">
      <table>
        <thead><tr><th>Mã</th><th>Ngày</th><th>Kênh</th><th>Tổng</th><th>Trạng thái</th></tr></thead>
        <tbody>
          @for (o of rows(); track o.id) {
            <tr (click)="open(o)" class="click">
              <td>{{ o.number }}</td>
              <td>{{ o.createdAt | date: 'dd/MM HH:mm' }}</td>
              <td>{{ o.channel }}</td>
              <td>{{ o.total | vnd }}</td>
              <td>{{ labels[o.status] }}</td>
            </tr>
          }
        </tbody>
      </table>
      @if (selected(); as o) {
        <div class="card">
          <h3>{{ o.number }}</h3>
          <p>{{ labels[o.status] }} @if (o.cancelReason) { — {{ o.cancelReason }} }</p>
          <ul>@for (l of o.lines; track l.sku) { <li>{{ l.qty }} × {{ l.name }} ({{ l.sku }})</li> }</ul>
          <p>Tổng {{ o.total | vnd }}</p>
        </div>
      }
    </div>
    @if (next()) { <button (click)="load(next())">Xem thêm</button> }
  `,
  styles: `
    .layout { display: grid; grid-template-columns: 2fr 1fr; gap: 24px; align-items: start; margin-top: 12px; }
    tr.click { cursor: pointer; }
  `,
})
export class OrderListComponent {
  private readonly api = inject(AdminOrderingApi);
  protected readonly labels = ORDER_STATUS_LABEL;
  protected readonly statuses = Object.keys(ORDER_STATUS_LABEL) as (keyof typeof ORDER_STATUS_LABEL)[];
  protected status = '';
  protected channel = '';
  protected readonly rows = signal<OrderSummary[]>([]);
  protected readonly next = signal<string | null>(null);
  protected readonly selected = signal<Order | null>(null);

  constructor() {
    this.load(null);
  }

  load(cursor: string | null): void {
    this.api.list({ status: this.status, channel: this.channel, cursor, size: 25 }).subscribe((page) => {
      this.rows.update((list) => (cursor ? [...list, ...page.items] : page.items));
      this.next.set(page.nextCursor);
    });
  }

  open(o: OrderSummary): void {
    this.api.get(o.id).subscribe((x) => this.selected.set(x));
  }
}
```

### Buổi 13 — k6 baseline + ghi chú lỗi

**Việc cần làm**

1. Profile LoadTest (`X-Test-User` thay token), nhập thêm hàng bằng `seed-loadtest.sh`.
2. Chạy `checkout-normal.js`, ghi p95/lỗi; bật chaos cho Payment/Inventory rồi chạy lại.
3. Điền `docs/notes/s2-failures.md`, `s2-concurrency.md`.

**Lệnh**

```bash
aspire run --project src/AppHost --launch-profile LoadTest
./tools/seed-loadtest.sh
k6 run load/checkout-normal.js
```

**File**

- `load/README.md`
- `load/checkout-normal.js`
- `tools/seed-loadtest.sh`
- `docs/notes/s2-concurrency.md`
- `docs/notes/s2-failures.md`
- `http/gate-s2.http`

#### `load/README.md`

````markdown
# Kịch bản k6

Mọi kịch bản chạy với AppHost ở profile LoadTest (header `X-Test-User` thay cho token Keycloak):

```bash
aspire run --project src/AppHost --launch-profile LoadTest
./tools/seed-loadtest.sh
k6 run -e BASE=http://localhost:5000 load/<file>.js
k6 run --summary-export docs/benchmarks/<tên>.json -e BASE=http://localhost:5000 load/<file>.js
```

| File | Bước | Đo gì |
| --- | --- | --- |
| checkout-normal.js | S2 | Baseline checkout đồng bộ |
| checkout-1000.js | S3 | 1.000 checkout, 10% lỗi thanh toán, saga |
| browse.js | S5 | Duyệt sản phẩm Postgres vs Mongo |
| flash-sale.js | S6 | 5.000 user giành 900 quota (MODE=naive hoặc redis) |
| flash-real-200.js | S6 | 200 user thật qua Keycloak + phòng chờ |

Trong lúc chạy, xem số kết nối DB:

```sql
SELECT datname, state, wait_event_type, wait_event, count(*)
FROM pg_stat_activity GROUP BY 1,2,3,4 ORDER BY 5 DESC;
```
````

#### `load/checkout-normal.js`

```javascript
// k6 — checkout bình thường: thêm 1 món vào giỏ rồi đặt hàng.
// Chạy khi AppHost ở profile LoadTest:  k6 run -e BASE=http://localhost:5000 load/checkout-normal.js
// S2: checkout đồng bộ trả 201. Từ S3 trả 202 rồi phải hỏi trạng thái (xem checkout-1000.js).
import http from 'k6/http';
import { check, sleep } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

export const options = {
  scenarios: {
    checkout: {
      executor: 'ramping-arrival-rate',
      startRate: 20,
      timeUnit: '1s',
      preAllocatedVUs: 100,
      maxVUs: 400,
      stages: [
        { target: 50, duration: '1m' },
        { target: 50, duration: '1m' },
        { target: 0, duration: '1m' },
      ],
    },
  },
  thresholds: {
    'http_req_duration{name:checkout}': ['p(95)<2000'],
    'http_req_failed{name:checkout}': ['rate<0.05'],
  },
};

const BASE = __ENV.BASE || 'http://localhost:5000';
const SKUS = ['SL-TEE-BASIC-M', 'SL-NOTE-A5', 'SL-MOUSE-WL', 'SL-BOTTLE-500-SLV'];

export default function () {
  const user = `loadtest-${__VU}-${__ITER}`;
  const headers = { 'Content-Type': 'application/json', 'X-Test-User': user };
  const sku = SKUS[Math.floor(Math.random() * SKUS.length)];

  const add = http.post(`${BASE}/api/cart/items`, JSON.stringify({ sku, qty: 1 }), { headers, tags: { name: 'add-to-cart' } });
  check(add, { 'thêm giỏ 200': (r) => r.status === 200 });

  const res = http.post(`${BASE}/api/ordering/checkout`, '{}', {
    headers: { ...headers, 'Idempotency-Key': uuidv4() },
    tags: { name: 'checkout' },
  });
  check(res, { 'checkout 201/202': (r) => r.status === 201 || r.status === 202 });
  sleep(0.1);
}
```

#### `tools/seed-loadtest.sh`

```bash
#!/usr/bin/env bash
# Nhập thêm hàng cho mọi SKU ở WH-HCM để k6 không hết hàng giữa chừng.
# Chạy khi AppHost đang chạy profile LoadTest (header X-Test-User có hiệu lực).
#   ./tools/seed-loadtest.sh            # mỗi SKU +100.000
set -euo pipefail
GATEWAY="${GATEWAY:-http://localhost:5000}"
QTY="${1:-100000}"
SKUS=(SL-BUDS-PRO-BLK SL-CHG-65W-WHT SL-MOUSE-WL SL-BOTTLE-500-SLV SL-BACKPACK-20L SL-TEE-BASIC-M SL-NOTE-A5)
for sku in "${SKUS[@]}"; do
  curl -sf -X POST "$GATEWAY/api/admin/inventory/stock/receipts" \
    -H "X-Test-User: loadtest-admin" -H "X-Test-Roles: admin" -H "Content-Type: application/json" \
    -d "{\"sku\":\"$sku\",\"locationCode\":\"WH-HCM\",\"qty\":$QTY,\"note\":\"loadtest\"}" > /dev/null
  echo "+$QTY $sku"
done
```

#### `docs/notes/s2-concurrency.md`

```markdown
# S2 — giữ hàng khi nhiều request cùng lúc

Kết quả từ `dotnet test tests/Inventory.Tests --filter "InventoryConcurrencyTests|ConditionalUpdateTests" --logger "console;verbosity=detailed"`.

| Cách | Thành công / 50 | Thời gian | Số lần retry | Ghi chú |
| --- | --- | --- | --- | --- |
| Đọc StockLevel + xmin + retry | | | | |
| UPDATE có điều kiện | | | 0 | |

Nhận xét: khi nào cách nào hợp hơn (độ phức tạp code, số round-trip, khả năng ghi movement trong cùng transaction).
```

#### `docs/notes/s2-failures.md`

````markdown
# S2 — ba chỗ gãy của checkout đồng bộ

Điền sau khi chạy (tuần 23). Cột "Sau S3" điền ở tuần 37.

| Kịch bản | Cách tái hiện | Hiện tượng | Dữ liệu lệch | Nguyên nhân gốc | S3 sửa bằng | Sau S3 |
| --- | --- | --- | --- | --- | --- | --- |
| 1. Inventory chết giữa chừng | `k6 run load/checkout-normal.js`, giữa chừng Stop resource `inventory` trên dashboard 30 giây | | | | Saga + event; Inventory xử lý khi sống lại | |
| 2. Payment trễ 30 giây | `"Chaos": { "DelayMs": 30000, "PathPrefix": "/internal/payments" }` trong Payment | | | | Bước thanh toán bất đồng bộ + hạn thanh toán + hoàn tiền | |
| 3. Gửi đôi | `./tools/double-submit.sh` | | | | `Idempotency-Key` + unique index | |

## SQL dùng để kiểm tra

```sql
-- Order kẹt
SELECT status, count(*) FROM ordering.orders GROUP BY status;
-- Hàng giữ của order đã huỷ (lẽ ra phải = 0)
SELECT r.* FROM inventory.reservations r WHERE r.status = 'Active';
-- Payment thành công của order đã huỷ (lẽ ra phải = 0)
-- (chạy riêng ở paymentdb rồi đối chiếu order_id với orderingdb)
SELECT order_id, status FROM payment.payments WHERE status = 'Succeeded';
```
````

#### `http/gate-s2.http`

```http
@gateway = http://localhost:5000
@token = paste-customer-token
@guest = 7d3f8a2e-0000-4000-8000-000000000001

### Giỏ vãng lai: thêm món
POST {{gateway}}/api/cart/items
X-Cart-Id: {{guest}}
Content-Type: application/json

{ "sku": "SL-TEE-BASIC-M", "qty": 2 }

### Giỏ vãng lai: xem
GET {{gateway}}/api/cart
X-Cart-Id: {{guest}}

### Đăng nhập rồi gộp giỏ vãng lai
POST {{gateway}}/api/cart/merge
Authorization: Bearer {{token}}
Content-Type: application/json

{ "guestCartId": "{{guest}}" }

### Giỏ của user
GET {{gateway}}/api/cart
Authorization: Bearer {{token}}

### Sửa số lượng (0 = xoá dòng)
PUT {{gateway}}/api/cart/items/SL-TEE-BASIC-M
Authorization: Bearer {{token}}
Content-Type: application/json

{ "qty": 3 }
@gateway = http://localhost:5000
@token = paste-customer-token

### 1. Thêm vào giỏ
POST {{gateway}}/api/cart/items
Authorization: Bearer {{token}}
Content-Type: application/json

{ "sku": "SL-MOUSE-WL", "qty": 1 }

### 2. Đặt hàng (S2: 201 + order Paid; từ S3: 202 + order PendingStock, có Idempotency-Key)
POST {{gateway}}/api/ordering/checkout
Authorization: Bearer {{token}}
Idempotency-Key: 6b1c2f2a-1111-4c4c-9999-000000000001
Content-Type: application/json

{ }

### 3. Đơn của tôi
GET {{gateway}}/api/ordering/orders
Authorization: Bearer {{token}}

### 4. Chi tiết (dán id)
GET {{gateway}}/api/ordering/orders/paste-order-id
Authorization: Bearer {{token}}
```

**Kiểm tra cuối buổi**

```bash
dotnet test
git add -A && git commit -m "s2: đặt hàng đồng bộ" && git tag s2-done
```

**Xong tuần 3 khi:**

- [ ] Mua hàng qua UI được
- [ ] Có số liệu k6 baseline
- [ ] Ghi chú lỗi đầy đủ, tag `s2-done`

---

## Gate S2

| # | Kiểm tra |
| --- | --- |
| 1 | Mua hàng end-to-end qua storefront |
| 2 | `InventoryConcurrencyTests`, `ConditionalUpdateTests`, `ReconcileTests` xanh |
| 3 | Có số liệu k6 và ghi chú 3 kịch bản gãy (`docs/notes/s2-failures.md`) |
| 4 | `SyncCheckoutFailureTests`: chỉ happy path xanh — đây là "nợ" S3 phải trả |
