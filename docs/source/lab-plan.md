# Lab: E-commerce microservices — implement plan v3

Oct 27, 2026 · @Khoa

**Thay đổi ở v3:** lộ trình đi từ một web bán hàng đơn giản, gọi nhau đồng bộ, lên dần bản đầy đủ; hạ tầng chỉ thêm vào khi bước đó cần, trừ năm thứ khó gắn về sau. Thêm một MongoDB replica set dùng chung làm read DB, chỉ Catalog, Ordering và Bff.Admin dùng. Bản v2 vẫn còn trong lịch sử của doc.

## Mục tiêu và phạm vi v1

V1 là một lab chạy hoàn toàn trên máy local: 11 service .NET, 3 gateway, 3 UI Angular, khởi động bằng một lệnh, và chịu được bài test flash sale 5.000 user giành 100 sản phẩm mà không oversell.

Mục tiêu học, theo thứ tự ưu tiên:

1. Tự tay dựng các pattern mà framework (ABP) thường che đi: Outbox, Saga, idempotency, service discovery, distributed tracing.
2. Hiểu chỗ hệ thống gãy dưới tải: hot row, lock contention, queue backlog, cache stampede.
3. Có một repo đủ thật để đem đi phỏng vấn hoặc demo.

Nguyên tắc làm lab:

- **Mỗi phase phải chạy được end-to-end** trước khi sang phase sau. Không viết 11 service rồi mới nối.
- **Không dùng ABP** trong lab này. Mục đích là thấy từng mảnh ghép, không phải dùng lại framework quen.
- **Đo trước, tối ưu sau.** Mỗi thay đổi kiến trúc cho flash sale đều đi kèm số liệu k6 trước/sau.
- Ngoài phạm vi v1: Kubernetes, CI/CD, multi-region, payment gateway thật, Elasticsearch. Ghi lại ở mục bài tập mở rộng.

## Stack công nghệ

Chọn .NET 10 (LTS) + Aspire 13.x làm xương sống, và tự viết lớp messaging (Outbox, Inbox, Saga) thay vì dùng MassTransit, vì [MassTransit v9 đã chuyển sang license thương mại](https://masstransit.io/introduction/v9-announcement) còn v8 chỉ được vá đến hết 2026.

| STT | Lớp | Lựa chọn | Lý do |
| --- | --- | --- | --- |
| 1 | Runtime | .NET 10, C# 14 | LTS hiện tại, Aspire 13.4 chạy trên SDK 10.0.x |
| 2 | Orchestration local | [Aspire 13.x](https://devblogs.microsoft.com/aspire/whats-new-aspire-13-5/) AppHost (C#) | Một lệnh chạy cả 20+ resource, có dashboard log/trace/metric sẵn, service discovery |
| 3 | API style | Minimal API + vertical slice, handler tự viết | Bỏ MediatR (v13 cũng đã thương mại hóa); thấy rõ pipeline |
| 4 | ORM | EF Core 10 + Npgsql | Quen tay; dùng `xmin` làm concurrency token cho Inventory |
| 5 | Database | PostgreSQL 17, 1 container, **mỗi service 1 database** | Đủ cô lập dữ liệu, nhẹ máy |
| 6 | Read DB | MongoDB 8, một replica set 3 node `rs0` dùng chung | Chỉ Catalog, Ordering, Bff.Admin; mỗi bên một database và user riêng, Postgres vẫn là nguồn sự thật |
| 7 | Cache / stock flash | Redis 7 | Cart, discount cache, Lua stock, rate limit |
| 8 | Message broker | RabbitMQ 4 + `RabbitMQ.Client` 7 | Tự viết Outbox/Inbox để hiểu cơ chế; topic exchange `ecommerce.events` |
| 9 | Gateway | YARP + ASP.NET Core RateLimiting | Cùng codebase .NET, dễ debug |
| 10 | Identity | Keycloak 26 (container) | OIDC thật, realm có sẵn role `customer`, `admin`, `cashier`, `store_manager` |
| 11 | Background job | Hangfire (PostgreSQL storage) | Hoàn hàng hết hạn, reconcile, đóng ca |
| 12 | Real-time | SignalR | Đẩy kết quả flash sale, dashboard admin |
| 13 | Email dev | Mailpit | Bắt email Notification service gửi ra |
| 14 | Frontend | Angular (bản stable mới nhất), Nx workspace: `storefront`, `admin`, `pos` | Đúng stack của bạn; POS là PWA + IndexedDB |
| 15 | Test | xUnit v3, Testcontainers, Shouldly, k6 | Integration test với Postgres/Redis/RabbitMQ thật; k6 cho flash sale |

Hai thư viện cố tình tránh: MediatR và FluentAssertions (cả hai đã có bản thương mại). Nếu sau này muốn so sánh, phase mở rộng có bài thay lớp messaging tự viết bằng Wolverine hoặc MassTransit v8.

## Cấu trúc solution

Một mono-repo `shoplab`, mỗi service là một project Web độc lập (không có Domain/Application/Infrastructure tách project), cắt theo feature folder. Chỉ có 2 project dùng chung: `BuildingBlocks` (messaging, outbox, auth) và `Contracts` (event DTO).

```text
shoplab/
├─ shoplab.slnx
├─ Directory.Build.props            # net10.0, nullable, warnings-as-errors
├─ Directory.Packages.props         # Central Package Management
├─ .editorconfig
├─ docker/
│  ├─ keycloak/shoplab-realm.json   # realm, client, role, user mẫu
│  ├─ postgres/init.sql             # tạo 1 DB cho mỗi service
│  └─ mongo/init-replset.js         # rs.initiate 3 node, tạo user theo từng DB
├─ seed/                            # data mẫu JSON (mục Data mẫu)
│  ├─ categories.json
│  ├─ products.json
│  ├─ locations.json
│  ├─ stock.json
│  ├─ customers.json
│  ├─ discounts.json
│  ├─ flash-campaigns.json
│  └─ stores.json
├─ src/
│  ├─ AppHost/
│  │  ├─ AppHost.csproj             # Aspire.AppHost.Sdk/13.x
│  │  └─ AppHost.cs                 # khai báo toàn bộ resource
│  ├─ ServiceDefaults/              # OTel, health check, resilience, service discovery
│  ├─ BuildingBlocks/
│  │  ├─ Messaging/
│  │  │  ├─ IntegrationEvent.cs     # EventId, OccurredAt, CorrelationId
│  │  │  ├─ OutboxMessage.cs        # entity + EF configuration
│  │  │  ├─ OutboxInterceptor.cs    # SaveChanges → ghi outbox cùng transaction
│  │  │  ├─ OutboxPublisher.cs      # BackgroundService đọc outbox → RabbitMQ
│  │  │  ├─ InboxMessage.cs         # chống xử lý trùng phía consumer
│  │  │  ├─ RabbitMqConsumerHost.cs # bind queue, retry, DLQ
│  │  │  └─ IEventHandler.cs
│  │  ├─ Persistence/Auditable.cs, AuditInterceptor.cs, SoftDeleteQueryFilter.cs
│  │  ├─ Idempotency/IdempotencyFilter.cs   # header Idempotency-Key, lưu Redis 24h
│  │  ├─ Auth/AuthExtensions.cs     # JWT Keycloak, policy theo role
│  │  └─ Results/ProblemDetailsMapping.cs
│  ├─ Contracts/                    # record event dùng chung, không có logic
│  │  ├─ Catalog/ProductPriceChanged.cs
│  │  ├─ Inventory/StockChanged.cs, StockReserved.cs, StockReservationFailed.cs
│  │  ├─ Orders/OrderCreated.cs, OrderPaid.cs, OrderCancelled.cs
│  │  ├─ Payments/PaymentSucceeded.cs, PaymentFailed.cs
│  │  └─ FlashSale/FlashReservationCreated.cs, FlashReservationReleased.cs
│  ├─ Gateways/
│  │  ├─ Gateway.Web/   (Program.cs, yarp.json, RateLimitPolicies.cs, FlashTokenValidator.cs)
│  │  ├─ Bff.Admin/     (Program.cs, yarp.json, Dashboard/GetKpis.cs, Projections/DailySales.cs, TopSkus.cs, LowStock.cs)
│  │  └─ Bff.Pos/       (Program.cs, Sync/PullDelta.cs, Sync/PushTransactions.cs)
│  ├─ Services/
│  │  ├─ Catalog.Api/
│  │  │  ├─ Program.cs
│  │  │  ├─ Data/CatalogDbContext.cs, Migrations/
│  │  │  ├─ Domain/Product.cs, ProductVariant.cs, Category.cs
│  │  │  ├─ Features/Products/CreateProduct.cs, UpdateProduct.cs, GetProduct.cs, SearchProducts.cs
│  │  │  ├─ Features/Categories/...
│  │  │  ├─ EventHandlers/StockChangedHandler.cs   # cập nhật cờ InStock
│  │  │  └─ ReadModel/ProductDocument.cs, ProductProjection.cs   # MongoDB catalog_read
│  │  ├─ Customer.Api/        (Domain/Customer.cs, Address.cs, LoyaltyAccount.cs)
│  │  ├─ Cart.Api/            (Redis only, Domain/Cart.cs, Features/AddItem.cs, ApplyCoupon.cs)
│  │  ├─ Inventory.Api/       (Domain/Location.cs, StockLevel.cs, StockMovement.cs, Reservation.cs)
│  │  ├─ Discount.Api/        (Domain/Promotion.cs, Coupon.cs, CouponRedemption.cs, Rules/)
│  │  ├─ Ordering.Api/        (Domain/Order.cs, OrderLine.cs, Sagas/CheckoutSaga.cs, ReadModel/OrderDocument.cs)
│  │  ├─ Payment.Api/         (Domain/Payment.cs, FakeGateway/FakePaymentProvider.cs)
│  │  ├─ Notification.Worker/ (Templates/*.liquid, Handlers/OrderPaidHandler.cs)
│  │  ├─ FlashSale.Api/       (Domain/Campaign.cs, CampaignItem.cs, Redis/reserve.lua, Workers/)
│  │  └─ Store.Api/           (Domain/Store.cs, Register.cs, Shift.cs, CashMovement.cs)
│  └─ Web/                          # Nx workspace
│     ├─ apps/storefront/  apps/admin/  apps/pos/
│     └─ libs/api-client/  libs/ui/  libs/auth/
├─ tests/
│  ├─ Inventory.Tests/  Ordering.Tests/  FlashSale.Tests/   # Testcontainers
│  └─ Architecture.Tests/           # NetArchTest: service không reference DbContext của service khác
└─ load/
   ├─ flash-sale.js                 # k6 scenario
   ├─ checkout-normal.js
   └─ README.md
```

Quy ước bên trong mỗi service:

- Một file trong `Features/` = một use case: chứa request, validator, handler và `MapEndpoint`. Đọc một file là hiểu một API.
- `Domain/` chứa entity có hành vi (vd `StockLevel.Reserve(qty)` ném lỗi khi thiếu hàng), không có anemic model.
- Mỗi DbContext có bảng `outbox_messages` và `inbox_messages` riêng, schema = tên service.

## Lệnh khởi tạo và chạy

Toàn bộ hệ thống chạy bằng `aspire run` từ thư mục gốc; Postgres, Redis, RabbitMQ, Keycloak, Mailpit đều là container do AppHost tự kéo lên. Máy cần khoảng 8 GB RAM trống cho Docker.

**1. Cài công cụ (một lần)**

```bash
# .NET 10 SDK, Docker Desktop/Podman, Node 22 LTS, k6 cài theo trang chủ của từng tool
dotnet tool install -g Aspire.Cli
dotnet tool install -g dotnet-ef
dotnet new install Aspire.ProjectTemplates
npm i -g nx
```

**2. Dựng khung solution**

```bash
mkdir shoplab && cd shoplab
git init
dotnet new sln -n shoplab            # .NET 10 sinh shoplab.slnx
dotnet new gitignore && dotnet new editorconfig

dotnet new aspire-apphost        -n AppHost         -o src/AppHost
dotnet new aspire-servicedefaults -n ServiceDefaults -o src/ServiceDefaults
dotnet new classlib -n BuildingBlocks -o src/BuildingBlocks
dotnet new classlib -n Contracts      -o src/Contracts

for s in Catalog Customer Cart Inventory Discount Ordering Payment FlashSale Store; do
  dotnet new web -n $s.Api -o src/Services/$s.Api
done
dotnet new worker -n Notification.Worker -o src/Services/Notification.Worker

for g in Gateway.Web Bff.Admin Bff.Pos; do
  dotnet new web -n $g -o src/Gateways/$g
done

dotnet sln add $(find src -name '*.csproj')
```

**3. Package chính (khai báo trong Directory.Packages.props)**

```bash
# AppHost
dotnet add src/AppHost package Aspire.Hosting.PostgreSQL
dotnet add src/AppHost package Aspire.Hosting.Redis
dotnet add src/AppHost package Aspire.Hosting.RabbitMQ
dotnet add src/AppHost package Aspire.Hosting.Keycloak
dotnet add src/AppHost package Aspire.Hosting.JavaScript

# Mỗi service có DB
dotnet add src/Services/Catalog.Api package Aspire.Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/Services/Catalog.Api package Microsoft.EntityFrameworkCore.Design

# BuildingBlocks
dotnet add src/BuildingBlocks package RabbitMQ.Client
dotnet add src/BuildingBlocks package Aspire.StackExchange.Redis
dotnet add src/BuildingBlocks package Microsoft.AspNetCore.Authentication.JwtBearer

# Gateway
dotnet add src/Gateways/Gateway.Web package Yarp.ReverseProxy

# Read model (chỉ Catalog, Ordering, Bff.Admin)
dotnet add src/Services/Catalog.Api package MongoDB.Driver
dotnet add src/Services/Ordering.Api package MongoDB.Driver
dotnet add src/Gateways/Bff.Admin package MongoDB.Driver
```

**4. AppHost.cs (rút gọn)**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var pg       = builder.AddPostgres("pg").WithPgAdmin().WithDataVolume();
var redis    = builder.AddRedis("redis").WithRedisInsight();
var rabbit   = builder.AddRabbitMQ("rabbit").WithManagementPlugin();
var keycloak = builder.AddKeycloak("keycloak", 8080)
                      .WithRealmImport("../../docker/keycloak");
var mailpit  = builder.AddContainer("mailpit", "axllent/mailpit")
                      .WithHttpEndpoint(8025, 8025, "ui")
                      .WithEndpoint(1025, 1025, name: "smtp");

var catalogDb   = pg.AddDatabase("catalogdb");
var inventoryDb = pg.AddDatabase("inventorydb");
var orderingDb  = pg.AddDatabase("orderingdb");
var flashsaleDb = pg.AddDatabase("flashsaledb");
// ... customerdb, discountdb, paymentdb, storedb

var inventory = builder.AddProject<Projects.Inventory_Api>("inventory")
    .WithReference(inventoryDb).WithReference(rabbit).WithReference(keycloak)
    .WaitFor(inventoryDb).WaitFor(rabbit);

var flashsale = builder.AddProject<Projects.FlashSale_Api>("flashsale")
    .WithReference(flashsaleDb)
    .WithReference(redis).WithReference(rabbit).WaitFor(redis);

// ... các service còn lại theo cùng mẫu

builder.AddProject<Projects.Gateway_Web>("gateway")
    .WithReference(inventory).WithReference(flashsale) /* + catalog, cart, ordering... */
    .WithReference(redis).WithExternalHttpEndpoints();

builder.Build().Run();
```

**MongoDB replica set (thêm ở S5).** Integration MongoDB của Aspire dựng một node; để có replica set 3 node, khai báo 3 container `mongo:8` chạy với `--replSet rs0` và một container chạy `docker/mongo/init-replset.js` một lần. Chỗ dễ vấp nhất: tên host khai báo trong `rs.initiate` phải phân giải được từ cả container lẫn các service chạy trên máy host, nếu không driver sẽ không tìm thấy primary. Connection string mẫu cho Catalog: `mongodb://catalog:Passw0rd!@localhost:27017,localhost:27018,localhost:27019/catalog_read?replicaSet=rs0`.

**5. Migration cho từng service**

```bash
dotnet ef migrations add Init -p src/Services/Inventory.Api -o Data/Migrations
# lặp lại cho Catalog, Customer, Discount, Ordering, Payment, FlashSale, Store
```

Ở môi trường Development, mỗi service tự `Database.MigrateAsync()` rồi chạy `SeedData.RunAsync()` (đọc `seed/*.json` khi bảng trống) lúc khởi động. Cart không có migration vì chỉ dùng Redis.

**6. Frontend**

```bash
cd src/Web
npx create-nx-workspace@latest . --preset=angular-monorepo --appName=storefront
nx g @nx/angular:application apps/admin
nx g @nx/angular:application apps/pos
nx g @angular/pwa --project=pos
nx g @nx/angular:library libs/api-client
```

**7. Chạy và kiểm tra**

```bash
aspire run                           # mở dashboard, mọi resource xanh là được
k6 run load/flash-sale.js            # sau khi đã có S6
dotnet test                          # Testcontainers tự dựng hạ tầng riêng
```

| STT | Địa chỉ local | Dùng để |
| --- | --- | --- |
| 1 | Aspire dashboard (link in ra khi `aspire run`) | Log, trace, metric của mọi service |
| 2 | `localhost:8080` | Keycloak admin (admin/admin) |
| 3 | `localhost:8025` | Mailpit, xem email gửi đi |
| 4 | RabbitMQ management (port do Aspire cấp) | Xem exchange, queue, DLQ |
| 5 | `localhost:27017–27019` | MongoDB replica set `rs0` (từ S5) |
| 6 | Gateway.Web | Điểm vào duy nhất cho storefront và k6 |

## Domain model

Mỗi service sở hữu đúng các bảng dưới đây; service khác chỉ biết `Sku`, `CustomerId`, `OrderId` dưới dạng giá trị, không có foreign key xuyên service. Tiền dùng `long` theo VND (không có phần lẻ), mọi thời gian là `DateTimeOffset` UTC.

**Audit và soft delete (dùng chung cho mọi service)**

```csharp
abstract Auditable(bool IsDeleted = false, bool IsActive = true, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)

// Model có ": Auditable" mang đủ 4 cột trên; mọi thời gian là DateTimeOffset UTC
```

- `AuditInterceptor` (SaveChangesInterceptor trong BuildingBlocks): entity mới thì gán `CreatedAt = UpdatedAt = TimeProvider.GetUtcNow()`; entity bị sửa thì cập nhật `UpdatedAt`; entity bị xoá thì đổi trạng thái thành Modified và đặt `IsDeleted = true`. Code nghiệp vụ không tự gán các cột này.
- Global query filter `HasQueryFilter(e => !e.IsDeleted)` cho mọi `Auditable`. Màn hình admin xem bản đã xoá bằng `IgnoreQueryFilters()`.
- Unique index phải có lọc, ví dụ `HasIndex(x => x.Sku).IsUnique().HasFilter("is_deleted = false")`. Không lọc thì xoá mềm một SKU rồi tạo lại sẽ bị lỗi trùng.
- `IsActive` khác `Status`: `IsActive` là công tắc bật/tắt của admin (ẩn sản phẩm, khoá quầy POS, tạm dừng coupon); `Status` là vòng đời nghiệp vụ (Draft/Active/Archived, PendingPayment/Paid…).
- Read model trên Mongo chỉ chứa bản `IsDeleted = false` và `IsActive = true`. Xoá mềm hoặc tắt `IsActive` cũng phát event như mọi thay đổi khác, để projection gỡ document khỏi Mongo.
- Bản ghi append-only (`StockMovement`, `CouponRedemption`, `CashMovement`, `PosTransaction`) và dòng con nằm trong aggregate (`OrderLine`) chỉ có `CreatedAt`, không có soft delete. Sổ cái chỉ được thêm bút toán đảo, không được sửa hay xoá.

**Catalog** (`catalogdb`)

```csharp
Category(Guid Id, string Name, string Slug, Guid? ParentId) : Auditable
Product(Guid Id, string Name, string Slug, string Description, Guid CategoryId, string Brand, ProductStatus Status, long Version) : Auditable
ProductVariant(Guid Id, Guid ProductId, string Sku, string Name, long Price, long? CompareAtPrice, string? Barcode, Dictionary<string, string> Attributes, bool InStock) : Auditable

// ProductStatus: Draft | Active | Archived
// Sku unique (lọc is_deleted = false) · Attributes lưu jsonb · InStock là projection từ StockChanged
// Version tăng mỗi lần ghi, dùng cho projection sang Mongo
```

**Customer** (`customerdb`)

```csharp
Customer(Guid Id, string KeycloakUserId, string Email, string? Phone, string FullName, CustomerStatus Status) : Auditable
Address(Guid Id, Guid CustomerId, string Line1, string Ward, string District, string City, bool IsDefault) : Auditable
LoyaltyAccount(Guid CustomerId, int Points, LoyaltyTier Tier) : Auditable

// CustomerStatus: Active | Banned · LoyaltyTier: Member | Silver | Gold
// Phone unique (lọc is_deleted = false)
```

**Cart** (Redis, không có DB)

```text
key  cart:{customerId}      JSON, TTL 48h, gia hạn mỗi lần sửa
{ customerId, couponCode?, updatedAt,
  lines: [ { sku, qty, unitPriceSnapshot, nameSnapshot } ] }
```

**Inventory** (`inventorydb`): trái tim của lab, viết kỹ nhất

```csharp
Location(Guid Id, string Code, string Name, LocationType Type, bool SellableOnline) : Auditable
StockLevel(string Sku, Guid LocationId, int OnHand, int Reserved, uint Version) : Auditable
StockMovement(long Id, string Sku, Guid LocationId, int Qty, MovementType Type, string RefType, string RefId, string By, DateTimeOffset CreatedAt)
Reservation(Guid Id, Guid OrderId, string Sku, Guid LocationId, int Qty, ReservationStatus Status, DateTimeOffset ExpiresAt) : Auditable

// LocationType: Warehouse | Store
// StockLevel: PK (Sku, LocationId) · Available = OnHand - Reserved · Version = xmin
//             hành vi: Reserve(qty), Release(qty), Commit(qty)
// StockMovement: append-only · Qty có dấu · unique (RefType, RefId, Sku, Type) để DB chặn ghi trùng
// MovementType: Receipt | Sale | Return | TransferOut | TransferIn | Adjustment | FlashAllocation | FlashReturn | PosSale
// ReservationStatus: Active | Committed | Released
```

Quy tắc: `StockLevel` chỉ được đổi cùng transaction với một `StockMovement` mới. Tổng `Qty` của movement theo (Sku, Location) luôn bằng `OnHand`; test reconcile kiểm tra đúng điều này.

**Discount** (`discountdb`)

```csharp
Promotion(Guid Id, string Name, PromoType Type, long Value, PromoScope Scope, string[] ScopeValues, SalesChannel Channel, long MinOrderAmount, long? MaxDiscount, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Stackable) : Auditable
Coupon(string Code, Guid PromotionId, int? UsageLimit, int PerCustomerLimit, int UsedCount) : Auditable
CouponRedemption(Guid Id, string Code, Guid CustomerId, Guid OrderId, DateTimeOffset CreatedAt)

// PromoType: PercentOff | AmountOff | BuyXGetY · PromoScope: All | Category | Sku
// SalesChannel: Online | Pos | All
// Coupon PK: Code · CouponRedemption append-only, unique (Code, OrderId)
```

**Ordering** (`orderingdb`)

```csharp
Order(Guid Id, string Number, Guid? CustomerId, SalesChannel Channel, Guid? StoreId, OrderStatus Status, long Subtotal, long DiscountTotal, long Total, string IdempotencyKey, long Version) : Auditable
OrderLine(Guid OrderId, string Sku, string Name, int Qty, long UnitPrice, long LineDiscount, DateTimeOffset CreatedAt)
CheckoutSagaState(Guid OrderId, SagaStep Step, Guid? PaymentId, string? LastError, DateTimeOffset PaymentDeadline, uint Version) : Auditable

// Number dạng SO-20261027-000123 · IdempotencyKey unique
// SalesChannel: Online | Pos | FlashSale
// OrderStatus: PendingStock → PendingPayment → Paid → Completed, hoặc → Cancelled
//              (hết hàng / hết hạn thanh toán / thanh toán lỗi)
// Order.Version tăng mỗi lần ghi, dùng cho projection sang Mongo
```

**Payment** (`paymentdb`)

```csharp
Payment(Guid Id, Guid OrderId, long Amount, PaymentMethod Method, PaymentStatus Status, string? ProviderRef) : Auditable

// OrderId unique · PaymentMethod: Fake | Cash | Card | VietQr
// PaymentStatus: Pending | Succeeded | Failed | Refunded
```

`FakePaymentProvider` cấu hình được tỉ lệ thất bại (vd 10%) và độ trễ (200–2.000 ms) để luyện nhánh bù trừ của saga.

**FlashSale** (`flashsaledb` + Redis)

```csharp
Campaign(Guid Id, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, CampaignStatus Status) : Auditable
CampaignItem(Guid CampaignId, string Sku, long FlashPrice, int Quota, int PerUserLimit, Guid SourceLocationId) : Auditable
FlashPurchase(Guid ReservationId, Guid CampaignId, string Sku, Guid CustomerId, int Qty, FlashPurchaseStatus Status, Guid? OrderId, DateTimeOffset ExpiresAt) : Auditable

// CampaignStatus: Draft | Scheduled | Live | Ended | Reconciled
// FlashPurchaseStatus: Reserved | OrderCreated | Paid | Released
// CampaignItem PK: (CampaignId, Sku) · FlashPurchase PK: ReservationId
// Campaign đã Live không cho xoá mềm hay tắt IsActive; muốn dừng thì chuyển Ended
```

```text
fs:{campId}:stock:{sku}     INT     số còn lại, nạp lúc Scheduled → Live
fs:{campId}:buyers:{sku}    HASH    customerId → số đã giữ
fs:{campId}:res:{resId}     HASH    sku, qty, customerId; TTL 10 phút
fs:{campId}:sold:{sku}      INT     đếm để dashboard admin
fs:admit:{campId}           ZSET    phòng chờ: customerId → thứ tự vào
```

**Store / POS** (`storedb`)

```csharp
Store(Guid Id, string Code, string Name, Guid LocationId) : Auditable
Register(Guid Id, Guid StoreId, string Name, string DeviceId) : Auditable
Shift(Guid Id, Guid RegisterId, string CashierId, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, long OpeningCash, long ExpectedCash, long? CountedCash) : Auditable
CashMovement(Guid Id, Guid ShiftId, CashMovementType Type, long Amount, string? RefId, DateTimeOffset CreatedAt)
PosTransaction(Guid ClientTxnId, Guid RegisterId, Guid ShiftId, DateTimeOffset OccurredAt, SyncStatus Status, string PayloadJson, DateTimeOffset CreatedAt)

// Store.LocationId trỏ sang Inventory.Location · PosTransaction PK: ClientTxnId, sinh trên máy POS
// CashMovement, PosTransaction append-only · OccurredAt = giờ trên máy POS, CreatedAt = giờ server nhận
// CashMovementType: Sale | Refund | PayIn | PayOut · SyncStatus: Received | Applied | Rejected
```

**Read model** (MongoDB, replica set `rs0`, chỉ 3 bên dùng)

```javascript
// catalog_read.products: 1 document / product, do Catalog cập nhật
{ _id: "sl-buds-pro", name: "Tai nghe SL Buds Pro", brand: "ShopLab",
  category: { slug: "dien-tu", name: "Điện tử" }, minPrice: 2490000,
  variants: [ { sku: "SL-BUDS-PRO-BLK", name: "Đen", price: 2490000,
                compareAtPrice: 2990000, inStock: true } ],
  searchText: "tai nghe sl buds pro den", version: 7, updatedAt: ISODate("...") }

// ordering_read.orders: 1 document / order, do Ordering cập nhật
{ _id: "<orderId>", number: "SO-20261027-000123", customerId: "<id>",
  channel: "Online", status: "Paid", total: 554000, createdAt: ISODate("..."),
  lines: [ { sku: "SL-MOUSE-WL", name: "Chuột không dây SL", qty: 1,
             unitPrice: 450000, lineDiscount: 45000 } ],
  timeline: [ { status: "PendingStock", at: ISODate("...") },
              { status: "Paid", at: ISODate("...") } ], version: 4 }

// admin_read.daily_sales | top_skus | low_stock: do Bff.Admin cập nhật
{ _id: "2026-10-27|Online", date: "2026-10-27", channel: "Online",
  orders: 42, revenue: 31250000 }
```

Index: `products` có `{ "category.slug": 1, minPrice: 1 }` và text index trên `searchText`; `orders` có `{ customerId: 1, createdAt: -1 }`. Projection phải idempotent: chỉ upsert khi `version` của event lớn hơn `version` trong document. Postgres vẫn là nguồn sự thật, nên xoá read model rồi rebuild phải ra kết quả giống hệt.

**Integration event**

| STT | Event | Phát bởi | Nghe bởi |
| --- | --- | --- | --- |
| 1 | `ProductPriceChanged` | Catalog | Cart (cập nhật snapshot), Bff.Pos (delta sync) |
| 2 | `StockChanged` | Inventory | Catalog (`InStock`), Bff.Admin (cảnh báo tồn thấp) |
| 3 | `OrderCreated` | Ordering | Inventory (reserve), Discount (redeem coupon) |
| 4 | `StockReserved` / `StockReservationFailed` | Inventory | Ordering (saga) |
| 5 | `PaymentRequested` | Ordering | Payment |
| 6 | `PaymentSucceeded` / `PaymentFailed` | Payment | Ordering (saga) |
| 7 | `OrderPaid` | Ordering | Inventory (commit), Notification, Customer (cộng điểm), FlashSale |
| 8 | `OrderCancelled` | Ordering | Inventory (release), Discount (hoàn lượt dùng), FlashSale (trả quota) |
| 9 | `FlashReservationCreated` | FlashSale | Ordering (tạo order kênh FlashSale) |
| 10 | `FlashReservationReleased` | FlashSale | Bff.Admin (dashboard) |
| 11 | `PosSaleRecorded` | Store | Ordering (order kênh Pos), Inventory (PosSale), Customer (điểm) |

Mọi event có `EventId`, `CorrelationId` (= OrderId hoặc ReservationId), `OccurredAt`, `Version`. Exchange: `ecommerce.events` (topic), routing key `<service>.<event>`, mỗi consumer một queue + một DLQ.

## Lộ trình theo phase

V3 đi từ một web bán hàng đơn giản, các service gọi nhau đồng bộ, lên dần bản đầy đủ qua 9 bước S0–S8. Mỗi bước thêm đúng một lớp hạ tầng hoặc một năng lực mới, và luôn để lại một hệ thống chạy được từ UI. Tổng 85–121 ngày công, tự code hoàn toàn. Làm ngoài giờ khoảng 11 giờ/tuần thì có **web đặt hàng được sau 4,5–6 tháng**, **flash sale sau 11–16 tháng**, **đủ tính năng sau 14–20,5 tháng**.

So với v2: web có giao diện ngay từ S1 thay vì đợi tới tháng 7,5–11, nhưng flash sale đến muộn hơn 5–7 tháng và tổng dài hơn khoảng 1,5–2,5 tháng. Phần dài thêm đến từ MongoDB (S5) và việc cố ý viết checkout đồng bộ ở S2 rồi chuyển sang event-driven ở S3. Lần viết lại này là có chủ đích: bạn thấy hệ thống đồng bộ gãy ở đâu trước khi dùng Outbox và Saga để sửa.

&#91;embedded content: 9 bước từ web đơn giản đến đầy đủ · dòng 1: hạ tầng thêm vào · dòng 2: gate\]

S2 là mốc có web đặt hàng được; S6 là trọng tâm của lab.

| STT | Bước | Ngày công | Ngoài giờ (11 giờ/tuần) | Cộng dồn ngoài giờ |
| --- | --- | --- | --- | --- |
| 1 | S0. Nền móng sớm | 5–7 | 4–5 tuần | 1 tháng |
| 2 | S1. Web catalog | 7–10 | 5–7 tuần | 2–3 tháng |
| 3 | S2. Đặt hàng đồng bộ | 14–19 | 10–14 tuần | **4,5–6 tháng** |
| 4 | S3. Chuyển sang event-driven | 13–18 | 9–13 tuần | 6,5–9 tháng |
| 5 | S4. Discount | 7–10 | 5–7 tuần | 7,5–11 tháng |
| 6 | S5. Read model MongoDB | 7–10 | 5–7 tuần | 9–12,5 tháng |
| 7 | S6. Flash sale | 14–20 | 10–15 tuần | **11–16 tháng** |
| 8 | S7. POS | 12–18 | 9–13 tuần | 13–19 tháng |
| 9 | S8. Hardening | 6–9 | 4–7 tuần | **14–20,5 tháng** |

### Hạ tầng dựng sớm và hạ tầng để sau

| STT | Thành phần | Bước | Lý do |
| --- | --- | --- | --- |
| 1 | AppHost + ServiceDefaults (OpenTelemetry) | S0 | Trace là công cụ debug chính khi có nhiều service; gắn sau thì phải sửa lại từng service |
| 2 | PostgreSQL + pipeline migration/seed | S0 | Mọi service có ghi dữ liệu đều dùng |
| 3 | Keycloak + Gateway.Web | S0 | Đổi mô hình auth về sau phải sửa mọi API lẫn UI; gateway giữ URL `/api/*` cố định để tách service sau không đụng frontend |
| 4 | Nx workspace, `libs/auth`, `libs/api-client` sinh từ OpenAPI | S0 | UI có từ S1; client sinh tự động nên API đổi thì frontend báo lỗi compile ngay |
| 5 | Testcontainers harness | S0 | Bước nào viết test ngay bước đó, không dồn về cuối |
| 6 | Redis, k6 | S2 | Lần đầu có giỏ hàng; lấy baseline của checkout đồng bộ |
| 7 | RabbitMQ, Hangfire, Mailpit | S3 | Lần đầu cần event, job hẹn giờ, email |
| 8 | MongoDB replica set `rs0` | S5 | Read model chỉ có ý nghĩa khi đã có event để chiếu sang |

### Các bước

**S0. Nền móng sớm (5–7 ngày)**

- [ ] AppHost với Postgres, Keycloak; ServiceDefaults với OpenTelemetry, `/health`, `/alive`, resilience handler
- [ ] Keycloak realm `shoplab`: 4 role, client `storefront`, `admin`, `pos`, `gateway`, user mẫu
- [ ] Gateway.Web: YARP + JWT, route `/api/*`
- [ ] Nx workspace với `apps/storefront`, `apps/admin` trống; `libs/auth` (OIDC), `libs/api-client` sinh từ OpenAPI
- [ ] Bộ test Testcontainers với 1 test mẫu chạy trong `dotnet test`
- [ ] Gate: đăng nhập storefront qua Keycloak được; `GET /api/ping` qua gateway có token được 200, không token được 401

**S1. Web catalog (7–10 ngày)**

- [ ] Catalog: CRUD product/variant/category, tìm theo tên + category, phân trang keyset; seed `categories.json`, `products.json`
- [ ] Storefront: danh sách và chi tiết sản phẩm
- [ ] Admin: quản lý sản phẩm, category
- [ ] Gate: admin tạo sản phẩm mới, storefront thấy ngay; user `customer` gọi API admin bị 403

**S2. Đặt hàng đồng bộ (14–19 ngày)**

- [ ] Customer: tạo hồ sơ khi đăng nhập lần đầu (từ claim Keycloak), địa chỉ, ban/unban
- [ ] Cart trên Redis: thêm/sửa/xoá dòng, snapshot giá
- [ ] Inventory với Location, StockLevel, StockMovement ngay từ đầu (đổi mô hình kho về sau rất tốn); nhập hàng, chuyển kho; seed `locations.json`, `stock.json`
- [ ] Ordering + Payment: `POST /checkout` gọi HTTP lần lượt Catalog (giá) → Inventory (reserve) → FakePayment → commit, tất cả trong một request; chưa có `Idempotency-Key`
- [ ] Storefront: giỏ hàng, checkout, "đơn của tôi"; Admin: khách hàng, tồn kho theo location
- [ ] k6 `checkout-normal.js`: ghi baseline p95 và tỉ lệ lỗi
- [ ] Gate: đặt hàng end-to-end từ UI được; chạy 3 kịch bản gãy và ghi lại hậu quả: (1) kill Inventory giữa checkout, (2) FakePayment trễ 30 giây, (3) client bấm "Đặt hàng" 2 lần

**S3. Chuyển sang event-driven (13–18 ngày)**

- [ ] `OutboxInterceptor`, `OutboxPublisher`, `RabbitMqConsumerHost` (retry, DLQ), `InboxMessage`
- [ ] `StockChanged` → Catalog cập nhật `InStock`
- [ ] Viết lại checkout thành saga trong Ordering, có bù trừ; Hangfire huỷ order quá `PaymentDeadline`; thêm `Idempotency-Key`
- [ ] Notification.Worker gửi email qua Mailpit; Customer cộng điểm khi `OrderPaid`
- [ ] Gate: 3 kịch bản gãy ở S2 giờ tự hồi phục; dừng RabbitMQ giữa lúc nhập 500 phiếu không mất event; 1.000 checkout với 10% lỗi thanh toán không lệch kho; so k6 với baseline S2

**S4. Discount (7–10 ngày)**

- [ ] Engine 3 loại khuyến mãi, coupon, `Stackable`, `MaxDiscount`, `MinOrderAmount`, `Channel`; Redis cache rule
- [ ] Nối vào saga: `OrderCreated` → redeem coupon, `OrderCancelled` → hoàn lượt dùng
- [ ] Storefront: nhập coupon ở giỏ hàng; Admin: quản lý promotion và coupon
- [ ] Gate: 12 test case tính giá đúng từng đồng; checkout ở S3 vẫn xanh

**S5. Read model MongoDB (7–10 ngày)**

- [ ] Replica set `rs0` 3 node dùng chung; mỗi bên một database và user riêng: `catalog_read`, `ordering_read`, `admin_read`
- [ ] Catalog: `ProductDocument` cập nhật từ event; API storefront chuyển sang đọc Mongo, giữ nguyên contract
- [ ] Ordering: `OrderDocument` cho "đơn của tôi" (đọc từ primary) và danh sách đơn của admin (đọc từ secondary)
- [ ] Bff.Admin: projection doanh thu theo ngày, top SKU, tồn thấp; dashboard hiển thị projection lag
- [ ] Endpoint rebuild cho từng projection
- [ ] Gate: xoá `catalog_read`, rebuild, dữ liệu khớp 100% với Postgres; đo lại p95 trang danh sách so với S4

**S6. Flash sale (14–20 ngày)**

- [ ] Bản naive trên Postgres trước, đo k6 và ghi số liệu
- [ ] FlashSale.Api: campaign, item, chuyển trạng thái bằng Hangfire; cấp quota từ Inventory sang Redis
- [ ] Phòng chờ + flash token HMAC; `reserve.lua`, `OrderWorker`, `ExpiryWorker` + `release.lua`, `ReconcileJob`
- [ ] Storefront: trang flash sale có đếm ngược, phòng chờ, kết quả qua SignalR; Admin: tạo campaign, dashboard realtime
- [ ] Bảng so sánh naive và Redis, kèm giải thích vì sao khác nhau
- [ ] Gate: kịch bản flash sale ở mục dưới đạt toàn bộ tiêu chí và cả 4 bài diễn tập sự cố

**S7. POS (12–18 ngày)**

- [ ] Store.Api: store, register, mở/đóng ca, tiền mặt
- [ ] Bff.Pos: `GET /sync/pull?since=`, `POST /sync/push` (batch giao dịch)
- [ ] Angular `pos`: PWA, IndexedDB, quét mã vạch bằng bàn phím, in hoá đơn qua `window.print`
- [ ] `PosSaleRecorded` → Ordering + Inventory (trừ đúng location cửa hàng) + Customer (điểm)
- [ ] Gate: tắt mạng, bán 20 giao dịch, bật mạng: server nhận đủ 20 order kênh Pos, đẩy lại lần 2 không tạo trùng, chênh lệch tiền khi đóng ca hiển thị đúng

**S8. Hardening (6–9 ngày)**

- [ ] Architecture tests: không service nào reference service khác; chỉ Catalog, Ordering, Bff.Admin có connection string Mongo, mỗi bên chỉ trỏ vào database của mình
- [ ] Dashboard: độ dài queue, outbox lag, projection lag, p95 checkout
- [ ] Diễn tập failover Mongo: kill primary khi storefront đang tải và Ordering đang ghi projection; đo thời gian bầu lại, xem driver tự kết nối lại
- [ ] Chaos thủ công: kill từng service giữa checkout và flash sale
- [ ] Gate: postmortem 1 trang cho mỗi lần kill; hệ thống tự hồi phục không cần sửa DB tay

## Scenario flash sale

Kịch bản chuẩn: 5.000 user ảo giành 3 SKU với tổng quota 900 trong 2 phút; đạt khi bán ra đúng bằng quota, không oversell, không user nào vượt giới hạn, và trang sản phẩm thường vẫn giữ p95 dưới 300 ms trong lúc sale.

**Campaign `LAB-1111`**

| STT | SKU | Tên | Giá gốc (VND) | Giá flash (VND) | Quota | Giới hạn/user | Lấy từ kho |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `SL-BUDS-PRO-BLK` | Tai nghe SL Buds Pro, đen | 2.490.000 | 990.000 | 100 | 1 | `WH-HCM` |
| 2 | `SL-BOTTLE-500-SLV` | Bình giữ nhiệt SL 500 ml, bạc | 390.000 | 149.000 | 500 | 2 | `WH-HCM` |
| 3 | `SL-CHG-65W-WHT` | Sạc nhanh SL 65W, trắng | 690.000 | 299.000 | 300 | 3 | `WH-HN` |

**Mốc thời gian (T = giờ mở bán)**

1. T − 10 phút: Hangfire chuyển campaign `Scheduled`; trang flash sale được render tĩnh, đếm ngược theo `GET /flash/time` (giờ server).
2. T − 1 phút: FlashSale gọi Inventory ghi movement `FlashAllocation` (−quota tại kho nguồn), rồi `SET fs:{camp}:stock:{sku} <quota>`. Nếu kho không đủ, campaign không được Live và admin nhận cảnh báo.
3. T − 1 phút → T: user vào phòng chờ `POST /flash/{camp}/queue`, nhận vị trí (`ZADD NX`).
4. Từ T: mỗi giây `AdmissionWorker` cho 200 user đầu hàng đợi qua, cấp flash token HMAC (customerId, campId, exp 120 s) qua SignalR.
5. User gọi `POST /flash/{camp}/buy` kèm token + `Idempotency-Key`. Gateway kiểm token, rate limit 5 req/s/user.
6. FlashSale chạy `reserve.lua`. Trả `202 {reservationId}` hoặc `409 SOLD_OUT` / `409 LIMIT_REACHED` ngay, không chạm Postgres.
7. `OrderWorker` đọc `FlashReservationCreated` (prefetch 20), tạo Order kênh `FlashSale` với `IdempotencyKey = reservationId`, trạng thái `PendingPayment`, hạn 10 phút. SignalR báo client chuyển sang trang thanh toán.
8. Thanh toán thành công → `OrderPaid` → FlashSale đánh dấu `Paid`, Inventory `không trừ thêm, vì hàng đã rời kho nguồn ở bước allocation`.
9. Mỗi 5 giây `ExpiryWorker` quét `ZRANGEBYSCORE fs:{camp}:expiry -inf now`, chạy `release.lua` trả quota về Redis, phát `OrderCancelled`.
10. Hết campaign: `ReconcileJob` so `quota − stock Redis` với số FlashPurchase `Paid`, ghi `FlashReturn` trả phần dư về kho nguồn, chuyển `Reconciled`.

**`reserve.lua`**

```lua
-- KEYS: 1 stock, 2 buyers, 3 res:{resId}, 4 expiry zset
-- ARGV: 1 customerId, 2 qty, 3 limit, 4 resId, 5 sku, 6 expireAtUnix, 7 ttlSeconds
local bought = tonumber(redis.call('HGET', KEYS[2], ARGV[1]) or '0')
if bought + tonumber(ARGV[2]) > tonumber(ARGV[3]) then return {-2, 0} end
local stock = tonumber(redis.call('GET', KEYS[1]) or '0')
if stock < tonumber(ARGV[2]) then return {-1, 0} end
redis.call('DECRBY', KEYS[1], ARGV[2])
redis.call('HINCRBY', KEYS[2], ARGV[1], ARGV[2])
redis.call('HSET', KEYS[3], 'customerId', ARGV[1], 'sku', ARGV[5], 'qty', ARGV[2])
redis.call('EXPIRE', KEYS[3], tonumber(ARGV[7]) + 300)
redis.call('ZADD', KEYS[4], ARGV[6], ARGV[4])
return {1, stock - tonumber(ARGV[2])}
```

`release.lua` làm ngược lại và **chỉ chạy nếu `res:{resId}` còn tồn tại và chưa có cờ `paid`**, sau đó `DEL` key đó; nhờ vậy job hết hạn và sự kiện thanh toán đến cùng lúc không thể hoàn hàng hai lần. Mọi key dùng hash tag `{camp}` để chạy được trên Redis Cluster. Bật AOF `appendfsync everysec` cho Redis.

**Hai phiên bản để so sánh (phần quan trọng nhất của lab)**

1. **Naive**: `UPDATE stock_levels SET reserved = reserved + @q WHERE sku=@s AND location_id=@l AND on_hand - reserved >= @q` trực tiếp trong Postgres, order tạo đồng bộ. Ghi lại p95, lỗi, số kết nối DB, có oversell hay không.
2. **Redis + queue** như trên. Chạy cùng kịch bản, so bảng số liệu. Viết 1 đoạn giải thích vì sao khác nhau (hot row, lock wait, connection pool).

**k6 (`load/flash-sale.js`, rút gọn)**

```javascript
import http from 'k6/http';
import { check } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

export const options = {
  scenarios: {
    buyers:  { executor: 'ramping-arrival-rate', startRate: 0, timeUnit: '1s',
               preAllocatedVUs: 2000, maxVUs: 5000,
               stages: [{ target: 3000, duration: '10s' }, { target: 3000, duration: '50s' },
                        { target: 0, duration: '60s' }], exec: 'buy' },
    browsers:{ executor: 'constant-arrival-rate', rate: 50, timeUnit: '1s', duration: '2m',
               preAllocatedVUs: 50, exec: 'browse' },
  },
  thresholds: {
    'http_req_duration{scenario:browsers}': ['p(95)<300'],
    'http_req_duration{name:buy}':          ['p(95)<150'],
    'http_req_failed{name:buy}':            ['rate<0.01'],   // 409 không tính là failed
  },
};
const BASE = __ENV.BASE, CAMP = 'LAB-1111';
const SKUS = ['SL-BUDS-PRO-BLK', 'SL-BOTTLE-500-SLV', 'SL-CHG-65W-WHT'];
http.setResponseCallback(http.expectedStatuses(202, 409));

export function buy() {
  const user = `loadtest-${Math.floor(Math.random() * 5000)}`;
  const sku  = SKUS[Math.floor(Math.random() * SKUS.length)];
  const res = http.post(`${BASE}/flash/${CAMP}/buy`, JSON.stringify({ sku, qty: 1 }), {
    headers: { 'Content-Type': 'application/json', 'X-Test-User': user,
               'Idempotency-Key': uuidv4() }, tags: { name: 'buy' } });
  check(res, { '202 or 409': r => r.status === 202 || r.status === 409 });
}
export function browse() { http.get(`${BASE}/api/catalog/products?page=1`); }
```

Header `X-Test-User` chỉ được gateway chấp nhận khi `ASPNETCORE_ENVIRONMENT=LoadTest`; nó thay cho việc cấp 5.000 token Keycloak và bỏ qua phòng chờ. Chạy riêng một bài nhỏ (200 user thật qua Keycloak + phòng chờ) để kiểm luồng token.

**Tiêu chí đạt**

| STT | Tiêu chí | Ngưỡng | Đo bằng |
| --- | --- | --- | --- |
| 1 | Oversell | 0 | `SUM(qty) FlashPurchase status≠Released` ≤ quota từng SKU |
| 2 | Vượt giới hạn/user | 0 user | `GROUP BY customer_id, sku HAVING SUM(qty) > limit` |
| 3 | Reservation thành order | 100% trong 30 s | `Reserved` không có OrderId sau 30 s = 0 |
| 4 | p95 `/buy` | < 150 ms | k6 |
| 5 | p95 trang catalog trong lúc sale | < 300 ms | k6 scenario `browsers` |
| 6 | Hoàn hàng hết hạn | 100% sau 10 phút + 10 s | stock Redis + Paid = quota |
| 7 | Lệch kho sau reconcile | 0 | tổng movement = OnHand ở `WH-HCM`, `WH-HN` |

**Bài diễn tập sự cố (chạy lại kịch bản với từng lỗi)**

- Kill `OrderWorker` 20 giây ở giữa: message dồn queue, sau khi bật lại không mất reservation nào.
- Lỗi thanh toán 30%: quota được trả lại và bán tiếp cho người khác trong cùng campaign.
- Restart Redis lúc giây thứ 30: ghi lại mất gì với AOF everysec, và reconcile có bắt được lệch không.
- Gửi lại cùng `Idempotency-Key` 3 lần: chỉ một reservation.

## Data mẫu

Bộ seed nhỏ, có chủ đích: 7 SKU, 4 location, 5 promotion/coupon, đủ để chạy cả 12 test case tính giá và kịch bản flash sale. Muốn đo hiệu năng tìm kiếm thì bật `Seed:Bulk=true` để sinh thêm 2.000 sản phẩm và 5.000 khách hàng bằng thư viện Bogus.

**Sản phẩm (`seed/products.json`)**

| STT | SKU | Tên | Category | Giá (VND) |
| --- | --- | --- | --- | --- |
| 1 | `SL-BUDS-PRO-BLK` | Tai nghe SL Buds Pro, đen | `dien-tu` | 2.490.000 |
| 2 | `SL-CHG-65W-WHT` | Sạc nhanh SL 65W, trắng | `dien-tu` | 690.000 |
| 3 | `SL-MOUSE-WL` | Chuột không dây SL | `dien-tu` | 450.000 |
| 4 | `SL-BOTTLE-500-SLV` | Bình giữ nhiệt SL 500 ml, bạc | `gia-dung` | 390.000 |
| 5 | `SL-BACKPACK-20L` | Balo SL 20L | `thoi-trang` | 890.000 |
| 6 | `SL-TEE-BASIC-M` | Áo thun SL Basic, size M | `thoi-trang` | 199.000 |
| 7 | `SL-NOTE-A5` | Sổ tay SL A5 | `van-phong-pham` | 49.000 |

```json
{
  "slug": "sl-buds-pro",
  "name": "Tai nghe SL Buds Pro",
  "category": "dien-tu",
  "brand": "ShopLab",
  "status": "Active",
  "description": "Tai nghe true wireless, chống ồn chủ động, pin 30 giờ.",
  "variants": [
    { "sku": "SL-BUDS-PRO-BLK", "name": "Đen", "price": 2490000,
      "compareAtPrice": 2990000, "barcode": "8930000000011",
      "attributes": { "color": "black" } }
  ]
}
```

**Location (`seed/locations.json`)**

| STT | Code | Tên | Loại | Bán online |
| --- | --- | --- | --- | --- |
| 1 | `WH-HCM` | Kho tổng TP.HCM | Warehouse | có |
| 2 | `WH-HN` | Kho tổng Hà Nội | Warehouse | có |
| 3 | `ST-Q1` | SL Store Quận 1 | Store | không |
| 4 | `ST-CG` | SL Store Cầu Giấy | Store | không |

**Tồn kho ban đầu (`seed/stock.json`, sinh thành movement `Receipt` với `RefType=Seed`)**

```csv
sku,WH-HCM,WH-HN,ST-Q1,ST-CG
SL-BUDS-PRO-BLK,250,120,15,10
SL-CHG-65W-WHT,80,400,12,8
SL-MOUSE-WL,200,150,20,15
SL-BOTTLE-500-SLV,800,300,40,30
SL-BACKPACK-20L,150,100,10,8
SL-TEE-BASIC-M,500,300,60,50
SL-NOTE-A5,1000,800,200,150
```

Đủ cho campaign `LAB-1111`: Buds lấy 100/250 ở `WH-HCM`, bình lấy 500/800 ở `WH-HCM`, sạc lấy 300/400 ở `WH-HN`.

**Khách hàng và tài khoản Keycloak**

| STT | Username | Role | Ghi chú |
| --- | --- | --- | --- |
| 1 | `admin@shoplab.local` | admin | Toàn quyền admin portal |
| 2 | `manager.q1` | store\_manager | Cửa hàng `ST-Q1`, đóng ca, hoàn tiền |
| 3 | `cashier.q1` | cashier | Quầy `Q1-R1` |
| 4 | `an.nguyen` | customer | Khách thường, SĐT 0901000001, Gold, 1.200 điểm |
| 5 | `binh.tran` | customer | Đã dùng `WELCOME50K` một lần (có CouponRedemption) |
| 6 | `chi.le` | customer | Trạng thái `Banned`, mọi API mua hàng trả 403 |
| 7 | `loadtest-0` … `loadtest-4999` | customer | Không tạo trong Keycloak; chỉ dùng qua header `X-Test-User` ở môi trường LoadTest |

Mật khẩu dev chung: `Passw0rd!`, nằm trong `shoplab-realm.json`, không dùng ở đâu khác.

**Cửa hàng (`seed/stores.json`)**: `ST-Q1` có 2 quầy `Q1-R1`, `Q1-R2`; `ST-CG` có 1 quầy `CG-R1`. Tiền đầu ca mặc định 2.000.000.

**Khuyến mãi (`seed/discounts.json`)**

| STT | Mã | Loại | Giá trị | Phạm vi | Kênh | Điều kiện | Cộng dồn |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `AUTO-DT10` (tự động) | PercentOff | 10% | category `dien-tu` | All | tối đa 200.000 / dòng | có |
| 2 | `AUTO-B2G1` (tự động) | BuyXGetY | mua 2 tặng 1 | `SL-BOTTLE-500-SLV` | All | mỗi nhóm 3 cái, tặng 1 | có |
| 3 | `AUTO-Q1OPEN` (tự động) | AmountOff | 100.000 | All | Pos | đơn từ 500.000 | có |
| 4 | `WELCOME50K` (coupon) | AmountOff | 50.000 | All | Online | đơn từ 300.000; 1 lần/khách | có |
| 5 | `SALE15` (coupon) | PercentOff | 15% | All | Online | đơn từ 1.000.000; tối đa 300.000 | không |

Quy tắc engine: điều kiện "đơn từ" xét trên subtotal gốc. Coupon cộng dồn được áp sau khuyến mãi tự động. Coupon không cộng dồn thì engine so (tổng khuyến mãi tự động) với (coupon đứng một mình) và chọn bên giảm nhiều hơn.

**12 test case tính giá (gate S4)**

| STT | Kênh, khách | Giỏ | Coupon | Giảm giá áp dụng | Tổng (VND) |
| --- | --- | --- | --- | --- | --- |
| 1 | Online, an.nguyen | 1 Tee | không | không có | 199.000 |
| 2 | Online, an.nguyen | 1 Buds | không | DT10: 249.000 → chặn 200.000 | 2.290.000 |
| 3 | Online, an.nguyen | 1 Sạc 65W | không | DT10: 69.000 | 621.000 |
| 4 | Online, an.nguyen | 1 Chuột + 1 Tee | WELCOME50K | DT10 45.000 + coupon 50.000 | 554.000 |
| 5 | Online, an.nguyen | 1 Tee | WELCOME50K | coupon bị từ chối `MIN_ORDER_NOT_MET` | 199.000 |
| 6 | Online, an.nguyen | 3 Bình | không | B2G1: tặng 1 = 390.000 | 780.000 |
| 7 | Online, an.nguyen | 2 Bình | không | chưa đủ nhóm 3, không giảm | 780.000 |
| 8 | Online, an.nguyen | 6 Bình | không | B2G1: tặng 2 = 780.000 | 1.560.000 |
| 9 | Online, an.nguyen | 1 Buds + 1 Balo | SALE15 | auto 200.000 < coupon 507.000 → chặn 300.000; chọn coupon | 3.080.000 |
| 10 | Online, an.nguyen | 1 Sạc 65W + 1 Tee | SALE15 | coupon từ chối `MIN_ORDER_NOT_MET`; DT10 69.000 | 820.000 |
| 11 | Pos `ST-Q1`, khách vãng lai | 1 Balo | không | Q1OPEN: 100.000 | 790.000 |
| 12 | Online, binh.tran | 1 Balo | WELCOME50K | coupon từ chối `PER_CUSTOMER_LIMIT` | 890.000 |

## Kiểm thử và bài tập mở rộng

Mỗi service có ít nhất một bộ integration test chạy trên Testcontainers; unit test chỉ dành cho domain có logic (StockLevel, Discount engine, saga transition).

**Test bắt buộc trước khi đóng v1**

- [ ] `StockLevelTests`: Reserve vượt Available ném lỗi; Release không làm Reserved âm
- [ ] `InventoryConcurrencyTests`: 50 task cùng reserve 1 SKU còn 10 → đúng 10 thành công (bản Postgres naive)
- [ ] `OutboxTests`: rollback transaction thì không có message; publisher chết giữa chừng thì gửi lại, consumer Inbox bỏ bản trùng
- [ ] `DiscountEngineTests`: 12 case ở mục Data mẫu, viết dạng `[Theory]` đọc từ JSON
- [ ] `CheckoutSagaTests`: các nhánh StockFailed, PaymentFailed, PaymentTimeout đều về `Cancelled` và hoàn kho
- [ ] `FlashReserveLuaTests`: chạy `reserve.lua` 1.000 lần song song trên Redis container, quota 100 → đúng 100 thành công, không user vượt limit
- [ ] `PosSyncTests`: push cùng batch 2 lần → số order không đổi
- [ ] `ArchitectureTests`: service không reference nhau, `Contracts` không reference gì ngoài BCL
- [ ] `ProjectionTests`: phát lại cùng một event 2 lần, hoặc event đến sai thứ tự, thì document Mongo vẫn đúng; rebuild từ đầu ra kết quả giống projection chạy realtime
- [ ] `AuditTests`: tạo mới có `CreatedAt = UpdatedAt`; sửa chỉ đổi `UpdatedAt`; `Remove()` thành xoá mềm và query thường không thấy; xoá mềm SKU rồi tạo lại cùng SKU không lỗi trùng; ghi `StockMovement` lần hai bị chặn sửa/xoá

**Bài tập mở rộng (sau v1)**

1. Thay lớp messaging tự viết bằng Wolverine hoặc MassTransit v8, so số dòng code và hành vi khi lỗi.
2. Chuyển Catalog search sang Elasticsearch hoặc PostgreSQL full-text + `unaccent` cho tiếng Việt.
3. Saga dạng choreography thay cho orchestration, viết lại luồng checkout và so độ khó debug.
4. Deploy lên Kubernetes bằng `aspire publish` (Docker Compose hoặc Kubernetes target), thêm HPA cho FlashSale.
5. Tích hợp VietQR / cổng thanh toán sandbox thật, xử lý webhook idempotent.
6. Event sourcing đầy đủ cho Inventory (StockLevel chỉ còn là projection rebuild được).
7. Multi-tenant (nhiều thương hiệu trên cùng hệ thống), tận dụng kinh nghiệm multi-tenant sẵn có.
