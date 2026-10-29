# ShopLab — S3: Event-driven: outbox, saga, idempotency

Mục tiêu của S3: trả "nợ" của S2. Checkout chỉ làm phần chắc chắn làm được ngay — ghi `Order(PendingStock)` + trạng thái saga +
event `OrderCreated` **trong một transaction** — rồi trả **202**. Giữ hàng, thanh toán, gửi mail, cộng điểm, xoá giỏ đi qua RabbitMQ.

Tự viết outbox/inbox (không dùng CAP/MassTransit) để hiểu cơ chế:

- **Outbox**: event là một dòng trong `outbox_messages`, cùng transaction với dữ liệu; publisher nền gửi đi (`FOR UPDATE SKIP LOCKED`).
- **Inbox**: consumer ghi `(event_id, consumer)` cùng transaction với thay đổi của nó → giao lại lần hai thì bỏ qua.
- **Retry/DLQ**: lỗi → `<queue>.retry` (TTL) → quay lại; lỗi không tạm thời quá 3 lần → `<queue>.dlq`.
- **Saga orchestration** ở Ordering + job Hangfire huỷ đơn quá hạn + hoàn tiền khi thanh toán tới muộn.
- **Idempotency-Key** bắt buộc khi checkout (Redis filter + unique index).

**Thời lượng:** 3 tuần · 12 buổi (mỗi buổi ~2–3 giờ ngoài giờ làm). **File:** 47 tạo mới, 35 sửa, 1 xoá.

## Lịch

| Tuần | Mục tiêu | Buổi |
| --- | --- | --- |
| Tuần 1 | Hạ tầng messaging tự viết: contract, outbox, publisher, consumer, retry/DLQ | Buổi 1: Contracts + EventTypeRegistry<br>Buổi 2: Outbox + publisher RabbitMQ<br>Buổi 3: Consumer host, inbox, retry/DLQ, đăng ký messaging<br>Buổi 4: Test messaging |
| Tuần 2 | Saga checkout: Inventory giữ hàng qua event, Payment qua event, Ordering điều phối | Buổi 5: Catalog phát ProductChanged, nghe StockChanged<br>Buổi 6: Inventory theo event<br>Buổi 7: Saga: state, chuyển bước, handler<br>Buổi 8: Checkout 202 + Idempotency-Key + job hết hạn + Payment<br>Buổi 9: Test saga + 3 kịch bản gãy giờ xanh |
| Tuần 3 | Hệ quả của OrderPaid, UI async, đo và phá thử | Buổi 10: Cart, Customer, Notification nghe OrderPaid + AppHost<br>Buổi 11: Storefront checkout async<br>Buổi 12: Đo tải và phá thử |

## Trước khi bắt đầu

```bash
git checkout s2-done -b s3
```

---

## Tuần 1 — Hạ tầng messaging tự viết: contract, outbox, publisher, consumer, retry/DLQ

**Mục tiêu tuần**

- Event có `EventId`, `CorrelationId`, `Version`; tên event = routing key trên exchange topic `ecommerce.events`.
- Outbox ghi cùng transaction; publisher gửi có publisher confirm, lỗi thì backoff, nhiều instance không gửi trùng.
- Consumer host: inbox EF hoặc Redis, retry queue có TTL, DLQ, trace OTel nối từ producer sang consumer.
- Test BuildingBlocks chạy trên Postgres + RabbitMQ thật (Testcontainers).

### Buổi 1 — Contracts + EventTypeRegistry

**Việc cần làm**

1. `IntegrationEvent` (record) + `[EventName("service.event")]`.
2. Event của Catalog (ProductChanged), Inventory (StockChanged, StockReserved, StockReservationFailed), Orders (OrderCreated, OrderPaid — fat event, OrderCancelled, OrderStatusChanged), Payments.
3. `EventTypeRegistry`: tên ↔ kiểu, serialize/deserialize JSON (Web defaults).

**File**

- `src/Contracts/Catalog/CatalogEvents.cs`
- `src/Contracts/IntegrationEvent.cs`
- `src/Contracts/Inventory/InventoryEvents.cs`
- `src/Contracts/Orders/OrderEvents.cs`
- `src/Contracts/Payments/PaymentEvents.cs`
- `src/BuildingBlocks/Messaging/EventTypeRegistry.cs`

#### `src/Contracts/Catalog/CatalogEvents.cs`

```csharp
namespace Contracts.Catalog;

/// <summary>Sản phẩm vừa được ghi (mọi thay đổi). Mang Version để projection bỏ qua event cũ.</summary>
[EventName("catalog.product_changed")]
public sealed record ProductChanged(Guid ProductId, long ProductVersion) : IntegrationEvent;
```

#### `src/Contracts/IntegrationEvent.cs`

```csharp
namespace Contracts;

/// <summary>
/// Base của mọi event đi qua RabbitMQ.
/// - EventId: Inbox phía consumer dùng để bỏ bản trùng.
/// - CorrelationId: OrderId / ReservationId / ClientTxnId, để lần theo một luồng qua nhiều service.
/// - Version: version của contract (tăng khi đổi cấu trúc theo cách không tương thích).
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public Guid CorrelationId { get; init; }
    public int Version { get; init; } = 1;
}

/// <summary>Tên event = routing key trên exchange topic ecommerce.events, dạng "&lt;service&gt;.&lt;event&gt;".</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
```

#### `src/Contracts/Inventory/InventoryEvents.cs`

```csharp
namespace Contracts.Inventory;

/// <summary>Tồn của một SKU vừa đổi. AvailableOnline = tổng còn bán ở các kho bán online.</summary>
[EventName("inventory.stock_changed")]
public sealed record StockChanged(string Sku, int AvailableOnline, int OnHandTotal) : IntegrationEvent;

[EventName("inventory.stock_reserved")]
public sealed record StockReserved(Guid OrderId) : IntegrationEvent;

[EventName("inventory.stock_reservation_failed")]
public sealed record StockReservationFailed(Guid OrderId, string Reason) : IntegrationEvent;
```

#### `src/Contracts/Orders/OrderEvents.cs`

```csharp
namespace Contracts.Orders;

public sealed record OrderLineItem(string Sku, string Name, int Qty, long UnitPrice, long LineDiscount);

/// <summary>
/// RequiresStock = false khi hàng đã nằm ngoài kho chung (flash sale đã cấp quota, POS đã giao tận tay).
/// </summary>
[EventName("orders.order_created")]
public sealed record OrderCreated(
    Guid OrderId, string Number, Guid? CustomerId, string Channel, long Total, string? CouponCode,
    IReadOnlyList<OrderLineItem> Lines, bool RequiresStock) : IntegrationEvent;

/// <summary>Fat event: mang đủ dữ liệu cho Notification, Customer (điểm) và Bff.Admin (doanh thu).</summary>
[EventName("orders.order_paid")]
public sealed record OrderPaid(
    Guid OrderId, string Number, Guid? CustomerId, string? CustomerEmail, string Channel, Guid? StoreId,
    long Total, long DiscountTotal, IReadOnlyList<OrderLineItem> Lines, DateTimeOffset PaidAt) : IntegrationEvent;

[EventName("orders.order_cancelled")]
public sealed record OrderCancelled(Guid OrderId, Guid? CustomerId, string Channel, string Reason, string? CouponCode) : IntegrationEvent;

/// <summary>Mỗi lần trạng thái đổi; projection "đơn của tôi" trên Mongo nghe event này (S5).</summary>
[EventName("orders.order_status_changed")]
public sealed record OrderStatusChanged(Guid OrderId, string Status, long OrderVersion) : IntegrationEvent;
```

#### `src/Contracts/Payments/PaymentEvents.cs`

```csharp
namespace Contracts.Payments;

[EventName("payments.payment_requested")]
public sealed record PaymentRequested(Guid OrderId, long Amount) : IntegrationEvent;

[EventName("payments.payment_succeeded")]
public sealed record PaymentSucceeded(Guid OrderId, Guid PaymentId, long Amount) : IntegrationEvent;

[EventName("payments.payment_failed")]
public sealed record PaymentFailed(Guid OrderId, string Reason) : IntegrationEvent;

/// <summary>Ordering yêu cầu hoàn tiền: thanh toán thành công đến sau khi đơn đã bị huỷ.</summary>
[EventName("payments.refund_requested")]
public sealed record PaymentRefundRequested(Guid OrderId, string Reason) : IntegrationEvent;
```

#### `src/BuildingBlocks/Messaging/EventTypeRegistry.cs`

```csharp
using System.Reflection;
using System.Text.Json;
using Contracts;

namespace BuildingBlocks.Messaging;

/// <summary>Bảng tra hai chiều tên event ↔ kiểu C#, quét từ assembly Contracts lúc khởi động.</summary>
public sealed class EventTypeRegistry
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = new();

    public EventTypeRegistry() : this(typeof(IntegrationEvent).Assembly) { }

    public EventTypeRegistry(params Assembly[] assemblies)
    {
        foreach (var type in assemblies.SelectMany(a => a.GetTypes())
                     .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(IntegrationEvent).IsAssignableFrom(t)))
        {
            var name = type.GetCustomAttribute<EventNameAttribute>()?.Name
                       ?? throw new InvalidOperationException($"{type.Name} thiếu [EventName].");
            _byName[name] = type;
            _byType[type] = name;
        }
    }

    public string NameOf(Type type) =>
        _byType.TryGetValue(type, out var name) ? name : throw new InvalidOperationException($"{type.Name} không phải event đã đăng ký.");

    public string NameOf<T>() where T : IntegrationEvent => NameOf(typeof(T));

    public Type TypeOf(string name) =>
        _byName.TryGetValue(name, out var type) ? type : throw new InvalidOperationException($"Không biết event '{name}'.");

    public string Serialize(IntegrationEvent evt) => JsonSerializer.Serialize(evt, evt.GetType(), Json);

    public IntegrationEvent Deserialize(string name, ReadOnlySpan<byte> body) =>
        (IntegrationEvent)JsonSerializer.Deserialize(body, TypeOf(name), Json)!;
}
```

**Kiểm tra cuối buổi**

```bash
dotnet build src/Contracts src/BuildingBlocks
```

### Buổi 2 — Outbox + publisher RabbitMQ

**Việc cần làm**

1. `OutboxMessage`/`InboxMessage` + `AddOutboxInbox()` cho OnModelCreating (partial index các dòng chưa gửi).
2. `IOutbox.Enqueue(evt)` → `EfOutbox<T>` thêm entity vào DbContext đang dùng — không SaveChanges riêng.
3. `RabbitMqPublisher`: channel pool, publisher confirms, header traceparent. `RabbitMqTopology`: exchange topic durable.
4. `OutboxPublisher<T>`: vòng 500 ms, 50 dòng `FOR UPDATE SKIP LOCKED`, lỗi → Attempts++ + NextAttemptAt; `OutboxCleanup<T>` xoá bản cũ > 7 ngày.
5. `MessagingTelemetry`: ActivitySource/Meter "ShopLab" (published, consumed, duplicates, dead-lettered).

**File**

- `src/BuildingBlocks/BuildingBlocks.csproj` *(sửa)*
- `src/BuildingBlocks/Messaging/OutboxMessage.cs`
- `src/BuildingBlocks/Messaging/Outbox.cs`
- `src/BuildingBlocks/Messaging/RabbitMqPublisher.cs`
- `src/BuildingBlocks/Messaging/OutboxPublisher.cs`
- `src/BuildingBlocks/Messaging/MessagingTelemetry.cs`

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
    <PackageReference Include="Aspire.RabbitMQ.Client" />
    <PackageReference Include="RabbitMQ.Client" />
    <PackageReference Include="Aspire.StackExchange.Redis" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\ServiceDefaults\ServiceDefaults.csproj" />
    <ProjectReference Include="..\Contracts\Contracts.csproj" />
  </ItemGroup>
</Project>
```

#### `src/BuildingBlocks/Messaging/OutboxMessage.cs`

```csharp
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Messaging;

/// <summary>
/// Event chờ gửi. Được ghi trong CÙNG transaction với dữ liệu nghiệp vụ, nên hoặc cả hai cùng có,
/// hoặc cả hai cùng không — không bao giờ "đã lưu đơn nhưng chưa ai biết" hay ngược lại.
/// </summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string Type { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public Guid CorrelationId { get; set; }
    public string? TraceParent { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Event đã xử lý bởi một consumer. Khoá (EventId, Consumer): giao lại lần hai thì bỏ qua.</summary>
public sealed class InboxMessage
{
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = default!;
    public DateTimeOffset ProcessedAt { get; set; }
}

public static class OutboxModelBuilderExtensions
{
    /// <summary>Gọi trong OnModelCreating của service có gửi hoặc nhận event.</summary>
    public static ModelBuilder AddOutboxInbox(this ModelBuilder b)
    {
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.TraceParent).HasMaxLength(100);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.EventId).IsUnique();
            // Publisher chỉ quét bản chưa gửi: partial index giữ cho truy vấn nhanh khi bảng lớn
            e.HasIndex(x => x.Id).HasFilter("processed_at IS NULL").HasDatabaseName("ix_outbox_pending");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => new { x.EventId, x.Consumer });
            e.Property(x => x.Consumer).HasMaxLength(200);
        });
        return b;
    }
}
```

#### `src/BuildingBlocks/Messaging/Outbox.cs`

```csharp
using System.Diagnostics;
using Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Messaging;

public interface IOutbox
{
    /// <summary>
    /// Thêm event vào DbContext hiện tại. Event chỉ thật sự tồn tại khi SaveChanges thành công,
    /// và cùng transaction với các thay đổi khác của request/handler.
    /// </summary>
    void Enqueue(IntegrationEvent evt);
}

public sealed class EfOutbox<TContext>(TContext db, EventTypeRegistry registry) : IOutbox where TContext : DbContext
{
    public void Enqueue(IntegrationEvent evt)
    {
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            EventId = evt.EventId,
            Type = registry.NameOf(evt.GetType()),
            Payload = registry.Serialize(evt),
            CorrelationId = evt.CorrelationId,
            OccurredAt = evt.OccurredAt,
            // giữ trace của request gốc để Aspire dashboard nối được span qua RabbitMQ
            TraceParent = Activity.Current?.Id
        });
    }
}
```

#### `src/BuildingBlocks/Messaging/RabbitMqPublisher.cs`

```csharp
using System.Diagnostics;
using System.Text;
using Contracts;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace BuildingBlocks.Messaging;

public static class RabbitMqTopology
{
    /// <summary>Một exchange topic cho toàn hệ thống; routing key = tên event.</summary>
    public const string Exchange = "ecommerce.events";

    public static Task DeclareExchangeAsync(IChannel channel, CancellationToken ct) =>
        channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
}

public interface IEventPublisher
{
    /// <summary>Gửi thẳng lên RabbitMQ, đợi broker xác nhận (publisher confirm). Ném lỗi nếu bị nack.</summary>
    Task PublishAsync(IntegrationEvent evt, CancellationToken ct = default);

    /// <summary>Gửi một message đã serialize sẵn (dùng bởi OutboxPublisher).</summary>
    Task PublishRawAsync(string type, string payload, Guid eventId, Guid correlationId, string? traceParent, CancellationToken ct = default);
}

/// <summary>
/// Giữ một nhóm channel có bật publisher confirm; mỗi channel chỉ một luồng dùng tại một thời điểm.
/// Nhiều channel để endpoint /buy của flash sale publish song song mà không xếp hàng sau một channel.
/// </summary>
public sealed class RabbitMqPublisher(IConnection connection, EventTypeRegistry registry, ILogger<RabbitMqPublisher> logger)
    : IEventPublisher, IAsyncDisposable
{
    private const int PoolSize = 8;
    private readonly SemaphoreSlim _init = new(1, 1);
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, PoolSize).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private IChannel?[] _channels = new IChannel?[PoolSize];
    private int _next;

    public Task PublishAsync(IntegrationEvent evt, CancellationToken ct = default) =>
        PublishRawAsync(registry.NameOf(evt.GetType()), registry.Serialize(evt), evt.EventId, evt.CorrelationId, Activity.Current?.Id, ct);

    public async Task PublishRawAsync(string type, string payload, Guid eventId, Guid correlationId, string? traceParent, CancellationToken ct = default)
    {
        var slot = (int)((uint)Interlocked.Increment(ref _next) % PoolSize);
        await _locks[slot].WaitAsync(ct);
        try
        {
            var channel = await GetChannelAsync(slot, ct);
            using var activity = MessagingTelemetry.Source.StartActivity($"publish {type}", ActivityKind.Producer, traceParent);
            var props = new BasicProperties
            {
                MessageId = eventId.ToString(),
                CorrelationId = correlationId.ToString(),
                Type = type,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Headers = new Dictionary<string, object?> { ["traceparent"] = activity?.Id ?? traceParent }
            };
            // Với publisherConfirmationTrackingEnabled, lệnh này chỉ hoàn tất khi broker ack; nack → PublishException
            await channel.BasicPublishAsync(RabbitMqTopology.Exchange, type, mandatory: false, props, Encoding.UTF8.GetBytes(payload), ct);
            MessagingTelemetry.Published.Add(1, new KeyValuePair<string, object?>("type", type));
        }
        catch
        {
            // channel có thể đã hỏng: bỏ đi để lần sau tạo lại
            _channels[slot] = null;
            throw;
        }
        finally
        {
            _locks[slot].Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(int slot, CancellationToken ct)
    {
        var existing = _channels[slot];
        if (existing is { IsOpen: true }) return existing;

        await _init.WaitAsync(ct);
        try
        {
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                ct);
            await RabbitMqTopology.DeclareExchangeAsync(channel, ct);
            _channels[slot] = channel;
            logger.LogDebug("Mở channel publish #{Slot}", slot);
            return channel;
        }
        finally
        {
            _init.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _channels)
            if (c is not null) await c.DisposeAsync();
        _channels = [];
    }
}
```

#### `src/BuildingBlocks/Messaging/OutboxPublisher.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Messaging;

/// <summary>
/// Vòng lặp 500 ms: lấy tối đa 50 dòng outbox chưa gửi bằng FOR UPDATE SKIP LOCKED
/// (nhiều instance chạy song song không lấy trùng dòng), publish có confirm, đánh dấu đã gửi.
/// Gửi lỗi → tăng Attempts và hẹn lần sau (backoff), không chặn các dòng khác.
/// Chết giữa batch → transaction rollback, dòng vẫn "chưa gửi" và được gửi lại: consumer phải chịu được bản trùng (Inbox).
/// </summary>
public sealed class OutboxPublisher<TContext>(
    IServiceScopeFactory scopes,
    IEventPublisher publisher,
    TimeProvider clock,
    ILogger<OutboxPublisher<TContext>> logger) : BackgroundService where TContext : DbContext
{
    public const int BatchSize = 50;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            int sent;
            try
            {
                sent = await PublishBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Vòng gửi outbox lỗi, thử lại sau");
                sent = 0;
            }

            // Còn nhiều thì chạy tiếp ngay, hết thì nghỉ
            if (sent < BatchSize)
                await Task.Delay(Interval, clock, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    public async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var table = db.Model.FindEntityType(typeof(OutboxMessage))!.GetSchemaQualifiedTableName();
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.Set<OutboxMessage>()
            .FromSqlRaw($"""
                SELECT * FROM {table}
                WHERE processed_at IS NULL AND (next_attempt_at IS NULL OR next_attempt_at <= {{0}})
                ORDER BY id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """, now)
            .ToListAsync(ct);

        foreach (var msg in batch)
        {
            try
            {
                await publisher.PublishRawAsync(msg.Type, msg.Payload, msg.EventId, msg.CorrelationId, msg.TraceParent, ct);
                msg.ProcessedAt = clock.GetUtcNow();
                msg.LastError = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                msg.Attempts++;
                msg.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                msg.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(60, Math.Pow(2, msg.Attempts)));
                logger.LogWarning("Không gửi được {Type} {EventId} (lần {Attempts}): {Error}", msg.Type, msg.EventId, msg.Attempts, ex.Message);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await UpdateMetricsAsync(db, now, ct);
        return batch.Count(m => m.ProcessedAt is not null);
    }

    private static async Task UpdateMetricsAsync(TContext db, DateTimeOffset now, CancellationToken ct)
    {
        var pending = db.Set<OutboxMessage>().AsNoTracking().Where(m => m.ProcessedAt == null);
        Interlocked.Exchange(ref MessagingTelemetry.OutboxPending, await pending.LongCountAsync(ct));
        var oldest = await pending.OrderBy(m => m.Id).Select(m => (DateTimeOffset?)m.OccurredAt).FirstOrDefaultAsync(ct);
        MessagingTelemetry.OutboxLagSeconds = oldest is null ? 0 : (now - oldest.Value).TotalSeconds;
    }
}

/// <summary>Xoá dòng outbox/inbox đã xử lý quá 7 ngày, mỗi giờ một lần.</summary>
public sealed class OutboxCleanup<TContext>(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OutboxCleanup<TContext>> logger)
    : BackgroundService where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();
                var cutoff = clock.GetUtcNow().AddDays(-7);
                var outbox = await db.Set<OutboxMessage>().Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                var inbox = await db.Set<InboxMessage>().Where(m => m.ProcessedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                if (outbox + inbox > 0) logger.LogInformation("Dọn {Outbox} outbox, {Inbox} inbox", outbox, inbox);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Dọn outbox lỗi");
            }
            await Task.Delay(TimeSpan.FromHours(1), clock, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
```

#### `src/BuildingBlocks/Messaging/MessagingTelemetry.cs`

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BuildingBlocks.Messaging;

/// <summary>Trace + metric của lớp messaging; ServiceDefaults đã đăng ký source/meter tên "ShopLab".</summary>
public static class MessagingTelemetry
{
    public static readonly ActivitySource Source = new("ShopLab");
    public static readonly Meter Meter = new("ShopLab");

    public static readonly Counter<long> Published = Meter.CreateCounter<long>("messaging.published");
    public static readonly Counter<long> Consumed = Meter.CreateCounter<long>("messaging.consumed");
    public static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("messaging.duplicates");
    public static readonly Counter<long> Retried = Meter.CreateCounter<long>("messaging.retried");
    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("messaging.dead_lettered");

    /// <summary>Số dòng outbox chưa gửi và tuổi của dòng cũ nhất, publisher cập nhật mỗi vòng quét.</summary>
    public static long OutboxPending;
    public static double OutboxLagSeconds;

    static MessagingTelemetry()
    {
        Meter.CreateObservableGauge("outbox.pending", () => Volatile.Read(ref OutboxPending));
        Meter.CreateObservableGauge("outbox.lag_seconds", () => OutboxLagSeconds);
    }
}
```

### Buổi 3 — Consumer host, inbox, retry/DLQ, đăng ký messaging

**Việc cần làm**

1. `IEventHandler<T>`, `ITransientErrorClassifier` (timeout, Npgsql transient, xung đột version → tạm thời).
2. `RabbitMqConsumerHost`: mỗi consumer một queue `<service>.<handler>` + `.retry` + `.dlq`; inbox chạy handler **lười** (chỉ sau khi chắc chưa xử lý).
3. `AddShopLabMessaging("svc").AddOutbox<Db>().AddConsumer<TEvent,THandler>(InboxKind…)`; `Messaging:Disabled=true` cho test API.
4. AppHost: `builder.AddRabbitMQ("rabbit").WithDataVolume("shoplab-rabbit").WithManagementPlugin()` và `.WithReference(rabbit).WaitFor(rabbit)` cho các service (AppHost đầy đủ ở Buổi 10).

**File**

- `src/BuildingBlocks/Messaging/IEventHandler.cs`
- `src/BuildingBlocks/Messaging/RabbitMqConsumerHost.cs`
- `src/BuildingBlocks/Messaging/MessagingExtensions.cs`

#### `src/BuildingBlocks/Messaging/IEventHandler.cs`

```csharp
using Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BuildingBlocks.Messaging;

/// <summary>
/// Xử lý một loại event. Handler không gọi SaveChanges khi đăng ký với Inbox EF:
/// consumer host tự SaveChanges một lần, cùng transaction với dòng inbox và các event handler enqueue vào outbox.
/// </summary>
public interface IEventHandler<in TEvent> where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent evt, CancellationToken ct);
}

/// <summary>Lỗi tạm thời (mất kết nối, đang bầu primary, xung đột xmin) → retry không giới hạn số lần, không vào DLQ.</summary>
public interface ITransientErrorClassifier
{
    bool IsTransient(Exception ex);
}

public sealed class DefaultTransientErrorClassifier(IEnumerable<Func<Exception, bool>> extra) : ITransientErrorClassifier
{
    public bool IsTransient(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException => true,
        NpgsqlException { IsTransient: true } => true,
        TimeoutException => true,
        HttpRequestException => true,
        Results.DependencyException => true,
        _ when ex.InnerException is not null && IsTransient(ex.InnerException) => true,
        _ => extra.Any(f => f(ex))
    };
}

/// <summary>Cách chống xử lý trùng cho một consumer.</summary>
public enum InboxKind
{
    /// <summary>Bảng inbox_messages trong DB của service, cùng transaction với handler. An toàn nhất.</summary>
    EntityFramework,
    /// <summary>Khoá SETNX trên Redis cho service không có Postgres (Cart, Notification).</summary>
    Redis,
    /// <summary>Không chống trùng: handler tự idempotent (vd ghi đè theo version).</summary>
    None
}

public sealed record ConsumerRegistration(
    Type EventType,
    Type HandlerType,
    string EventName,
    string QueueName,
    InboxKind Inbox,
    Type? DbContextType,
    ushort Prefetch);
```

#### `src/BuildingBlocks/Messaging/RabbitMqConsumerHost.cs`

```csharp
using System.Diagnostics;
using System.Text;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;

namespace BuildingBlocks.Messaging;

/// <summary>
/// Mỗi consumer một queue bền "&lt;service&gt;.&lt;handler&gt;" bind vào exchange theo tên event, kèm:
///   - &lt;queue&gt;.retry: message hết TTL thì quay lại queue chính (dead-letter về default exchange)
///   - &lt;queue&gt;.dlq: message lỗi quá 3 lần (lỗi không tạm thời) nằm lại để người xem
/// Ack thủ công sau khi xử lý xong; consumer chết giữa chừng thì broker giao lại.
/// </summary>
public sealed class RabbitMqConsumerHost(
    IConnection connection,
    IEnumerable<ConsumerRegistration> registrations,
    IServiceScopeFactory scopes,
    EventTypeRegistry registry,
    ITransientErrorClassifier classifier,
    TimeProvider clock,
    ILogger<RabbitMqConsumerHost> logger) : BackgroundService
{
    public const int MaxAttempts = 3;
    private const string RetryHeader = "x-retry-count";
    private readonly List<IChannel> _channels = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var reg in registrations)
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
            _channels.Add(channel);
            await DeclareAsync(channel, reg, stoppingToken);
            await channel.BasicQosAsync(0, reg.Prefetch, false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => OnMessageAsync(channel, reg, delivery, stoppingToken);
            await channel.BasicConsumeAsync(reg.QueueName, autoAck: false, consumer, stoppingToken);
            logger.LogInformation("Đang nghe {Event} trên queue {Queue}", reg.EventName, reg.QueueName);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
    }

    public static async Task DeclareAsync(IChannel channel, ConsumerRegistration reg, CancellationToken ct)
    {
        await RabbitMqTopology.DeclareExchangeAsync(channel, ct);
        await channel.QueueDeclareAsync(reg.QueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(reg.QueueName, RabbitMqTopology.Exchange, reg.EventName, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{reg.QueueName}.retry", durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = reg.QueueName
            }, cancellationToken: ct);
        await channel.QueueDeclareAsync($"{reg.QueueName}.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
    }

    private async Task OnMessageAsync(IChannel channel, ConsumerRegistration reg, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var headers = delivery.BasicProperties.Headers;
        var parent = headers?.TryGetValue("traceparent", out var tp) == true && tp is byte[] tpBytes ? Encoding.UTF8.GetString(tpBytes) : null;
        using var activity = MessagingTelemetry.Source.StartActivity($"consume {reg.EventName}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.destination", reg.QueueName);

        try
        {
            var evt = registry.Deserialize(reg.EventName, delivery.Body.Span);
            await DispatchAsync(reg, evt, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            MessagingTelemetry.Consumed.Add(1, new KeyValuePair<string, object?>("queue", reg.QueueName));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await RetryOrDeadLetterAsync(channel, reg, delivery, ex, ct);
        }
    }

    /// <summary>Gọi handler trong một scope DI mới, bọc bởi inbox theo cấu hình của consumer.</summary>
    public async Task DispatchAsync(ConsumerRegistration reg, IntegrationEvent evt, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var handler = sp.GetRequiredService(reg.HandlerType);
        var method = typeof(IEventHandler<>).MakeGenericType(reg.EventType)
            .GetMethod(nameof(IEventHandler<IntegrationEvent>.HandleAsync))!;
        // Gọi lười: chỉ chạy handler sau khi inbox xác nhận chưa xử lý event này
        Task Handle() => (Task)method.Invoke(handler, [evt, ct])!;

        switch (reg.Inbox)
        {
            case InboxKind.EntityFramework:
                await RunWithEfInboxAsync((DbContext)sp.GetRequiredService(reg.DbContextType!), reg, evt, Handle, ct);
                break;
            case InboxKind.Redis:
                await RunWithRedisInboxAsync(sp.GetRequiredService<IConnectionMultiplexer>(), reg, evt, Handle, ct);
                break;
            default:
                await Handle();
                break;
        }
    }

    private async Task RunWithEfInboxAsync(DbContext db, ConsumerRegistration reg, IntegrationEvent evt, Func<Task> handle, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var seen = await db.Set<InboxMessage>().AnyAsync(i => i.EventId == evt.EventId && i.Consumer == reg.QueueName, ct);
        if (seen)
        {
            MessagingTelemetry.Duplicates.Add(1, new KeyValuePair<string, object?>("queue", reg.QueueName));
            logger.LogDebug("Bỏ qua bản trùng {EventId} ở {Queue}", evt.EventId, reg.QueueName);
            return;
        }

        db.Set<InboxMessage>().Add(new InboxMessage { EventId = evt.EventId, Consumer = reg.QueueName, ProcessedAt = clock.GetUtcNow() });
        await handle();
        // Một SaveChanges: thay đổi của handler + dòng inbox + event handler vừa enqueue vào outbox
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task RunWithRedisInboxAsync(IConnectionMultiplexer redis, ConsumerRegistration reg, IntegrationEvent evt, Func<Task> handle, CancellationToken ct)
    {
        var key = $"inbox:{reg.QueueName}:{evt.EventId}";
        var db = redis.GetDatabase();
        if (!await db.StringSetAsync(key, "1", TimeSpan.FromDays(7), When.NotExists))
        {
            MessagingTelemetry.Duplicates.Add(1, new KeyValuePair<string, object?>("queue", reg.QueueName));
            return;
        }
        try
        {
            await handle();
        }
        catch
        {
            await db.KeyDeleteAsync(key); // cho phép lần giao lại xử lý tiếp
            throw;
        }
    }

    private async Task RetryOrDeadLetterAsync(IChannel channel, ConsumerRegistration reg, BasicDeliverEventArgs delivery, Exception ex, CancellationToken ct)
    {
        var attempt = ReadRetryCount(delivery.BasicProperties.Headers) + 1;
        var transient = classifier.IsTransient(ex);
        var props = new BasicProperties(delivery.BasicProperties)
        {
            Headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
            {
                [RetryHeader] = attempt,
                ["x-last-error"] = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message
            }
        };

        if (transient || attempt < MaxAttempts)
        {
            // Backoff 1s, 5s, 25s…; lỗi tạm thời tối đa 30s mỗi lần nhưng không giới hạn số lần
            var delayMs = (int)Math.Min(30_000, 1000 * Math.Pow(5, Math.Min(attempt - 1, 3)));
            props.Expiration = delayMs.ToString();
            await channel.BasicPublishAsync("", $"{reg.QueueName}.retry", false, props, delivery.Body, ct);
            MessagingTelemetry.Retried.Add(1, new KeyValuePair<string, object?>("queue", reg.QueueName));
            logger.LogWarning(ex, "{Queue}: lỗi {Kind}, thử lại lần {Attempt} sau {Delay} ms",
                reg.QueueName, transient ? "tạm thời" : "nghiệp vụ", attempt, delayMs);
        }
        else
        {
            await channel.BasicPublishAsync("", $"{reg.QueueName}.dlq", false, props, delivery.Body, ct);
            MessagingTelemetry.DeadLettered.Add(1, new KeyValuePair<string, object?>("queue", reg.QueueName));
            logger.LogError(ex, "{Queue}: lỗi {Attempt} lần, chuyển vào DLQ", reg.QueueName, attempt);
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
    }

    private static int ReadRetryCount(IDictionary<string, object?>? headers) =>
        headers?.TryGetValue(RetryHeader, out var v) == true
            ? v switch { int i => i, long l => (int)l, byte[] b => int.TryParse(Encoding.UTF8.GetString(b), out var p) ? p : 0, _ => 0 }
            : 0;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        foreach (var c in _channels) await c.DisposeAsync();
    }
}
```

#### `src/BuildingBlocks/Messaging/MessagingExtensions.cs`

```csharp
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BuildingBlocks.Messaging;

/// <summary>
/// Cấu hình messaging của một service:
/// <code>
/// builder.AddShopLabMessaging("ordering")
///     .AddOutbox&lt;OrderingDbContext&gt;()
///     .AddConsumer&lt;StockReserved, StockReservedHandler&gt;(InboxKind.EntityFramework, typeof(OrderingDbContext));
/// </code>
/// </summary>
public sealed class MessagingBuilder(IHostApplicationBuilder builder, string serviceName, bool disabled)
{
    public IHostApplicationBuilder Host => builder;
    public string ServiceName => serviceName;
    /// <summary>Messaging:Disabled=true (test): outbox vẫn ghi dòng, nhưng không publish và không consume.</summary>
    public bool Disabled => disabled;

    /// <summary>IOutbox ghi vào DbContext này + publisher nền + job dọn.</summary>
    public MessagingBuilder AddOutbox<TContext>() where TContext : DbContext
    {
        builder.Services.AddScoped<IOutbox, EfOutbox<TContext>>();
        builder.Services.AddSingleton<OutboxPublisher<TContext>>();
        if (disabled) return this;
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxPublisher<TContext>>());
        builder.Services.AddHostedService<OutboxCleanup<TContext>>();
        return this;
    }

    public MessagingBuilder AddConsumer<TEvent, THandler>(InboxKind inbox = InboxKind.None, Type? dbContextType = null, ushort prefetch = 10)
        where TEvent : IntegrationEvent
        where THandler : class, IEventHandler<TEvent>
    {
        if (inbox == InboxKind.EntityFramework && dbContextType is null)
            throw new ArgumentException("Inbox EF cần dbContextType.");

        var registry = new EventTypeRegistry();
        var eventName = registry.NameOf<TEvent>();
        builder.Services.AddScoped<THandler>();
        if (disabled) return this;
        builder.Services.AddSingleton(new ConsumerRegistration(
            typeof(TEvent), typeof(THandler), eventName,
            $"{serviceName}.{typeof(THandler).Name.Replace("Handler", "").ToLowerInvariant()}",
            inbox, dbContextType, prefetch));
        return this;
    }

    /// <summary>Thêm luật nhận diện lỗi tạm thời (vd lỗi kết nối Mongo ở S5).</summary>
    public MessagingBuilder AddTransientError(Func<Exception, bool> isTransient)
    {
        builder.Services.AddSingleton(isTransient);
        return this;
    }
}

public static class MessagingExtensions
{
    public static MessagingBuilder AddShopLabMessaging(this IHostApplicationBuilder builder, string serviceName, string connectionName = "rabbit")
    {
        var disabled = builder.Configuration.GetValue<bool>("Messaging:Disabled");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<EventTypeRegistry>();
        builder.Services.TryAddSingleton<ITransientErrorClassifier, DefaultTransientErrorClassifier>();

        if (disabled)
        {
            builder.Services.TryAddSingleton<IEventPublisher, InMemoryEventPublisher>();
        }
        else
        {
            builder.AddRabbitMQClient(connectionName);
            builder.Services.TryAddSingleton<IEventPublisher, RabbitMqPublisher>();
            builder.Services.AddHostedService<RabbitMqConsumerHost>();
        }
        return new MessagingBuilder(builder, serviceName, disabled);
    }
}

/// <summary>Publisher giả cho test: giữ lại message đã "gửi" để assert.</summary>
public sealed class InMemoryEventPublisher(EventTypeRegistry registry) : IEventPublisher
{
    public System.Collections.Concurrent.ConcurrentQueue<(string Type, string Payload)> Sent { get; } = new();

    public Task PublishAsync(IntegrationEvent evt, CancellationToken ct = default)
    {
        Sent.Enqueue((registry.NameOf(evt.GetType()), registry.Serialize(evt)));
        return Task.CompletedTask;
    }

    public Task PublishRawAsync(string type, string payload, Guid eventId, Guid correlationId, string? traceParent, CancellationToken ct = default)
    {
        Sent.Enqueue((type, payload));
        return Task.CompletedTask;
    }
}
```

### Buổi 4 — Test messaging

**Việc cần làm**

1. `RabbitMqFixture`; `ShopLabApiFactory` đặt `Messaging:Disabled=true` (test API không cần RabbitMQ).
2. OutboxTests: commit có đúng một dòng outbox; rollback không còn dòng nào.
3. OutboxPublisherTests: 2 publisher song song không gửi trùng; gửi lỗi thì dòng vẫn chờ và được thử lại.
4. ConsumerHostTests: cùng event giao 2 lần chỉ xử lý 1; handler lỗi 3 lần → DLQ.

**Lệnh**

```bash
mkdir -p tests/BuildingBlocks.Tests && python3 tools/sln-sync.py && dotnet test tests/BuildingBlocks.Tests
```

**File**

- `tests/TestKit/RabbitMqFixture.cs`
- `tests/TestKit/ShopLabApiFactory.cs` *(sửa)*
- `tests/BuildingBlocks.Tests/BuildingBlocks.Tests.csproj`
- `tests/BuildingBlocks.Tests/ConsumerHostTests.cs`
- `tests/BuildingBlocks.Tests/OutboxPublisherTests.cs`
- `tests/BuildingBlocks.Tests/OutboxTests.cs`
- `tests/BuildingBlocks.Tests/TestContracts.cs`

#### `tests/TestKit/RabbitMqFixture.cs`

```csharp
using Testcontainers.RabbitMq;

namespace TestKit;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder().WithImage("rabbitmq:4-management-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
```

#### `tests/TestKit/ShopLabApiFactory.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

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
        // Test API không cần RabbitMQ: outbox vẫn ghi, không publish/consume. Test messaging dùng fixture riêng.
        builder.UseSetting("Messaging:Disabled", "true");
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

#### `tests/BuildingBlocks.Tests/BuildingBlocks.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\TestKit\TestKit.csproj" />
    <ProjectReference Include="..\..\src\BuildingBlocks\BuildingBlocks.csproj" />
  </ItemGroup>
</Project>
```

#### `tests/BuildingBlocks.Tests/ConsumerHostTests.cs`

```csharp
using System.Collections.Concurrent;
using BuildingBlocks.Messaging;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TestKit;

namespace BuildingBlocks.Tests;

/// <summary>
/// RabbitMQ thật + Postgres thật: Inbox chống xử lý trùng, retry rồi vào DLQ.
/// Chạy: dotnet test tests/BuildingBlocks.Tests --filter ConsumerHostTests
/// </summary>
public sealed class ConsumerHostTests(PostgresFixture pg, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>
{
    public static readonly ConcurrentDictionary<string, int> Calls = new();

    public sealed class CountingHandler(TestDbContext db) : IEventHandler<SomethingHappened>
    {
        public Task HandleAsync(SomethingHappened evt, CancellationToken ct)
        {
            Calls.AddOrUpdate(evt.Text, 1, (_, n) => n + 1);
            db.Notes.Add(new Note { Text = evt.Text });
            return Task.CompletedTask;
        }
    }

    public sealed class AlwaysFailingHandler : IEventHandler<SomethingHappened>
    {
        public Task HandleAsync(SomethingHappened evt, CancellationToken ct) =>
            throw new InvalidOperationException("lỗi nghiệp vụ cố ý");
    }

    private async Task<(RabbitMqConsumerHost Host, IConnection Conn, Func<TestDbContext> NewDb)> StartAsync<THandler>(string queue, InboxKind inbox)
        where THandler : class, IEventHandler<SomethingHappened>
    {
        var newDb = await TestDbContext.CreateAsync(pg);
        var conn = await new ConnectionFactory { Uri = new Uri(rabbit.ConnectionString) }.CreateConnectionAsync();
        var reg = new ConsumerRegistration(typeof(SomethingHappened), typeof(THandler), "test.something_happened", queue, inbox,
            inbox == InboxKind.EntityFramework ? typeof(TestDbContext) : null, 10);
        var services = new ServiceCollection()
            .AddScoped(_ => newDb())
            .AddScoped<THandler>()
            .BuildServiceProvider();
        var host = new RabbitMqConsumerHost(conn, [reg], services.GetRequiredService<IServiceScopeFactory>(), TestRegistry.Instance,
            new DefaultTransientErrorClassifier([]), TimeProvider.System, NullLogger<RabbitMqConsumerHost>.Instance);
        await host.StartAsync(CancellationToken.None);
        await Task.Delay(500); // đợi khai báo queue xong
        return (host, conn, newDb);
    }

    private async Task PublishAsync(IConnection conn, SomethingHappened evt)
    {
        var publisher = new RabbitMqPublisher(conn, TestRegistry.Instance, NullLogger<RabbitMqPublisher>.Instance);
        await publisher.PublishAsync(evt);
    }

    [Fact]
    public async Task Same_event_delivered_twice_is_handled_once()
    {
        var (host, conn, newDb) = await StartAsync<CountingHandler>("test.inbox", InboxKind.EntityFramework);
        var evt = new SomethingHappened("dup-" + Guid.NewGuid());

        await PublishAsync(conn, evt);
        await PublishAsync(conn, evt); // cùng EventId: giả lập outbox gửi lại sau khi publisher chết giữa chừng
        await Task.Delay(2000);

        Calls[evt.Text].ShouldBeGreaterThanOrEqualTo(1);
        await using var db = newDb();
        (await db.Notes.CountAsync(n => n.Text == evt.Text)).ShouldBe(1);
        (await db.Set<InboxMessage>().CountAsync(i => i.EventId == evt.EventId)).ShouldBe(1);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Handler_failing_three_times_ends_in_dead_letter_queue()
    {
        var (host, conn, _) = await StartAsync<AlwaysFailingHandler>("test.failing", InboxKind.None);
        await PublishAsync(conn, new SomethingHappened("boom"));

        // backoff 1s + 5s rồi vào DLQ ở lần 3
        await using var ch = await conn.CreateChannelAsync();
        uint dlq = 0;
        for (var i = 0; i < 30 && dlq == 0; i++)
        {
            await Task.Delay(500);
            dlq = await ch.MessageCountAsync("test.failing.dlq");
        }
        dlq.ShouldBe(1u);
        await host.StopAsync(CancellationToken.None);
    }
}
```

#### `tests/BuildingBlocks.Tests/OutboxPublisherTests.cs`

```csharp
using System.Collections.Concurrent;
using BuildingBlocks.Messaging;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TestKit;

namespace BuildingBlocks.Tests;

/// <summary>FOR UPDATE SKIP LOCKED: hai publisher chạy song song không gửi trùng; publisher lỗi giữa chừng thì gửi lại.</summary>
public sealed class OutboxPublisherTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private sealed class CountingPublisher(Func<string, bool>? failWhen = null) : IEventPublisher
    {
        public ConcurrentBag<Guid> Sent { get; } = [];

        public Task PublishAsync(IntegrationEvent evt, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task PublishRawAsync(string type, string payload, Guid eventId, Guid correlationId, string? traceParent, CancellationToken ct = default)
        {
            await Task.Delay(2, ct); // để hai publisher thật sự chồng lên nhau
            if (failWhen?.Invoke(payload) == true) throw new InvalidOperationException("broker nack");
            Sent.Add(eventId);
        }
    }

    private static async Task SeedAsync(Func<TestDbContext> newDb, int count)
    {
        await using var db = newDb();
        var outbox = new EfOutbox<TestDbContext>(db, TestRegistry.Instance);
        for (var i = 0; i < count; i++) outbox.Enqueue(new SomethingHappened($"m{i}"));
        await db.SaveChangesAsync();
    }

    private static OutboxPublisher<TestDbContext> CreatePublisher(Func<TestDbContext> newDb, IEventPublisher publisher)
    {
        var services = new ServiceCollection().AddScoped(_ => newDb()).BuildServiceProvider();
        return new OutboxPublisher<TestDbContext>(services.GetRequiredService<IServiceScopeFactory>(), publisher,
            TimeProvider.System, NullLogger<OutboxPublisher<TestDbContext>>.Instance);
    }

    [Fact]
    public async Task Two_parallel_publishers_never_send_the_same_message_twice()
    {
        var newDb = await TestDbContext.CreateAsync(pg);
        await SeedAsync(newDb, 500);
        var sink = new CountingPublisher();
        var a = CreatePublisher(newDb, sink);
        var b = CreatePublisher(newDb, sink);

        async Task Drain(OutboxPublisher<TestDbContext> p)
        {
            while (await p.PublishBatchAsync(CancellationToken.None) > 0) { }
        }
        await Task.WhenAll(Drain(a), Drain(b), Drain(a), Drain(b));

        sink.Sent.Count.ShouldBe(500);
        sink.Sent.Distinct().Count().ShouldBe(500);
        await using var check = newDb();
        (await check.Set<OutboxMessage>().CountAsync(m => m.ProcessedAt == null)).ShouldBe(0);
    }

    [Fact]
    public async Task Failed_message_stays_pending_and_is_retried_later()
    {
        var newDb = await TestDbContext.CreateAsync(pg);
        await SeedAsync(newDb, 3);
        var failing = CreatePublisher(newDb, new CountingPublisher(payload => payload.Contains("m1")));

        await failing.PublishBatchAsync(CancellationToken.None);

        await using (var check = newDb())
        {
            var pending = await check.Set<OutboxMessage>().SingleAsync(m => m.ProcessedAt == null);
            pending.Attempts.ShouldBe(1);
            pending.LastError.ShouldContain("nack");
            pending.NextAttemptAt.ShouldNotBeNull();
            // đưa hạn thử lại về quá khứ để lần sau lấy ngay
            pending.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await check.SaveChangesAsync();
        }

        var healthy = new CountingPublisher();
        await CreatePublisher(newDb, healthy).PublishBatchAsync(CancellationToken.None);
        healthy.Sent.Count.ShouldBe(1);
    }
}
```

#### `tests/BuildingBlocks.Tests/OutboxTests.cs`

```csharp
using BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;
using TestKit;

namespace BuildingBlocks.Tests;

/// <summary>Outbox ghi cùng transaction với dữ liệu nghiệp vụ.</summary>
public sealed class OutboxTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Commit_writes_business_row_and_exactly_one_outbox_row()
    {
        var newDb = await TestDbContext.CreateAsync(pg);
        await using (var db = newDb())
        {
            db.Notes.Add(new Note { Text = "a" });
            new EfOutbox<TestDbContext>(db, TestRegistry.Instance).Enqueue(new SomethingHappened("a"));
            await db.SaveChangesAsync();
        }

        await using var check = newDb();
        (await check.Notes.CountAsync()).ShouldBe(1);
        var msg = await check.Set<OutboxMessage>().SingleAsync();
        msg.Type.ShouldBe("test.something_happened");
        msg.ProcessedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Rollback_leaves_no_outbox_row()
    {
        var newDb = await TestDbContext.CreateAsync(pg);
        await using (var db = newDb())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Notes.Add(new Note { Text = "b" });
            new EfOutbox<TestDbContext>(db, TestRegistry.Instance).Enqueue(new SomethingHappened("b"));
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using var check = newDb();
        (await check.Notes.CountAsync()).ShouldBe(0);
        (await check.Set<OutboxMessage>().CountAsync()).ShouldBe(0);
    }
}
```

#### `tests/BuildingBlocks.Tests/TestContracts.cs`

```csharp
using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Tests;

/// <summary>Event và DbContext tối giản chỉ dùng cho test lớp messaging.</summary>
[EventName("test.something_happened")]
public sealed record SomethingHappened(string Text) : IntegrationEvent;

public sealed class Note
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("test");
        b.Entity<Note>().ToTable("notes");
        b.AddOutboxInbox();
    }

    public static async Task<Func<TestDbContext>> CreateAsync(TestKit.PostgresFixture pg)
    {
        var cs = await pg.CreateDatabaseAsync("bb");
        TestDbContext New() => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);
        await using var db = New();
        await db.Database.EnsureCreatedAsync();
        return New;
    }
}

public static class TestRegistry
{
    public static readonly EventTypeRegistry Instance = new(typeof(SomethingHappened).Assembly, typeof(IntegrationEvent).Assembly);
}
```

**Kiểm tra cuối buổi**

```bash
dotnet test tests/BuildingBlocks.Tests   # 6 test xanh
```

**Xong tuần 1 khi:**

- [ ] Test BuildingBlocks xanh
- [ ] Hiểu vì sao cần SKIP LOCKED và vì sao handler phải chạy sau inbox

---

## Tuần 2 — Saga checkout: Inventory giữ hàng qua event, Payment qua event, Ordering điều phối

**Mục tiêu tuần**

- Catalog/Inventory phát event qua outbox; Inventory nghe OrderCreated/OrderPaid/OrderCancelled.
- Ordering: `POST /checkout` → 202, `CheckoutSaga` chuyển trạng thái theo event, job huỷ đơn quá hạn, refund khi tiền tới muộn.
- Payment nghe PaymentRequested/OrderCancelled/PaymentRefundRequested.
- Test từng nhánh saga trên Postgres thật, không cần RabbitMQ.

### Buổi 5 — Catalog phát ProductChanged, nghe StockChanged

**Việc cần làm**

1. DbContext các service thêm `AddOutboxInbox()`. Lab: xoá volume Postgres để EnsureCreated tạo bảng mới (hoặc tạo migration).
2. Product: `ProductEvents.Changed(product)`; các slice Create/Update/Delete/Restore/SetActive enqueue ProductChanged.
3. `StockChangedHandler`: cập nhật cờ còn hàng (dùng để ẩn/hiện ở storefront).

**Lệnh**

```bash
docker volume rm shoplab-pg     # lab: tạo lại DB có bảng outbox/inbox
```

**File**

- `src/Services/Catalog.Api/Data/CatalogDbContext.cs` *(sửa)*
- `src/Services/Catalog.Api/Domain/Product.cs` *(sửa)*
- `src/Services/Catalog.Api/EventHandlers/StockChangedHandler.cs`
- `src/Services/Catalog.Api/Features/Products/CreateProduct.cs` *(sửa)*
- `src/Services/Catalog.Api/Features/Products/DeleteProduct.cs` *(sửa)*
- `src/Services/Catalog.Api/Features/Products/RestoreProduct.cs` *(sửa)*
- `src/Services/Catalog.Api/Features/Products/SetProductActive.cs` *(sửa)*
- `src/Services/Catalog.Api/Features/Products/UpdateProduct.cs` *(sửa)*
- `src/Services/Catalog.Api/Program.cs` *(sửa)*
- `src/Services/Customer.Api/Data/CustomerDbContext.cs` *(sửa)*

#### `src/Services/Catalog.Api/Data/CatalogDbContext.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
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
        modelBuilder.AddOutboxInbox(); // S3
        modelBuilder.ApplySoftDeleteFilters();
    }
}
```

#### `src/Services/Catalog.Api/Domain/Product.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

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

public static class ProductEvents
{
    /// <summary>Event báo sản phẩm vừa đổi; gọi sau Touch() để mang Version mới.</summary>
    public static Contracts.Catalog.ProductChanged Changed(this Product p) =>
        new(p.Id, p.Version) { CorrelationId = p.Id };
}
```

#### `src/Services/Catalog.Api/EventHandlers/StockChangedHandler.cs`

```csharp
using BuildingBlocks.Messaging;
using Catalog.Api.Data;
using Catalog.Api.Domain;
using Contracts.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.EventHandlers;

/// <summary>
/// Event-carried state transfer: Catalog giữ cờ InStock từ event của Inventory,
/// nên trang sản phẩm không cần gọi sang Inventory mỗi lần hiển thị.
/// Cờ chỉ đổi khi vượt ngưỡng 0, nên event lặp hay đến muộn vài trăm ms không làm sai.
/// </summary>
public sealed class StockChangedHandler(CatalogDbContext db, IOutbox outbox, ILogger<StockChangedHandler> logger) : IEventHandler<StockChanged>
{
    public async Task HandleAsync(StockChanged evt, CancellationToken ct)
    {
        var variant = await db.Variants.Include(v => v.Product).FirstOrDefaultAsync(v => v.Sku == evt.Sku, ct);
        if (variant is null)
        {
            logger.LogDebug("StockChanged cho SKU {Sku} không có trong Catalog", evt.Sku);
            return;
        }

        if (variant.SetInStock(evt.AvailableOnline > 0))
        {
            variant.Product.Touch();
            outbox.Enqueue(variant.Product.Changed());
        }
    }
}
```

#### `src/Services/Catalog.Api/Features/Products/CreateProduct.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
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
        app.MapPost("/admin/products", async (SaveProductRequest req, CatalogDbContext db, IOutbox outbox, CancellationToken ct) =>
            {
                await ProductGuards.EnsureCategoryExists(db, req.CategoryId, ct);
                var product = Product.Create(req.Name, req.Slug ?? req.Name, req.Description ?? "", req.CategoryId, req.Brand ?? "", req.Status);
                product.SyncVariants(req.Variants.Select(v => v.ToInput()));
                await ProductGuards.EnsureUnique(db, product, ct);

                db.Products.Add(product);
                outbox.Enqueue(product.Changed());
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

#### `src/Services/Catalog.Api/Features/Products/DeleteProduct.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Products;

public sealed class DeleteProduct : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/admin/products/{id:guid}", async (Guid id, CatalogDbContext db, IOutbox outbox, CancellationToken ct) =>
            {
                var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
                              ?? throw NotFoundException.For("product", id);
                product.MarkDeleted();
                db.Products.Remove(product); // AuditInterceptor đổi thành IsDeleted = true
                outbox.Enqueue(product.Changed());
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}
```

#### `src/Services/Catalog.Api/Features/Products/RestoreProduct.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
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
        app.MapPost("/admin/products/{id:guid}/restore", async (Guid id, CatalogDbContext db, IOutbox outbox, CancellationToken ct) =>
            {
                var product = await db.Products.IgnoreQueryFilters().Include(p => p.Variants)
                                  .FirstOrDefaultAsync(p => p.Id == id, ct)
                              ?? throw NotFoundException.For("product", id);
                if (!product.IsDeleted) return Results.NoContent();

                product.Restore();
                await ProductGuards.EnsureUnique(db, product, ct);
                outbox.Enqueue(product.Changed());
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}
```

#### `src/Services/Catalog.Api/Features/Products/SetProductActive.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
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
        app.MapPost("/admin/products/{id:guid}/active", async (Guid id, SetActiveRequest req, CatalogDbContext db, IOutbox outbox, CancellationToken ct) =>
            {
                var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
                              ?? throw NotFoundException.For("product", id);
                product.SetActive(req.IsActive);
                outbox.Enqueue(product.Changed());
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}
```

#### `src/Services/Catalog.Api/Features/Products/UpdateProduct.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Features.Products;

public sealed class UpdateProduct : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/admin/products/{id:guid}", async (Guid id, SaveProductRequest req, CatalogDbContext db, IOutbox outbox, CancellationToken ct) =>
            {
                var product = await db.Products.Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == id, ct)
                              ?? throw NotFoundException.For("product", id);
                await ProductGuards.EnsureCategoryExists(db, req.CategoryId, ct);

                product.Update(req.Name, req.Slug ?? req.Name, req.Description ?? "", req.CategoryId, req.Brand ?? "", req.Status);
                var removed = product.SyncVariants(req.Variants.Select(v => v.ToInput()));
                db.Variants.RemoveRange(removed); // xoá mềm variant không còn trong form
                await ProductGuards.EnsureUnique(db, product, ct);
                outbox.Enqueue(product.Changed());

                await db.SaveChangesAsync(ct);
                return Results.Ok(product.ToDto());
            })
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin.Products");
}
```

#### `src/Services/Catalog.Api/Program.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks;
using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using Catalog.Api.Data;
using Catalog.Api.EventHandlers;
using Contracts.Inventory;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddShopLabDbContext<CatalogDbContext>("catalogdb");

// S3: Catalog phát ProductChanged, nghe StockChanged
builder.AddShopLabMessaging("catalog")
    .AddOutbox<CatalogDbContext>()
    .AddConsumer<StockChanged, StockChangedHandler>(InboxKind.EntityFramework, typeof(CatalogDbContext));

var app = builder.Build();

await app.InitializeDatabaseAsync<CatalogDbContext>(SeedData.RunAsync);
app.UseShopLabApi(typeof(Catalog.Api.ApiMarker).Assembly);

app.Run();
```

#### `src/Services/Customer.Api/Data/CustomerDbContext.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
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

        b.AddOutboxInbox(); // S3: inbox cho consumer cộng điểm
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

### Buổi 6 — Inventory theo event

**Việc cần làm**

1. `OrderCreatedHandler`: giữ hàng → `StockReserved`; hết hàng là kết quả nghiệp vụ → `StockReservationFailed` (không retry).
2. `OrderPaidHandler` commit, `OrderCancelledHandler` release (không có reservation thì bỏ qua).
3. `StockLedger.EnqueueStockChangedAsync`: phát StockChanged cho các SKU vừa đổi.

**File**

- `src/Services/Inventory.Api/Data/InventoryDbContext.cs` *(sửa)*
- `src/Services/Inventory.Api/Domain/StockLedger.cs` *(sửa)*
- `src/Services/Inventory.Api/EventHandlers/OrderEventHandlers.cs`
- `src/Services/Inventory.Api/Features/Admin/AdminStock.cs` *(sửa)*
- `src/Services/Inventory.Api/Features/Internal/Reservations.cs` *(sửa)*
- `src/Services/Inventory.Api/Program.cs` *(sửa)*

#### `src/Services/Inventory.Api/Data/InventoryDbContext.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
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

        b.AddOutboxInbox();      // S3
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

#### `src/Services/Inventory.Api/Domain/StockLedger.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
using BuildingBlocks.Results;
using Contracts.Inventory;
using Inventory.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Domain;

public sealed record ReserveLine(string Sku, int Qty);

/// <summary>
/// Mọi thay đổi tồn kho đi qua đây: cập nhật StockLevel và ghi StockMovement trong cùng một SaveChanges.
/// Không gọi SaveChanges bên trong; endpoint dùng SaveAsync (có retry xmin), consumer để consumer host lưu.
/// Từ S3: SKU nào bị đổi thì trước khi lưu phát StockChanged qua outbox (EnqueueStockChangedAsync).
/// </summary>
public sealed class StockLedger(InventoryDbContext db, TimeProvider clock, IOutbox? outbox = null)
{
    private readonly HashSet<string> _touched = [];

    /// <summary>Endpoint: chạy thao tác, phát StockChanged, lưu; xung đột xmin thì làm lại tối đa 3 lần.</summary>
    public async Task<T> SaveAsync<T>(Func<Task<T>> action, int attempts = 3, CancellationToken ct = default)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                var result = await action();
                await EnqueueStockChangedAsync(ct);
                await db.SaveChangesAsync(ct);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (i < attempts)
            {
                db.ChangeTracker.Clear();
                _touched.Clear();
                await Task.Delay(Random.Shared.Next(5, 25) * i, ct);
            }
        }
    }

    /// <summary>Tính lại tồn bán online cho các SKU vừa đổi (gồm thay đổi chưa lưu) và enqueue StockChanged.</summary>
    public async Task EnqueueStockChangedAsync(CancellationToken ct)
    {
        if (outbox is null || _touched.Count == 0) return;
        var sellable = await db.Locations.Where(l => l.SellableOnline).Select(l => l.Id).ToListAsync(ct);
        foreach (var sku in _touched)
        {
            var dbLevels = await db.StockLevels.AsNoTracking().Where(s => s.Sku == sku).ToListAsync(ct);
            // Giá trị đang tracked (chưa lưu) thắng giá trị trong DB
            var levels = dbLevels
                .Select(d => db.StockLevels.Local.FirstOrDefault(l => l.Sku == d.Sku && l.LocationId == d.LocationId) ?? d)
                .Concat(db.StockLevels.Local.Where(l => l.Sku == sku && dbLevels.All(d => d.LocationId != l.LocationId)))
                .ToList();
            outbox.Enqueue(new StockChanged(
                sku,
                levels.Where(l => sellable.Contains(l.LocationId)).Sum(l => l.Available),
                levels.Sum(l => l.OnHand)));
        }
        _touched.Clear();
    }

    public async Task<Location> GetLocationAsync(string code, CancellationToken ct) =>
        await db.Locations.FirstOrDefaultAsync(l => l.Code == code.ToUpperInvariant(), ct)
        ?? throw new BadRequestException("LOCATION_NOT_FOUND", $"Không có location '{code}'.");

    public async Task<StockLevel> GetOrCreateLevelAsync(string sku, Guid locationId, CancellationToken ct)
    {
        sku = sku.ToUpperInvariant();
        _touched.Add(sku);
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
            _touched.Add(line.Sku);
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

#### `src/Services/Inventory.Api/EventHandlers/OrderEventHandlers.cs`

```csharp
using BuildingBlocks.Messaging;
using BuildingBlocks.Results;
using Contracts.Inventory;
using Contracts.Orders;
using Inventory.Api.Domain;

namespace Inventory.Api.EventHandlers;

/// <summary>
/// Saga bước 1: Ordering tạo đơn → Inventory giữ hàng → báo StockReserved hoặc StockReservationFailed.
/// Đơn flash sale / POS có RequiresStock = false thì bỏ qua (hàng đã rời kho chung).
/// Consumer host lưu thay đổi + inbox + outbox trong một transaction.
/// </summary>
public sealed class OrderCreatedHandler(StockLedger ledger, IOutbox outbox) : IEventHandler<OrderCreated>
{
    public async Task HandleAsync(OrderCreated evt, CancellationToken ct)
    {
        if (!evt.RequiresStock) return;
        try
        {
            await ledger.ReserveAsync(evt.OrderId, evt.Lines.Select(l => new ReserveLine(l.Sku, l.Qty)).ToList(), TimeSpan.FromMinutes(20), ct);
            await ledger.EnqueueStockChangedAsync(ct);
            outbox.Enqueue(new StockReserved(evt.OrderId) { CorrelationId = evt.OrderId });
        }
        catch (ConflictException ex) when (ex.Code == "OUT_OF_STOCK")
        {
            // Hết hàng là kết quả nghiệp vụ, không phải lỗi: báo lại cho saga, không retry
            outbox.Enqueue(new StockReservationFailed(evt.OrderId, ex.Message) { CorrelationId = evt.OrderId });
        }
    }
}

/// <summary>Đơn đã thanh toán: hàng giữ thành hàng bán (movement Sale).</summary>
public sealed class OrderPaidHandler(StockLedger ledger) : IEventHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid evt, CancellationToken ct)
    {
        await ledger.CommitAsync(evt.OrderId, "ordering", ct);
        await ledger.EnqueueStockChangedAsync(ct);
    }
}

/// <summary>Bù trừ: đơn huỷ thì trả hàng đã giữ. Không có reservation (vd chưa kịp giữ) thì không làm gì.</summary>
public sealed class OrderCancelledHandler(StockLedger ledger) : IEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled evt, CancellationToken ct)
    {
        await ledger.ReleaseAsync(evt.OrderId, ct);
        await ledger.EnqueueStockChangedAsync(ct);
    }
}
```

#### `src/Services/Inventory.Api/Features/Admin/AdminStock.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

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
            await ledger.SaveAsync(async () =>
            {
                await ledger.ReceiveAsync(req.Sku, req.LocationCode, req.Qty, refId, user.Username ?? "admin", req.Note, ct);
                return 0;
            });
            return Results.Created($"/admin/stock/{req.Sku}/movements", new { refId });
        });

        // AdjustStock: kiểm kê
        group.MapPost("/stock/adjustments", async (AdjustStockRequest req, StockLedger ledger, InventoryDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var delta = await ledger.SaveAsync(() =>
                ledger.AdjustAsync(req.Sku, req.LocationCode, req.CountedQty, Guid.CreateVersion7().ToString(), user.Username ?? "admin", req.Reason, ct));
            return Results.Ok(new { delta });
        });

        // TransferStock: TransferOut + TransferIn trong một transaction
        group.MapPost("/stock/transfers", async (TransferStockRequest req, StockLedger ledger, InventoryDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var refId = Guid.CreateVersion7().ToString();
            await ledger.SaveAsync(async () =>
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

#### `src/Services/Inventory.Api/Features/Internal/Reservations.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

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
            var created = await ledger.SaveAsync(() =>
                ledger.ReserveAsync(req.OrderId, req.Lines, TimeSpan.FromMinutes(req.TtlMinutes ?? 15), ct));
            return Results.Ok(created.Select(r => r.ToDto()));
        });

        // CommitReservation
        group.MapPost("/{orderId:guid}/commit", async (Guid orderId, StockLedger ledger, InventoryDbContext db, CancellationToken ct) =>
        {
            var count = await ledger.SaveAsync(() => ledger.CommitAsync(orderId, "ordering", ct));
            return Results.Ok(new { committed = count });
        });

        // ReleaseReservation
        group.MapPost("/{orderId:guid}/release", async (Guid orderId, StockLedger ledger, InventoryDbContext db, CancellationToken ct) =>
        {
            var count = await ledger.SaveAsync(() => ledger.ReleaseAsync(orderId, ct));
            return Results.Ok(new { released = count });
        });

        group.MapGet("/{orderId:guid}", async (Guid orderId, InventoryDbContext db, CancellationToken ct) =>
            (await db.Reservations.AsNoTracking().Where(r => r.OrderId == orderId).ToListAsync(ct)).Select(r => r.ToDto()));
    }
}
```

#### `src/Services/Inventory.Api/Program.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks;
using BuildingBlocks.Chaos;
using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using Contracts.Orders;
using Inventory.Api.Data;
using Inventory.Api.Domain;
using Inventory.Api.EventHandlers;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddChaos();
builder.AddShopLabDbContext<InventoryDbContext>("inventorydb");
builder.Services.AddScoped<StockLedger>();

// S3: event-driven
builder.AddShopLabMessaging("inventory")
    .AddOutbox<InventoryDbContext>()
    .AddConsumer<OrderCreated, OrderCreatedHandler>(InboxKind.EntityFramework, typeof(InventoryDbContext))
    .AddConsumer<OrderPaid, OrderPaidHandler>(InboxKind.EntityFramework, typeof(InventoryDbContext))
    .AddConsumer<OrderCancelled, OrderCancelledHandler>(InboxKind.EntityFramework, typeof(InventoryDbContext));

var app = builder.Build();

await app.InitializeDatabaseAsync<InventoryDbContext>(SeedData.RunAsync);
app.UseChaos(app.Environment);
app.UseShopLabApi(typeof(Inventory.Api.ApiMarker).Assembly);

app.Run();
```

### Buổi 7 — Saga: state, chuyển bước, handler

**Việc cần làm**

1. `CheckoutSagaState` (Step, PaymentDeadline, xmin) — hai event cùng lúc cho một đơn: một bên bị xung đột → retry.
2. `CheckoutSaga`: OnStockReserved → PendingPayment + PaymentRequested; OnPaymentSucceeded → Paid + OrderPaid; hết hàng/thanh toán lỗi → Cancelled + OrderCancelled; tiền tới sau khi huỷ → PaymentRefundRequested.
3. `OrderEvents` dựng event từ Order (CorrelationId = OrderId). Handler mỏng chuyển event vào saga.

**File**

- `src/Services/Ordering.Api/Domain/Order.cs` *(sửa)*
- `src/Services/Ordering.Api/Domain/OrderEvents.cs`
- `src/Services/Ordering.Api/Sagas/CheckoutSaga.cs`
- `src/Services/Ordering.Api/Sagas/CheckoutSagaState.cs`
- `src/Services/Ordering.Api/Data/OrderingDbContext.cs` *(sửa)*
- `src/Services/Ordering.Api/EventHandlers/SagaEventHandlers.cs`

#### `src/Services/Ordering.Api/Domain/Order.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

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
    public string? CustomerEmail { get; private set; }
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
        long discountTotal = 0, string? customerEmail = null)
    {
        var order = new Order
        {
            Number = number,
            CustomerId = customerId,
            CustomerEmail = customerEmail,
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

#### `src/Services/Ordering.Api/Domain/OrderEvents.cs`

```csharp
using Contracts.Orders;

namespace Ordering.Api.Domain;

/// <summary>Dựng integration event từ Order. CorrelationId luôn là OrderId để lần theo một đơn qua mọi service.</summary>
public static class OrderEvents
{
    private static IReadOnlyList<OrderLineItem> Items(Order o) =>
        o.Lines.Select(l => new OrderLineItem(l.Sku, l.Name, l.Qty, l.UnitPrice, l.LineDiscount)).ToList();

    public static OrderCreated Created(this Order o, bool requiresStock) =>
        new(o.Id, o.Number, o.CustomerId, o.Channel.ToString(), o.Total, o.CouponCode, Items(o), requiresStock) { CorrelationId = o.Id };

    public static OrderPaid Paid(this Order o, DateTimeOffset paidAt) =>
        new(o.Id, o.Number, o.CustomerId, o.CustomerEmail, o.Channel.ToString(), o.StoreId, o.Total, o.DiscountTotal, Items(o), paidAt)
        { CorrelationId = o.Id };

    public static OrderCancelled Cancelled(this Order o) =>
        new(o.Id, o.CustomerId, o.Channel.ToString(), o.CancelReason ?? "", o.CouponCode) { CorrelationId = o.Id };

    public static OrderStatusChanged StatusChanged(this Order o) =>
        new(o.Id, o.Status.ToString(), o.Version) { CorrelationId = o.Id };
}
```

#### `src/Services/Ordering.Api/Sagas/CheckoutSaga.cs`

```csharp
using BuildingBlocks.Messaging;
using Contracts.Payments;
using Microsoft.EntityFrameworkCore;
using Ordering.Api.Data;
using Ordering.Api.Domain;

namespace Ordering.Api.Sagas;

/// <summary>
/// Saga orchestration cho checkout. Mỗi phương thức xử lý một event, đổi Order + saga state,
/// rồi enqueue bước tiếp theo vào outbox. Không gọi SaveChanges: consumer host lưu một lần.
///
///   OrderCreated ──▶ Inventory ──StockReserved──▶ PaymentRequested ──▶ Payment ──PaymentSucceeded──▶ OrderPaid
///                          └─StockReservationFailed─▶ OrderCancelled          └─PaymentFailed─▶ OrderCancelled (Inventory trả hàng)
///   Quá PaymentDeadline (job) ─▶ OrderCancelled
///   PaymentSucceeded đến sau khi đã huỷ ─▶ PaymentRefundRequested
///
/// Mọi phương thức idempotent: event lặp hoặc đến muộn khi saga đã kết thúc thì bỏ qua.
/// </summary>
public sealed class CheckoutSaga(OrderingDbContext db, IOutbox outbox, TimeProvider clock, ILogger<CheckoutSaga> logger)
{
    public async Task OnStockReservedAsync(Guid orderId, CancellationToken ct)
    {
        var (order, saga) = await LoadAsync(orderId, ct);
        if (saga is null || saga.Step != SagaStep.AwaitingStock) return;

        order.MarkPendingPayment();
        saga.StockReserved();
        outbox.Enqueue(order.StatusChanged());
        outbox.Enqueue(new PaymentRequested(order.Id, order.Total) { CorrelationId = order.Id });
    }

    public async Task OnStockReservationFailedAsync(Guid orderId, string reason, CancellationToken ct)
    {
        var (order, saga) = await LoadAsync(orderId, ct);
        if (saga is null || saga.IsFinished) return;
        Cancel(order, saga, $"Hết hàng: {reason}");
    }

    public async Task OnPaymentSucceededAsync(Guid orderId, Guid paymentId, CancellationToken ct)
    {
        var (order, saga) = await LoadAsync(orderId, ct);
        if (saga is null) return;

        if (order.Status == OrderStatus.Cancelled)
        {
            // Tiền về sau khi đơn đã huỷ (hết hạn thanh toán): không bán nữa, yêu cầu hoàn tiền
            logger.LogWarning("Đơn {Number} đã huỷ nhưng thanh toán thành công, yêu cầu hoàn tiền", order.Number);
            outbox.Enqueue(new PaymentRefundRequested(order.Id, "Đơn đã huỷ trước khi thanh toán hoàn tất") { CorrelationId = order.Id });
            return;
        }
        if (saga.Step != SagaStep.AwaitingPayment) return;

        order.MarkPaid();
        saga.Paid(paymentId);
        outbox.Enqueue(order.StatusChanged());
        outbox.Enqueue(order.Paid(clock.GetUtcNow()));
    }

    public async Task OnPaymentFailedAsync(Guid orderId, string reason, CancellationToken ct)
    {
        var (order, saga) = await LoadAsync(orderId, ct);
        if (saga is null || saga.IsFinished) return;
        Cancel(order, saga, $"Thanh toán lỗi: {reason}");
    }

    /// <summary>S4: coupon hết lượt (giành nhau giữa hai đơn) → huỷ đơn.</summary>
    public async Task OnCouponRejectedAsync(Guid orderId, string reason, CancellationToken ct)
    {
        var (order, saga) = await LoadAsync(orderId, ct);
        if (saga is null || saga.IsFinished) return;
        Cancel(order, saga, $"Mã giảm giá không dùng được: {reason}");
    }

    /// <summary>Job hẹn giờ: huỷ đơn chưa thanh toán khi quá hạn. Trả số đơn đã huỷ.</summary>
    public async Task<int> ExpireUnpaidAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = await db.Sagas
            .Where(s => (s.Step == SagaStep.AwaitingStock || s.Step == SagaStep.AwaitingPayment) && s.PaymentDeadline < now)
            .OrderBy(s => s.PaymentDeadline)
            .Take(200)
            .ToListAsync(ct);

        foreach (var saga in expired)
        {
            var order = await db.Orders.Include(o => o.Lines).FirstAsync(o => o.Id == saga.OrderId, ct);
            Cancel(order, saga, "Hết hạn thanh toán");
        }
        return expired.Count;
    }

    private void Cancel(Order order, CheckoutSagaState saga, string reason)
    {
        if (!order.Cancel(reason)) return;
        saga.Compensate(reason);
        outbox.Enqueue(order.StatusChanged());
        // Inventory trả hàng giữ, Discount hoàn lượt coupon, FlashSale trả quota — mỗi bên tự nghe
        outbox.Enqueue(order.Cancelled());
    }

    private async Task<(Order Order, CheckoutSagaState? Saga)> LoadAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct)
                    ?? throw new InvalidOperationException($"Không có order {orderId}");
        var saga = await db.Sagas.FirstOrDefaultAsync(s => s.OrderId == orderId, ct);
        return (order, saga);
    }
}
```

#### `src/Services/Ordering.Api/Sagas/CheckoutSagaState.cs`

```csharp
using BuildingBlocks.Persistence;

namespace Ordering.Api.Sagas;

public enum SagaStep { AwaitingStock = 0, AwaitingPayment = 1, Completed = 2, Compensated = 3 }

/// <summary>
/// Trạng thái của saga checkout cho một đơn. Version = xmin: hai event đến cùng lúc cho cùng đơn
/// thì một bên bị DbUpdateConcurrencyException và được consumer host retry với dữ liệu mới.
/// </summary>
public sealed class CheckoutSagaState : Auditable
{
    public Guid OrderId { get; private set; }
    public SagaStep Step { get; private set; } = SagaStep.AwaitingStock;
    public Guid? PaymentId { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset PaymentDeadline { get; private set; }
    public uint Version { get; private set; }

    private CheckoutSagaState() { }

    public static CheckoutSagaState Start(Guid orderId, DateTimeOffset deadline, bool stockAlreadyReserved = false) => new()
    {
        OrderId = orderId,
        PaymentDeadline = deadline,
        Step = stockAlreadyReserved ? SagaStep.AwaitingPayment : SagaStep.AwaitingStock
    };

    public void StockReserved() => Step = SagaStep.AwaitingPayment;

    public void Paid(Guid paymentId)
    {
        Step = SagaStep.Completed;
        PaymentId = paymentId;
    }

    public void Compensate(string reason)
    {
        Step = SagaStep.Compensated;
        LastError = reason;
    }

    public bool IsFinished => Step is SagaStep.Completed or SagaStep.Compensated;
}
```

#### `src/Services/Ordering.Api/Data/OrderingDbContext.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Ordering.Api.Domain;
using Ordering.Api.Sagas;

namespace Ordering.Api.Data;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<CheckoutSagaState> Sagas => Set<CheckoutSagaState>();

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
            e.Property(x => x.CustomerEmail).HasMaxLength(256);
            e.Property(x => x.ShippingAddress).HasMaxLength(500);
            e.Property(x => x.CancelReason).HasMaxLength(300);
            e.Property(x => x.IdempotencyKey).HasMaxLength(150);
            e.Property(x => x.Version).IsConcurrencyToken();
            // chốt chặn cuối cho gửi đôi: cùng khoá thì DB từ chối order thứ hai
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

        // S3
        b.Entity<CheckoutSagaState>(e =>
        {
            e.ToTable("checkout_saga_states");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Step).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => new { x.Step, x.PaymentDeadline });
            e.Ignore(x => x.IsFinished);
        });
        b.AddOutboxInbox();

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

#### `src/Services/Ordering.Api/EventHandlers/SagaEventHandlers.cs`

```csharp
using BuildingBlocks.Messaging;
using Contracts.Inventory;
using Contracts.Payments;
using Ordering.Api.Sagas;

namespace Ordering.Api.EventHandlers;

// Handler mỏng: chỉ chuyển event vào đúng bước của saga.

public sealed class StockReservedHandler(CheckoutSaga saga) : IEventHandler<StockReserved>
{
    public Task HandleAsync(StockReserved evt, CancellationToken ct) => saga.OnStockReservedAsync(evt.OrderId, ct);
}

public sealed class StockReservationFailedHandler(CheckoutSaga saga) : IEventHandler<StockReservationFailed>
{
    public Task HandleAsync(StockReservationFailed evt, CancellationToken ct) => saga.OnStockReservationFailedAsync(evt.OrderId, evt.Reason, ct);
}

public sealed class PaymentSucceededHandler(CheckoutSaga saga) : IEventHandler<PaymentSucceeded>
{
    public Task HandleAsync(PaymentSucceeded evt, CancellationToken ct) => saga.OnPaymentSucceededAsync(evt.OrderId, evt.PaymentId, ct);
}

public sealed class PaymentFailedHandler(CheckoutSaga saga) : IEventHandler<PaymentFailed>
{
    public Task HandleAsync(PaymentFailed evt, CancellationToken ct) => saga.OnPaymentFailedAsync(evt.OrderId, evt.Reason, ct);
}
```

### Buổi 8 — Checkout 202 + Idempotency-Key + job hết hạn + Payment

**Việc cần làm**

1. `IdempotencyFilter` (BuildingBlocks): bắt buộc header, Redis lưu response 24h, request song song cùng key → 409 đang xử lý.
2. `CheckoutService.PlaceAsync`: đọc giỏ/khách/giá (HTTP đọc vẫn ổn) → Order + Saga + OrderCreated một transaction; trùng key → trả đơn cũ.
3. `ExpireUnpaidOrdersJob` (Hangfire, mỗi phút) gọi `saga.ExpireUnpaidAsync`.
4. Payment: PaymentRequested → charge → PaymentSucceeded/Failed; OrderCancelled → huỷ payment chờ; PaymentRefundRequested → hoàn tiền.

**Lệnh**

```bash
dotnet add src/Services/Ordering.Api package Hangfire.AspNetCore
dotnet add src/Services/Ordering.Api package Hangfire.PostgreSql
```

**File**

- `src/BuildingBlocks/Idempotency/IdempotencyFilter.cs`
- `src/Services/Ordering.Api/Clients/ServiceClients.cs` *(sửa)*
- `src/Services/Ordering.Api/Features/Checkout/CheckoutService.cs`
- `src/Services/Ordering.Api/Features/Checkout/PlaceOrder.cs` *(sửa)*
- `src/Services/Ordering.Api/Jobs/ExpireUnpaidOrdersJob.cs`
- `src/Services/Ordering.Api/Ordering.Api.csproj` *(sửa)*
- `src/Services/Ordering.Api/Program.cs` *(sửa)*
- `src/Services/Ordering.Api/appsettings.Development.json`
- `src/Services/Ordering.Api/appsettings.json` *(sửa)*
- `src/Services/Payment.Api/Data/PaymentDbContext.cs` *(sửa)*
- `src/Services/Payment.Api/EventHandlers/PaymentEventHandlers.cs`
- `src/Services/Payment.Api/Program.cs` *(sửa)*

#### `src/BuildingBlocks/Idempotency/IdempotencyFilter.cs`

```csharp
using System.Text.Json;
using BuildingBlocks.Auth;
using BuildingBlocks.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace BuildingBlocks.Idempotency;

/// <summary>
/// Endpoint filter cho header Idempotency-Key:
///   - lần đầu: giữ chỗ khoá "processing" (SET NX, 24 giờ), chạy endpoint, lưu status + body
///   - lần sau cùng khoá: trả lại đúng response đã lưu, không chạy endpoint lần hai
///   - đang chạy dở: 409 IDEMPOTENCY_IN_PROGRESS
/// Khoá gắn với user để hai người dùng trùng khoá không thấy kết quả của nhau.
/// Dùng: app.MapPost(...).AddEndpointFilter&lt;IdempotencyFilter&gt;()
/// </summary>
public sealed class IdempotencyFilter(IConnectionMultiplexer redis) : IEndpointFilter
{
    public const string Header = "Idempotency-Key";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private const string Processing = "processing";

    private sealed record Stored(int Status, string Body);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var key = http.Request.Headers[Header].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            throw new BadRequestException("IDEMPOTENCY_KEY_REQUIRED", $"Thiếu header {Header} (tối đa 100 ký tự).");

        var user = http.RequestServices.GetRequiredService<ICurrentUser>();
        var owner = user.IsAuthenticated ? user.Id.ToString() : "anonymous";
        var redisKey = $"idem:{http.Request.Path}:{owner}:{key}";
        var db = redis.GetDatabase();

        if (!await db.StringSetAsync(redisKey, Processing, Ttl, When.NotExists))
        {
            var existing = await db.StringGetAsync(redisKey);
            if (existing == Processing)
                throw new ConflictException("IDEMPOTENCY_IN_PROGRESS", "Request cùng Idempotency-Key đang được xử lý.");
            var stored = JsonSerializer.Deserialize<Stored>(existing.ToString())!;
            http.Response.Headers["Idempotent-Replayed"] = "true";
            return Microsoft.AspNetCore.Http.Results.Text(stored.Body, "application/json", statusCode: stored.Status);
        }

        try
        {
            var result = await next(ctx);
            var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
            var value = (result as IValueHttpResult)?.Value ?? (result is IResult ? null : result);
            var body = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.StringSetAsync(redisKey, JsonSerializer.Serialize(new Stored(status, body)), Ttl);
            return result;
        }
        catch
        {
            // Lỗi thì nhả khoá để client thử lại được với cùng khoá
            await db.KeyDeleteAsync(redisKey);
            throw;
        }
    }
}
```

#### `src/Services/Ordering.Api/Clients/ServiceClients.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using System.Net.Http.Json;
using BuildingBlocks.Http;

namespace Ordering.Api.Clients;

// ---- DTO nhận về từ service khác (chỉ những trường Ordering cần) ----
public sealed record CartLineInfo(string Sku, string Name, string ProductSlug, int Qty, long UnitPrice, long LineTotal);
public sealed record CartInfo(string? CouponCode, List<CartLineInfo> Lines, int ItemCount, long Subtotal);
public sealed record CustomerStatusInfo(Guid Id, string Status, string? Email);
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

#### `src/Services/Ordering.Api/Features/Checkout/CheckoutService.cs`

```csharp
using BuildingBlocks.Messaging;
using BuildingBlocks.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ordering.Api.Clients;
using Ordering.Api.Data;
using Ordering.Api.Domain;
using Ordering.Api.Sagas;

namespace Ordering.Api.Features.Checkout;

public sealed class CheckoutOptions
{
    /// <summary>Quá hạn này mà chưa thanh toán thì đơn bị huỷ, hàng giữ được trả.</summary>
    public int PaymentDeadlineSeconds { get; set; } = 900;
}

/// <summary>
/// S3 — checkout bất đồng bộ. Request chỉ làm phần chắc chắn làm được ngay:
/// đọc giỏ + giá (đọc thì gọi HTTP vẫn ổn), tạo Order PendingStock + saga + event OrderCreated
/// trong MỘT transaction, rồi trả 202. Phần còn lại (giữ hàng, thanh toán) đi qua event.
/// Idempotency hai lớp: IdempotencyFilter trên Redis + unique index IdempotencyKey trong DB.
/// </summary>
public sealed class CheckoutService(
    OrderingDbContext db,
    OrderNumberGenerator numbers,
    CartClient cart,
    CustomerClient customers,
    CatalogClient catalog,
    IOutbox outbox,
    TimeProvider clock,
    IOptions<CheckoutOptions> options)
{
    public async Task<Order> PlaceAsync(Guid customerId, Guid? addressId, string idempotencyKey, CancellationToken ct)
    {
        var key = $"{customerId}:{idempotencyKey}";
        var existing = await FindByKeyAsync(key, ct);
        if (existing is not null) return existing;

        var cartInfo = await cart.GetAsync(customerId, ct);
        if (cartInfo.Lines.Count == 0) throw new BadRequestException("CART_EMPTY", "Giỏ hàng trống.");

        var status = await customers.GetStatusAsync(customerId, ct);
        if (status.Status == "Banned") throw new ForbiddenException("CUSTOMER_BANNED", "Tài khoản đang bị khoá.");
        var address = await customers.GetAddressAsync(customerId, addressId, ct);

        var variants = (await catalog.GetVariantsAsync(cartInfo.Lines.Select(l => l.Sku), ct)).ToDictionary(v => v.Sku);
        var lines = cartInfo.Lines.Select(l =>
        {
            if (!variants.TryGetValue(l.Sku, out var v) || !v.IsSellable)
                throw new BadRequestException("SKU_NOT_SELLABLE", $"{l.Sku} không còn bán, hãy xoá khỏi giỏ.");
            return OrderLine.Create(v.Sku, $"{v.ProductName} - {v.Name}", l.Qty, v.Price);
        }).ToList();

        var order = Order.Create(await numbers.NextAsync(ct: ct), customerId, SalesChannel.Online, lines,
            address?.ToString(), key, customerEmail: status.Email);
        db.Orders.Add(order);
        db.Sagas.Add(CheckoutSagaState.Start(order.Id, clock.GetUtcNow().AddSeconds(options.Value.PaymentDeadlineSeconds)));
        outbox.Enqueue(order.Created(requiresStock: true));
        outbox.Enqueue(order.StatusChanged());

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Request trùng khoá chạy song song và đã lưu trước: trả về đơn đó
            db.ChangeTracker.Clear();
            return await FindByKeyAsync(key, ct) ?? throw new InvalidOperationException("Trùng IdempotencyKey nhưng không tìm thấy đơn.", ex);
        }
        return order;
    }

    private Task<Order?> FindByKeyAsync(string key, CancellationToken ct) =>
        db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct);
}
```

#### `src/Services/Ordering.Api/Features/Checkout/PlaceOrder.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Auth;
using BuildingBlocks.Endpoints;
using BuildingBlocks.Idempotency;
using BuildingBlocks.Results;

namespace Ordering.Api.Features.Checkout;

public sealed record PlaceOrderRequest(Guid? AddressId);
public sealed record PlaceOrderResponse(Guid OrderId, string Number, string Status);

/// <summary>
/// POST /checkout — từ S3 trả 202 Accepted ngay khi đơn được ghi nhận.
/// Client hỏi GET /orders/{id} để biết đơn đã thanh toán hay bị huỷ.
/// Bắt buộc header Idempotency-Key: bấm hai lần chỉ ra một đơn.
/// Bản đồng bộ của S2 nằm trong lịch sử git (tag s2-done).
/// </summary>
public sealed class PlaceOrder : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/checkout", async (HttpContext http, PlaceOrderRequest req, ICurrentUser user, CheckoutService checkout, CancellationToken ct) =>
            {
                var key = http.Request.Headers[IdempotencyFilter.Header].ToString();
                var order = await checkout.PlaceAsync(user.Id, req.AddressId, key, ct);
                return Results.Accepted($"/orders/{order.Id}", new PlaceOrderResponse(order.Id, order.Number, order.Status.ToString()));
            })
            .AddEndpointFilter<IdempotencyFilter>()
            .RequireAuthorization(Policies.Authenticated)
            .WithTags("Checkout");
}
```

#### `src/Services/Ordering.Api/Jobs/ExpireUnpaidOrdersJob.cs`

```csharp
using Ordering.Api.Data;
using Ordering.Api.Sagas;

namespace Ordering.Api.Jobs;

/// <summary>
/// Hangfire recurring job mỗi phút: huỷ đơn quá PaymentDeadline (mặc định 15 phút; flash sale 10 phút).
/// Quét định kỳ thay vì hẹn một job cho từng đơn: không mất hẹn khi ghi Hangfire lỗi sau khi đã lưu đơn.
/// Hai server Hangfire cùng chạy thì xmin của saga chặn huỷ hai lần.
/// </summary>
public sealed class ExpireUnpaidOrdersJob(CheckoutSaga saga, OrderingDbContext db, ILogger<ExpireUnpaidOrdersJob> logger)
{
    public const string Id = "ordering-expire-unpaid";

    public async Task RunAsync(CancellationToken ct)
    {
        var count = await saga.ExpireUnpaidAsync(ct);
        if (count == 0) return;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Đã huỷ {Count} đơn quá hạn thanh toán", count);
    }
}
```

#### `src/Services/Ordering.Api/Ordering.Api.csproj` *(sửa — nội dung đầy đủ sau khi sửa)*

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
    <PackageReference Include="Hangfire.AspNetCore" />
    <PackageReference Include="Hangfire.PostgreSql" />
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

#### `src/Services/Ordering.Api/Program.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks;
using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using Contracts.Inventory;
using Contracts.Payments;
using Hangfire;
using Hangfire.PostgreSql;
using Ordering.Api.Clients;
using Ordering.Api.Data;
using Ordering.Api.EventHandlers;
using Ordering.Api.Features.Checkout;
using Ordering.Api.Jobs;
using Ordering.Api.Sagas;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddShopLabDbContext<OrderingDbContext>("orderingdb");
builder.AddRedisClient("redis"); // IdempotencyFilter
builder.Services.AddScoped<OrderNumberGenerator>();
builder.Services.Configure<CheckoutOptions>(builder.Configuration.GetSection("Checkout"));
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<CheckoutSaga>();
builder.Services.AddScoped<ExpireUnpaidOrdersJob>();

// Đọc (giỏ, giá, khách) vẫn gọi HTTP; ghi (giữ hàng, thanh toán) đi qua event từ S3.
builder.Services.AddHttpClient<CartClient>(c => c.BaseAddress = new Uri("https+http://cart"));
builder.Services.AddHttpClient<CustomerClient>(c => c.BaseAddress = new Uri("https+http://customer"));
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("https+http://catalog"));
builder.Services.AddHttpClient<InventoryClient>(c => c.BaseAddress = new Uri("https+http://inventory"));
builder.Services.AddHttpClient<PaymentClient>(c => c.BaseAddress = new Uri("https+http://payment"));

builder.AddShopLabMessaging("ordering")
    .AddOutbox<OrderingDbContext>()
    .AddConsumer<StockReserved, StockReservedHandler>(InboxKind.EntityFramework, typeof(OrderingDbContext))
    .AddConsumer<StockReservationFailed, StockReservationFailedHandler>(InboxKind.EntityFramework, typeof(OrderingDbContext))
    .AddConsumer<PaymentSucceeded, PaymentSucceededHandler>(InboxKind.EntityFramework, typeof(OrderingDbContext))
    .AddConsumer<PaymentFailed, PaymentFailedHandler>(InboxKind.EntityFramework, typeof(OrderingDbContext));

// Hangfire lưu job ở schema "hangfire" của orderingdb
if (!builder.Configuration.GetValue<bool>("Messaging:Disabled"))
{
    builder.Services.AddHangfire(c => c
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(builder.Configuration.GetConnectionString("orderingdb")),
            new PostgreSqlStorageOptions { SchemaName = "hangfire", PrepareSchemaIfNecessary = true }));
    builder.Services.AddHangfireServer(o => o.WorkerCount = 2);
}

var app = builder.Build();

await app.InitializeDatabaseAsync<OrderingDbContext>();
app.UseShopLabApi(typeof(Ordering.Api.ApiMarker).Assembly);

if (!app.Configuration.GetValue<bool>("Messaging:Disabled"))
{
    // Dashboard mở ở http://localhost:5106/hangfire (chỉ request từ máy local)
    app.MapHangfireDashboard("/hangfire");
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<ExpireUnpaidOrdersJob>(
        ExpireUnpaidOrdersJob.Id, j => j.RunAsync(CancellationToken.None), Cron.Minutely());
}

app.Run();
```

#### `src/Services/Ordering.Api/appsettings.Development.json`

```json
{
  "Checkout": { "PaymentDeadlineSeconds": 900 }
}
```

#### `src/Services/Ordering.Api/appsettings.json` *(sửa — nội dung đầy đủ sau khi sửa)*

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning", "Hangfire": "Warning" } },
  "AllowedHosts": "*",
  "Checkout": { "PaymentDeadlineSeconds": 900 }
}
```

#### `src/Services/Payment.Api/Data/PaymentDbContext.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Messaging;
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
        b.AddOutboxInbox(); // S3
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

#### `src/Services/Payment.Api/EventHandlers/PaymentEventHandlers.cs`

```csharp
using BuildingBlocks.Messaging;
using Contracts.Orders;
using Contracts.Payments;
using Microsoft.EntityFrameworkCore;
using Payment.Api.Data;
using Payment.Api.Domain;
using Payment.Api.FakeGateway;

namespace Payment.Api.EventHandlers;

/// <summary>
/// Saga bước 2: Ordering yêu cầu thanh toán → gọi cổng (giả) → báo kết quả.
/// OrderId unique: event giao lại hai lần cũng không trừ tiền hai lần, chỉ báo lại kết quả cũ.
/// </summary>
public sealed class PaymentRequestedHandler(PaymentDbContext db, FakePaymentProvider provider, IOutbox outbox, ILogger<PaymentRequestedHandler> logger)
    : IEventHandler<PaymentRequested>
{
    public async Task HandleAsync(PaymentRequested evt, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == evt.OrderId, ct);
        if (payment is null)
        {
            payment = Domain.Payment.Create(evt.OrderId, evt.Amount, PaymentMethod.Fake);
            db.Payments.Add(payment);
            var result = await provider.ChargeAsync(evt.OrderId, evt.Amount, ct);
            if (result.Success) payment.Succeed(result.ProviderRef!);
            else payment.Fail(result.Error!);
            logger.LogInformation("Thanh toán đơn {OrderId}: {Status}", evt.OrderId, payment.Status);
        }

        if (payment.Status == PaymentStatus.Succeeded)
            outbox.Enqueue(new PaymentSucceeded(evt.OrderId, payment.Id, payment.Amount) { CorrelationId = evt.OrderId });
        else if (payment.Status == PaymentStatus.Failed)
            outbox.Enqueue(new PaymentFailed(evt.OrderId, payment.FailureReason ?? "UNKNOWN") { CorrelationId = evt.OrderId });
    }
}

/// <summary>Đơn bị huỷ mà tiền đã trừ (hiếm, do đua giữa hết hạn và thanh toán) → hoàn tiền.</summary>
public sealed class OrderCancelledHandler(PaymentDbContext db, ILogger<OrderCancelledHandler> logger) : IEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled evt, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == evt.OrderId, ct);
        if (payment?.Refund() == true)
            logger.LogWarning("Hoàn tiền đơn {OrderId} vì đơn đã huỷ: {Reason}", evt.OrderId, evt.Reason);
    }
}

public sealed class PaymentRefundRequestedHandler(PaymentDbContext db, ILogger<PaymentRefundRequestedHandler> logger) : IEventHandler<PaymentRefundRequested>
{
    public async Task HandleAsync(PaymentRefundRequested evt, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == evt.OrderId, ct);
        if (payment?.Refund() == true)
            logger.LogWarning("Hoàn tiền đơn {OrderId}: {Reason}", evt.OrderId, evt.Reason);
    }
}
```

#### `src/Services/Payment.Api/Program.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks;
using BuildingBlocks.Chaos;
using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using Contracts.Orders;
using Contracts.Payments;
using Payment.Api.Data;
using Payment.Api.EventHandlers;
using Payment.Api.FakeGateway;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddChaos();
builder.AddShopLabDbContext<PaymentDbContext>("paymentdb");
builder.Services.Configure<FakePaymentOptions>(builder.Configuration.GetSection("FakePayment"));
builder.Services.AddSingleton<FakePaymentProvider>();

builder.AddShopLabMessaging("payment")
    .AddOutbox<PaymentDbContext>()
    .AddConsumer<PaymentRequested, PaymentRequestedHandler>(InboxKind.EntityFramework, typeof(PaymentDbContext), prefetch: 20)
    .AddConsumer<OrderCancelled, OrderCancelledHandler>(InboxKind.EntityFramework, typeof(PaymentDbContext))
    .AddConsumer<PaymentRefundRequested, PaymentRefundRequestedHandler>(InboxKind.EntityFramework, typeof(PaymentDbContext));

var app = builder.Build();

await app.InitializeDatabaseAsync<PaymentDbContext>();
app.UseChaos(app.Environment);
app.UseShopLabApi(typeof(Payment.Api.ApiMarker).Assembly);

app.Run();
```

### Buổi 9 — Test saga + 3 kịch bản gãy giờ xanh

**Việc cần làm**

1. `OrderingFixture`: CheckoutService + CheckoutSaga trên Postgres thật, FakeTimeProvider, service khác giả bằng StubHandler.
2. `CheckoutSagaTests` 7 test, `CheckoutRecoveryTests` (Gay_1/2/3 + khách bị khoá).
3. Xoá `SyncCheckoutFailureTests.cs`.

**Lệnh**

```bash
git rm tests/Ordering.Tests/SyncCheckoutFailureTests.cs
dotnet test tests/Ordering.Tests
```

**Xoá file**

- `tests/Ordering.Tests/SyncCheckoutFailureTests.cs`

**File**

- `tests/Ordering.Tests/CheckoutRecoveryTests.cs`
- `tests/Ordering.Tests/CheckoutSagaTests.cs`
- `tests/Ordering.Tests/OrderingFixture.cs`

#### `tests/Ordering.Tests/CheckoutRecoveryTests.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Ordering.Api.Domain;
using TestKit;

namespace Ordering.Tests;

/// <summary>
/// Ba chỗ gãy của checkout đồng bộ ở S2 (xem docs/notes/s2-failures.md), viết lại cho luồng saga của S3.
/// Ở S2 các test này nằm trong SyncCheckoutFailureTests và bị Skip vì đỏ; giờ chúng phải xanh.
/// </summary>
public sealed class CheckoutRecoveryTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Gay_3_double_submit_with_same_key_creates_one_order()
    {
        var t = await OrderingTestContext.CreateAsync(pg);

        var ids = await Task.WhenAll(t.PlaceAsync("same-key"), t.PlaceAsync("same-key"), t.PlaceAsync("same-key"));

        ids.Distinct().Count().ShouldBe(1);
        await using var db = t.NewDb();
        (await db.Orders.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Gay_1_inventory_down_leaves_order_pending_then_job_cancels_it()
    {
        // Inventory chết: OrderCreated nằm trong outbox/queue, không ai trả lời. Đơn không kẹt mãi:
        // khi Inventory sống lại thì xử lý tiếp; nếu quá hạn thì job huỷ và phát OrderCancelled để trả hàng.
        var t = await OrderingTestContext.CreateAsync(pg);
        await t.PlaceAsync();

        t.Clock.Advance(TimeSpan.FromMinutes(16));
        await t.StepAsync(s => s.ExpireUnpaidAsync(default));

        await using var db = t.NewDb();
        (await db.Orders.SingleAsync()).Status.ShouldBe(OrderStatus.Cancelled);
        (await t.OutboxTypesAsync()).ShouldContain("orders.order_cancelled");
    }

    [Fact]
    public async Task Gay_2_slow_payment_never_leaves_customer_charged_for_cancelled_order()
    {
        // Payment trễ quá hạn: đơn bị huỷ trước, rồi PaymentSucceeded mới tới → saga yêu cầu hoàn tiền.
        var t = await OrderingTestContext.CreateAsync(pg);
        var orderId = await t.PlaceAsync();
        await t.StepAsync(s => s.OnStockReservedAsync(orderId, default));
        t.Clock.Advance(TimeSpan.FromMinutes(20));
        await t.StepAsync(s => s.ExpireUnpaidAsync(default));

        await t.StepAsync(s => s.OnPaymentSucceededAsync(orderId, Guid.NewGuid(), default));

        (await t.OutboxTypesAsync()).ShouldContain("payments.refund_requested");
    }

    [Fact]
    public async Task Banned_customer_cannot_place_order()
    {
        var stub = OrderingTestContext.HappyStub()
            .OnJson("GET", "/internal/customers/", new { id = TestUsers.Chi, status = "Banned", email = "chi.le@shoplab.local" });
        var t = await OrderingTestContext.CreateAsync(pg, stub);

        var ex = await Should.ThrowAsync<BuildingBlocks.Results.ForbiddenException>(() => t.PlaceAsync());
        ex.Code.ShouldBe("CUSTOMER_BANNED");
    }
}
```

#### `tests/Ordering.Tests/CheckoutSagaTests.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Ordering.Api.Domain;
using Ordering.Api.Sagas;
using TestKit;

namespace Ordering.Tests;

/// <summary>
/// Mọi nhánh của saga checkout, chạy trên Postgres thật nhưng không cần RabbitMQ:
/// gọi từng bước như consumer host và kiểm tra event được enqueue vào outbox.
/// </summary>
public sealed class CheckoutSagaTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Place_order_creates_pending_order_saga_and_OrderCreated_in_one_transaction()
    {
        var t = await OrderingTestContext.CreateAsync(pg);
        var orderId = await t.PlaceAsync();

        await using var db = t.NewDb();
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        order.Id.ShouldBe(orderId);
        order.Status.ShouldBe(OrderStatus.PendingStock);
        order.Total.ShouldBe(398_000);
        (await db.Sagas.SingleAsync()).Step.ShouldBe(SagaStep.AwaitingStock);
        (await t.OutboxTypesAsync()).ShouldBe(["orders.order_created", "orders.order_status_changed"]);
    }

    [Fact]
    public async Task Happy_path_ends_paid_and_publishes_OrderPaid()
    {
        var t = await OrderingTestContext.CreateAsync(pg);
        var orderId = await t.PlaceAsync();

        await t.StepAsync(s => s.OnStockReservedAsync(orderId, default));
        await t.StepAsync(s => s.OnPaymentSucceededAsync(orderId, Guid.NewGuid(), default));

        await using var db = t.NewDb();
        (await db.Orders.SingleAsync()).Status.ShouldBe(OrderStatus.Paid);
        (await db.Sagas.SingleAsync()).Step.ShouldBe(SagaStep.Completed);
        var types = await t.OutboxTypesAsync();
        types.ShouldContain("payments.payment_requested");
        types.Last().ShouldBe("orders.order_paid");
    }

    [Fact]
    public async Task Out_of_stock_cancels_and_publishes_OrderCancelled()
    {
        var t = await OrderingTestContext.CreateAsync(pg);
        var orderId = await t.PlaceAsync();

        await t.StepAsync(s => s.OnStockReservationFailedAsync(orderId, "SL-TEE-BASIC-M chỉ còn 0", default));

        await using var db = t.NewDb();
        var order = await db.Orders.SingleAsync();
        order.Status.ShouldBe(OrderStatus.Cancelled);
        order.CancelReason!.ShouldContain("Hết hàng");
        (await t.OutboxTypesAsync()).Last().ShouldBe("orders.order_cancelled");
    }

    [Fact]
    public async Task Payment_failure_cancels_so_inventory_releases_stock()
    {
        var t = await OrderingTestContext.CreateAsync(pg);
        var orderId = await t.PlaceAsync();
        await t.StepAsync(s => s.OnStockReservedAsync(orderId, default));

        await t.StepAsync(s => s.OnPaymentFailedAsync(orderId, "CARD_DECLINED", default));

        await using var db = t.NewDb();
        (await db.Orders.SingleAsync()).Status.ShouldBe(OrderStatus.Cancelled);
        (await db.Sagas.SingleAsync()).Step.ShouldBe(SagaStep.Compensated);
        (await t.OutboxTypesAsync()).Last().ShouldBe("orders.order_cancelled");
    }

    [Fact]
    public async Task Duplicate_events_are_ignored_once_saga_has_moved_on()
    {
        var t = await OrderingTestContext.CreateAsync(pg);
        var orderId = await t.PlaceAsync();
        await t.StepAsync(s => s.OnStockReservedAsync(orderId, default));
        var before = (await t.OutboxTypesAsync()).Count;

        await t.StepAsync(s => s.OnStockReservedAsync(orderId, default)); // giao lại

        (await t.OutboxTypesAsync()).Count.ShouldBe(before);
    }

    [Fact]
    public async Task Unpaid_order_past_deadline_is_cancelled_by_job()
    {
        var t = await OrderingTestContext.CreateAsync(pg);
        await t.PlaceAsync();
        await t.StepAsync(async s => (await s.ExpireUnpaidAsync(default)).ShouldBe(0)); // chưa hết hạn

        t.Clock.Advance(TimeSpan.FromMinutes(16));
        await t.StepAsync(async s => (await s.ExpireUnpaidAsync(default)).ShouldBe(1));

        await using var db = t.NewDb();
        var order = await db.Orders.SingleAsync();
        order.Status.ShouldBe(OrderStatus.Cancelled);
        order.CancelReason.ShouldBe("Hết hạn thanh toán");
    }

    [Fact]
    public async Task Late_payment_after_cancellation_requests_a_refund()
    {
        var t = await OrderingTestContext.CreateAsync(pg);
        var orderId = await t.PlaceAsync();
        await t.StepAsync(s => s.OnStockReservedAsync(orderId, default));
        t.Clock.Advance(TimeSpan.FromMinutes(16));
        await t.StepAsync(s => s.ExpireUnpaidAsync(default));

        await t.StepAsync(s => s.OnPaymentSucceededAsync(orderId, Guid.NewGuid(), default));

        await using var db = t.NewDb();
        (await db.Orders.SingleAsync()).Status.ShouldBe(OrderStatus.Cancelled);
        (await t.OutboxTypesAsync()).Last().ShouldBe("payments.refund_requested");
    }
}
```

#### `tests/Ordering.Tests/OrderingFixture.cs`

```csharp
using System.Net;
using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ordering.Api.Clients;
using Ordering.Api.Data;
using Ordering.Api.Features.Checkout;
using Ordering.Api.Sagas;
using TestKit;

namespace Ordering.Tests;

/// <summary>Dựng CheckoutService + CheckoutSaga trên Postgres thật, các service khác giả bằng StubHandler.</summary>
internal sealed class OrderingTestContext
{
    public required Func<OrderingDbContext> NewDb { get; init; }
    public required FakeTimeProvider Clock { get; init; }
    public required StubHandler Stub { get; init; }

    public static readonly object Cart = new
    {
        couponCode = (string?)null,
        lines = new[] { new { sku = "SL-TEE-BASIC-M", name = "Áo", productSlug = "ao", qty = 2, unitPrice = 199000, lineTotal = 398000 } },
        itemCount = 2, subtotal = 398000
    };

    public static StubHandler HappyStub() => new StubHandler()
        .OnJson("GET", "/internal/carts/", Cart)
        .OnJson("GET", "/internal/customers/", new { id = TestUsers.An, status = "Active", email = "an.nguyen@shoplab.local" })
        .OnJson("POST", "/internal/variants/batch", new[]
        {
            new { sku = "SL-TEE-BASIC-M", name = "M", productName = "Áo thun", productSlug = "ao", categoryId = Guid.NewGuid(),
                  categorySlug = "thoi-trang", price = 199000, compareAtPrice = (long?)null, isSellable = true }
        });

    public static async Task<OrderingTestContext> CreateAsync(PostgresFixture pg, StubHandler? stub = null)
    {
        var cs = await pg.CreateDatabaseAsync("ordering");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 2, 0, 0, TimeSpan.Zero));
        OrderingDbContext New() => new(new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(cs).UseSnakeCaseNamingConvention()
            .AddInterceptors(new AuditInterceptor(clock)).Options);
        await using (var db = New()) await db.Database.EnsureCreatedAsync();
        return new OrderingTestContext { NewDb = New, Clock = clock, Stub = stub ?? HappyStub() };
    }

    public CheckoutService Checkout(OrderingDbContext db)
    {
        HttpClient Http() => new(Stub) { BaseAddress = new Uri("http://stub") };
        return new CheckoutService(db, new OrderNumberGenerator(db, Clock),
            new CartClient(Http()), new CustomerClient(Http()), new CatalogClient(Http()),
            new EfOutbox<OrderingDbContext>(db, new EventTypeRegistry()), Clock,
            Options.Create(new CheckoutOptions { PaymentDeadlineSeconds = 900 }));
    }

    public CheckoutSaga Saga(OrderingDbContext db) =>
        new(db, new EfOutbox<OrderingDbContext>(db, new EventTypeRegistry()), Clock, NullLogger<CheckoutSaga>.Instance);

    /// <summary>Tên các event đang chờ trong outbox, theo thứ tự ghi.</summary>
    public async Task<List<string>> OutboxTypesAsync()
    {
        await using var db = NewDb();
        return await db.Set<OutboxMessage>().OrderBy(m => m.Id).Select(m => m.Type).ToListAsync();
    }

    public async Task<Guid> PlaceAsync(string key = "k-1")
    {
        await using var db = NewDb();
        var order = await Checkout(db).PlaceAsync(TestUsers.An, null, key, CancellationToken.None);
        return order.Id;
    }

    /// <summary>Chạy một bước saga như consumer host: handler rồi SaveChanges.</summary>
    public async Task StepAsync(Func<CheckoutSaga, Task> step)
    {
        await using var db = NewDb();
        await step(Saga(db));
        await db.SaveChangesAsync();
    }
}

internal static class HttpStatus
{
    public static HttpResponseMessage Unavailable() => new(HttpStatusCode.ServiceUnavailable);
}
```

**Kiểm tra cuối buổi**

```bash
dotnet test tests/Ordering.Tests   # 11 test xanh
```

**Xong tuần 2 khi:**

- [ ] Saga test xanh
- [ ] Đặt hàng qua curl: 202 rồi Paid trong vài giây

---

## Tuần 3 — Hệ quả của OrderPaid, UI async, đo và phá thử

**Mục tiêu tuần**

- Cart xoá giỏ, Customer cộng điểm (inbox bắt buộc), Notification gửi mail qua Mailpit.
- Storefront: checkout gửi Idempotency-Key, trang kết quả hỏi trạng thái tới khi Paid/Cancelled.
- 1000 checkout k6 không đơn kẹt; tắt RabbitMQ khi đang ghi vẫn không mất event.

### Buổi 10 — Cart, Customer, Notification nghe OrderPaid + AppHost

**Việc cần làm**

1. Cart: xoá giỏ (inbox Redis). Customer: +1 điểm / 10.000 ₫ (inbox EF — thiếu inbox là cộng hai lần).
2. `Notification.Worker` (Worker SDK): template Liquid (Fluid), gửi SMTP bằng MailKit tới Mailpit.
3. AppHost bản đầy đủ: RabbitMQ, Mailpit (8025/1025), Notification.

**Lệnh**

```bash
dotnet new worker -o src/Services/Notification.Worker   # rồi thay nội dung theo file bên dưới
python3 tools/sln-sync.py
aspire run --project src/AppHost
```

**File**

- `src/Services/Cart.Api/EventHandlers/OrderPaidHandler.cs`
- `src/Services/Cart.Api/Program.cs` *(sửa)*
- `src/Services/Customer.Api/EventHandlers/OrderPaidHandler.cs`
- `src/Services/Customer.Api/Features/Internal/InternalCustomers.cs` *(sửa)*
- `src/Services/Customer.Api/Program.cs` *(sửa)*
- `src/Services/Notification.Worker/Handlers/OrderPaidHandler.cs`
- `src/Services/Notification.Worker/Notification.Worker.csproj`
- `src/Services/Notification.Worker/Program.cs`
- `src/Services/Notification.Worker/Properties/launchSettings.json`
- `src/Services/Notification.Worker/Templates/order-paid.liquid`
- `src/Services/Notification.Worker/appsettings.json`
- `src/AppHost/AppHost.cs` *(sửa)*
- `src/AppHost/AppHost.csproj` *(sửa)*
- `shoplab.slnx` *(sửa)*

#### `src/Services/Cart.Api/EventHandlers/OrderPaidHandler.cs`

```csharp
using BuildingBlocks.Messaging;
using Cart.Api.Data;
using Contracts.Orders;

namespace Cart.Api.EventHandlers;

/// <summary>Đơn online đã thanh toán → xoá giỏ của khách. Chống trùng bằng khoá Redis (Cart không có Postgres).</summary>
public sealed class OrderPaidHandler(ICartStore store) : IEventHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid evt, CancellationToken ct)
    {
        if (evt.Channel != "Online" || evt.CustomerId is not { } customerId) return;
        await store.DeleteAsync(RedisCartStore.UserKey(customerId));
    }
}
```

#### `src/Services/Cart.Api/Program.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks;
using BuildingBlocks.Messaging;
using Cart.Api.EventHandlers;
using Contracts.Orders;
using Cart.Api.Clients;
using Cart.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddRedisClient("redis");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICartStore, RedisCartStore>();
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("https+http://catalog"));

// S3: xoá giỏ khi đơn đã thanh toán
builder.AddShopLabMessaging("cart")
    .AddConsumer<OrderPaid, OrderPaidHandler>(InboxKind.Redis);

var app = builder.Build();

app.UseShopLabApi(typeof(Cart.Api.ApiMarker).Assembly);

app.Run();
```

#### `src/Services/Customer.Api/EventHandlers/OrderPaidHandler.cs`

```csharp
using BuildingBlocks.Messaging;
using Contracts.Orders;
using Customer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.EventHandlers;

/// <summary>
/// Cộng 1 điểm mỗi 10.000 ₫. Đây là projection cộng dồn nên BẮT BUỘC có inbox:
/// thiếu inbox thì event giao lại hai lần sẽ cộng điểm hai lần.
/// </summary>
public sealed class OrderPaidHandler(CustomerDbContext db, ILogger<OrderPaidHandler> logger) : IEventHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid evt, CancellationToken ct)
    {
        if (evt.CustomerId is not { } customerId) return; // khách vãng lai ở POS
        var loyalty = await db.Loyalty.FirstOrDefaultAsync(l => l.CustomerId == customerId, ct);
        if (loyalty is null) return; // khách chưa có hồ sơ (vd user ảo của k6)
        var earned = loyalty.AddPointsForAmount(evt.Total);
        logger.LogInformation("Khách {CustomerId} +{Points} điểm từ đơn {Number}", customerId, earned, evt.Number);
    }
}
```

#### `src/Services/Customer.Api/Features/Internal/InternalCustomers.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks.Endpoints;
using BuildingBlocks.Results;
using Customer.Api.Data;
using Customer.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Customer.Api.Features.Internal;

public sealed record CustomerStatusDto(Guid Id, string Status, string? Email);

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
            var row = await db.Customers.AsNoTracking().Where(c => c.Id == id)
                .Select(c => new { c.Status, c.Email }).FirstOrDefaultAsync(ct);
            return new CustomerStatusDto(id, (row?.Status ?? CustomerStatus.Active).ToString(), row?.Email);
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

#### `src/Services/Customer.Api/Program.cs` *(sửa — nội dung đầy đủ sau khi sửa)*

```csharp
using BuildingBlocks;
using BuildingBlocks.Messaging;
using Contracts.Orders;
using Customer.Api.EventHandlers;
using BuildingBlocks.Persistence;
using Customer.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddShopLabApi();
builder.AddShopLabDbContext<CustomerDbContext>("customerdb");

// S3: cộng điểm khi đơn đã thanh toán
builder.AddShopLabMessaging("customer")
    .AddConsumer<OrderPaid, OrderPaidHandler>(InboxKind.EntityFramework, typeof(CustomerDbContext));

var app = builder.Build();

await app.InitializeDatabaseAsync<CustomerDbContext>(SeedData.RunAsync);
app.UseShopLabApi(typeof(Customer.Api.ApiMarker).Assembly);

app.Run();
```

#### `src/Services/Notification.Worker/Handlers/OrderPaidHandler.cs`

```csharp
using System.Globalization;
using BuildingBlocks.Messaging;
using Contracts.Orders;
using Fluid;
using MailKit.Net.Smtp;
using MimeKit;

namespace Notification.Worker.Handlers;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string From { get; set; } = "ShopLab <no-reply@shoplab.local>";
}

/// <summary>Gửi email xác nhận khi đơn đã thanh toán. Chống gửi trùng bằng khoá Redis (worker không có DB).</summary>
public sealed class OrderPaidHandler(Microsoft.Extensions.Options.IOptions<SmtpOptions> smtp, ILogger<OrderPaidHandler> logger) : IEventHandler<OrderPaid>
{
    private static readonly FluidParser Parser = new();
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly IFluidTemplate Template = Parser.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates", "order-paid.liquid")));

    public async Task HandleAsync(OrderPaid evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.CustomerEmail))
        {
            logger.LogInformation("Đơn {Number} không có email khách, bỏ qua", evt.Number);
            return;
        }

        var model = new
        {
            number = evt.Number,
            paid_at = TimeZoneInfo.ConvertTime(evt.PaidAt, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")).ToString("dd/MM/yyyy HH:mm", Vi),
            total = Money(evt.Total),
            discount = Money(evt.DiscountTotal),
            lines = evt.Lines.Select(l => new { name = l.Name, qty = l.Qty, total = Money(l.UnitPrice * l.Qty - l.LineDiscount) }).ToList()
        };
        var options = new TemplateOptions { MemberAccessStrategy = new UnsafeMemberAccessStrategy() };
        var html = await Template.RenderAsync(new TemplateContext(model, options));

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(smtp.Value.From));
        message.To.Add(MailboxAddress.Parse(evt.CustomerEmail));
        message.Subject = $"ShopLab: đơn {evt.Number} đã thanh toán";
        message.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Value.Host, smtp.Value.Port, MailKit.Security.SecureSocketOptions.None, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
        logger.LogInformation("Đã gửi email đơn {Number} tới {Email}", evt.Number, evt.CustomerEmail);
    }

    private static string Money(long amount) => string.Format(Vi, "{0:N0} ₫", amount);
}
```

#### `src/Services/Notification.Worker/Notification.Worker.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <ItemGroup>
    <PackageReference Include="MailKit" />
    <PackageReference Include="Fluid.Core" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\ServiceDefaults\ServiceDefaults.csproj" />
    <ProjectReference Include="..\..\BuildingBlocks\BuildingBlocks.csproj" />
    <ProjectReference Include="..\..\Contracts\Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="Templates\*.liquid" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

#### `src/Services/Notification.Worker/Program.cs`

```csharp
using BuildingBlocks.Messaging;
using Contracts.Orders;
using Notification.Worker.Handlers;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient("redis"); // inbox chống gửi trùng
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));

builder.AddShopLabMessaging("notification")
    .AddConsumer<OrderPaid, OrderPaidHandler>(InboxKind.Redis);

builder.Build().Run();
```

#### `src/Services/Notification.Worker/Properties/launchSettings.json`

```json
{ "profiles": { "Worker": { "commandName": "Project", "environmentVariables": { "DOTNET_ENVIRONMENT": "Development" } } } }
```

#### `src/Services/Notification.Worker/Templates/order-paid.liquid`

```liquid
<h2>Cảm ơn bạn đã mua hàng tại ShopLab</h2>
<p>Đơn <strong>{{ number }}</strong> đã thanh toán lúc {{ paid_at }}.</p>
<table cellpadding="6" style="border-collapse: collapse">
  <tr><th align="left">Sản phẩm</th><th>SL</th><th align="right">Thành tiền</th></tr>
  {% for line in lines %}
  <tr><td>{{ line.name }}</td><td align="center">{{ line.qty }}</td><td align="right">{{ line.total }}</td></tr>
  {% endfor %}
</table>
{% if discount != "0 ₫" %}<p>Giảm giá: −{{ discount }}</p>{% endif %}
<p><strong>Tổng cộng: {{ total }}</strong></p>
<p style="color:#888">Email tự động từ lab ShopLab, gửi tới Mailpit.</p>
```

#### `src/Services/Notification.Worker/appsettings.json`

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "Smtp": { "Host": "localhost", "Port": 1025, "From": "ShopLab <no-reply@shoplab.local>" }
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

var rabbit = builder.AddRabbitMQ("rabbit")    // S3: event bus
    .WithDataVolume("shoplab-rabbit")
    .WithManagementPlugin();

// S3: bắt email gửi ra. UI http://localhost:8025, SMTP localhost:1025
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "latest")
    .WithHttpEndpoint(port: 8025, targetPort: 8025, name: "ui", isProxied: false)
    .WithEndpoint(port: 1025, targetPort: 1025, name: "smtp", isProxied: false);

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
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(catalogDb)
    .WithReference(rabbit).WaitFor(rabbit);

var customer = builder.AddProject<Projects.Customer_Api>("customer")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(customerDb)
    .WithReference(rabbit).WaitFor(rabbit);

var inventory = builder.AddProject<Projects.Inventory_Api>("inventory")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(inventoryDb)
    .WithReference(rabbit).WaitFor(rabbit);

var cart = builder.AddProject<Projects.Cart_Api>("cart")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak)
    .WithReference(redis).WaitFor(redis)
    .WithReference(rabbit).WaitFor(rabbit)
    .WithReference(catalog);

var payment = builder.AddProject<Projects.Payment_Api>("payment")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(paymentDb)
    .WithReference(rabbit).WaitFor(rabbit);

var ordering = builder.AddProject<Projects.Ordering_Api>("ordering")
    .WithShopLabEnvironment(env).WithKeycloak(keycloak).WithDatabase(orderingDb)
    .WithReference(redis).WaitFor(redis)
    .WithReference(rabbit).WaitFor(rabbit)
    .WithReference(cart).WithReference(customer).WithReference(catalog)
    .WithReference(inventory).WithReference(payment);

builder.AddProject<Projects.Notification_Worker>("notification")
    .WithReference(redis).WaitFor(redis)
    .WithReference(rabbit).WaitFor(rabbit)
    .WithEnvironment("Smtp__Host", "localhost")
    .WithEnvironment("Smtp__Port", "1025")
    .WaitFor(mailpit);

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
    <PackageReference Include="Aspire.Hosting.RabbitMQ" />
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
    <ProjectReference Include="..\Services\Notification.Worker\Notification.Worker.csproj" />
  </ItemGroup>
</Project>
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
    <Project Path="src/Services/Notification.Worker/Notification.Worker.csproj" />
    <Project Path="src/Services/Ordering.Api/Ordering.Api.csproj" />
    <Project Path="src/Services/Payment.Api/Payment.Api.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/BuildingBlocks.Tests/BuildingBlocks.Tests.csproj" />
    <Project Path="tests/Cart.Tests/Cart.Tests.csproj" />
    <Project Path="tests/Catalog.Tests/Catalog.Tests.csproj" />
    <Project Path="tests/Customer.Tests/Customer.Tests.csproj" />
    <Project Path="tests/Inventory.Tests/Inventory.Tests.csproj" />
    <Project Path="tests/Ordering.Tests/Ordering.Tests.csproj" />
    <Project Path="tests/TestKit/TestKit.csproj" />
  </Folder>
</Solution>
```

**Kiểm tra cuối buổi**

```bash
# đặt 1 đơn → http://localhost:8025 có mail; giỏ trống; /api/customer/me điểm tăng
```

### Buổi 11 — Storefront checkout async

**Việc cần làm**

1. `OrderingApi.checkout(body, idempotencyKey)`; một khoá cho một ý định đặt hàng, lỗi 4xx thì tạo khoá mới.
2. Trang kết quả hỏi `GET /orders/{id}` mỗi giây tới khi trạng thái cuối, hiện nhãn tiếng Việt.

**File**

- `src/Web/apps/storefront/src/app/pages/checkout/checkout.component.ts` *(sửa)*
- `src/Web/apps/storefront/src/app/pages/order-result/order-result.component.ts` *(sửa)*
- `src/Web/libs/api-client/src/lib/ordering.api.ts` *(sửa)*
- `src/Web/libs/api-client/src/lib/shop.models.ts` *(sửa)*

#### `src/Web/apps/storefront/src/app/pages/checkout/checkout.component.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

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
  /** Một khoá cho một lần "ý định đặt hàng": bấm lại (hoặc mạng chập chờn gửi lại) vẫn ra cùng một đơn. */
  private idempotencyKey = crypto.randomUUID();

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
    this.ordering.checkout({ addressId: this.addressId }, this.idempotencyKey).subscribe({
      next: (res) => this.router.navigate(['/orders', res.orderId, 'result']),
      error: (e) => {
        const p = readProblem(e);
        this.error.set((p.code && ERROR_TEXT[p.code]) || p.title);
        this.placing.set(false);
        // lỗi nghiệp vụ (400/403) thì lần bấm sau là ý định mới
        if (p.status >= 400 && p.status < 500) this.idempotencyKey = crypto.randomUUID();
      },
    });
  }
}
```

#### `src/Web/apps/storefront/src/app/pages/order-result/order-result.component.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
import { Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { switchMap, take, takeWhile, timer } from 'rxjs';
import { RouterLink } from '@angular/router';
import { CartStore } from '../../state/cart.store';
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

  private readonly destroyRef = inject(DestroyRef);
  private readonly cart = inject(CartStore);

  constructor() {
    // S3: đơn được xử lý bất đồng bộ → hỏi trạng thái mỗi giây tới khi Paid/Cancelled (tối đa 60 lần)
    effect(() => {
      const id = this.id();
      timer(0, 1000)
        .pipe(
          take(60),
          switchMap(() => this.api.get(id)),
          takeWhile((o) => o.status !== 'Paid' && o.status !== 'Cancelled', true),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe((o) => {
          this.order.set(o);
          if (o.status === 'Paid') this.cart.load();
        });
    });
  }
}
```

#### `src/Web/libs/api-client/src/lib/ordering.api.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

```typescript
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Paged } from './catalog.models';
import { toParams } from './http-params';
import { Order, OrderSummary, PlaceOrderResponse } from './shop.models';

@Injectable({ providedIn: 'root' })
export class OrderingApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/ordering';

  /** Từ S3: bắt buộc Idempotency-Key, trả 202 với orderId; hỏi get(orderId) để biết kết quả. */
  checkout(body: { addressId?: string | null }, idempotencyKey: string) {
    const headers = new HttpHeaders({ 'Idempotency-Key': idempotencyKey });
    return this.http.post<PlaceOrderResponse>(`${this.base}/checkout`, body, { headers });
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

#### `src/Web/libs/api-client/src/lib/shop.models.ts` *(sửa — nội dung đầy đủ sau khi sửa)*

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

export interface PlaceOrderResponse {
  orderId: string;
  number: string;
  status: OrderStatus;
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

### Buổi 12 — Đo tải và phá thử

**Việc cần làm**

1. `receipt-500.sh`: 500 phiếu nhập; giữa chừng Stop RabbitMQ 20s rồi Start — outbox dồn rồi xả hết.
2. `checkout-1000.js`: 1000 checkout, đếm Paid/Cancelled/kẹt, đo thời gian tới khi xong.
3. `check-reservations.sql`: đối chiếu Ordering ↔ Inventory (không đơn kẹt, reserved khớp, không âm).
4. Thử 3 kịch bản trong `http/gate-s3.http`, điền `docs/notes/s3-outbox.md`.

**Lệnh**

```bash
chmod +x tools/receipt-500.sh
./tools/receipt-500.sh
aspire run --project src/AppHost --launch-profile LoadTest
./tools/seed-loadtest.sh && k6 run load/checkout-1000.js
```

**File**

- `tools/receipt-500.sh`
- `tools/sql/check-reservations.sql`
- `load/checkout-1000.js`
- `docs/notes/s3-outbox.md`
- `http/gate-s3.http`

#### `tools/receipt-500.sh`

```bash
#!/usr/bin/env bash
# S3 — kiểm tra outbox dưới tải: nhập kho 500 phiếu liên tiếp, mỗi phiếu sinh 1 StockChanged.
# Sau đó so số event đã gửi (outbox processed) với số phiếu, và xem Catalog đã nhận đủ chưa.
#   ./tools/receipt-500.sh                 # 500 phiếu SL-NOTE-A5 vào WH-HCM
#   N=2000 SKU=SL-MOUSE-WL ./tools/receipt-500.sh
set -euo pipefail
GATEWAY="${GATEWAY:-http://localhost:5000}"
N="${N:-500}"
SKU="${SKU:-SL-NOTE-A5}"
LOC="${LOC:-WH-HCM}"
TOKEN="${TOKEN:-$(./tools/get-token.sh admin@shoplab.local)}"
RUN="R$(date +%H%M%S)"

echo "Gửi $N phiếu nhập ($RUN) cho $SKU @ $LOC …"
ok=0; fail=0
for i in $(seq 1 "$N"); do
  code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$GATEWAY/api/admin/inventory/stock/receipts" \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"sku\":\"$SKU\",\"locationCode\":\"$LOC\",\"qty\":1,\"refId\":\"$RUN-$i\"}")
  if [[ "$code" == "201" ]]; then ok=$((ok+1)); else fail=$((fail+1)); fi
  (( i % 100 == 0 )) && echo "  $i/$N (ok=$ok fail=$fail)"
done
echo "HTTP: ok=$ok fail=$fail"

cat <<SQL

Chạy trong pgAdmin (hoặc psql) để đối chiếu — tất cả phải bằng nhau sau vài giây:

-- inventorydb: số phiếu đã ghi sổ cái
SELECT count(*) FROM inventory.stock_movements WHERE ref_id LIKE '$RUN-%';
-- inventorydb: event StockChanged còn chờ gửi (phải về 0)
SELECT count(*) FROM inventory.outbox_messages WHERE processed_at IS NULL;
-- inventorydb: event gửi lỗi nhiều lần
SELECT id, type, attempts, last_error FROM inventory.outbox_messages WHERE attempts > 0 ORDER BY id DESC LIMIT 20;
-- catalogdb: Catalog đã xử lý StockChanged (inbox)
SELECT count(*) FROM catalog.inbox_messages WHERE consumer LIKE 'catalog.%';
SQL
echo
echo "Thử thêm: giữa chừng tắt RabbitMQ trên Aspire dashboard (Stop) 20 giây rồi Start lại."
echo "Kỳ vọng: HTTP vẫn 201, outbox dồn lên rồi tự xả hết, không mất event nào."
```

#### `tools/sql/check-reservations.sql`

```sql
-- S3 — đối chiếu Ordering và Inventory sau khi chạy load/checkout-1000.js.
-- Chạy từng khối trong đúng database (pgAdmin: chọn DB rồi Query Tool).

-- ===== orderingdb =====
-- 1. Phân bố trạng thái đơn
SELECT status, count(*) FROM ordering.orders GROUP BY status ORDER BY status;

-- 2. Đơn kẹt: PendingStock/PendingPayment quá hạn thanh toán mà job chưa huỷ (phải = 0 sau ~1 phút)
SELECT o.number, o.status, s.step, s.payment_deadline, s.last_error
FROM ordering.orders o JOIN ordering.checkout_saga_states s ON s.order_id = o.id
WHERE o.status IN ('PendingStock', 'PendingPayment') AND s.payment_deadline < now() - interval '1 minute';

-- 3. Event chưa gửi của Ordering (phải về 0)
SELECT type, count(*) FROM ordering.outbox_messages WHERE processed_at IS NULL GROUP BY type;

-- 4. Trùng Idempotency-Key (phải rỗng: unique index chặn)
SELECT idempotency_key, count(*) FROM ordering.orders WHERE idempotency_key IS NOT NULL
GROUP BY idempotency_key HAVING count(*) > 1;

-- ===== inventorydb =====
-- 5. Reservation còn Active quá hạn (phải = 0: OrderCancelled đã release)
SELECT order_id, sku, qty, expires_at FROM inventory.reservations
WHERE status = 'Active' AND expires_at < now() - interval '1 minute';

-- 6. Reserved trên stock_levels phải bằng tổng reservation Active
SELECT l.sku, l.location_id, l.reserved, coalesce(r.active, 0) AS active_reservations
FROM inventory.stock_levels l
LEFT JOIN (SELECT sku, location_id, sum(qty) AS active FROM inventory.reservations
           WHERE status = 'Active' GROUP BY sku, location_id) r
  ON r.sku = l.sku AND r.location_id = l.location_id
WHERE l.reserved <> coalesce(r.active, 0);

-- 7. Không bao giờ âm
SELECT sku, location_id, on_hand, reserved FROM inventory.stock_levels
WHERE on_hand < 0 OR reserved < 0 OR reserved > on_hand;

-- 8. So số lượng: tổng qty của reservation Committed = tổng qty đơn Paid (copy số từ orderingdb để so)
SELECT sum(qty) AS committed_qty FROM inventory.reservations WHERE status = 'Committed';
-- orderingdb: SELECT sum(l.qty) FROM ordering.order_lines l JOIN ordering.orders o ON o.id = l.order_id WHERE o.status = 'Paid';
```

#### `load/checkout-1000.js`

```javascript
// k6 — S3: 1000 checkout async. POST /checkout trả 202, client hỏi trạng thái tới khi Paid/Cancelled.
// Chạy: AppHost profile LoadTest, rồi
//   ./tools/seed-loadtest.sh
//   k6 run -e BASE=http://localhost:5000 load/checkout-1000.js
// Sau khi chạy: tools/sql/check-reservations.sql để đối chiếu.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

const settled = new Counter('orders_settled');
const paid = new Counter('orders_paid');
const cancelled = new Counter('orders_cancelled');
const stuck = new Counter('orders_stuck');
const timeToSettle = new Trend('order_time_to_settle', true);

export const options = {
  scenarios: {
    checkout: { executor: 'shared-iterations', vus: 100, iterations: 1000, maxDuration: '10m' },
  },
  thresholds: {
    'http_req_duration{name:checkout}': ['p(95)<500'], // chỉ ghi đơn + outbox, phải nhanh hơn S2 nhiều
    'http_req_failed{name:checkout}': ['rate<0.01'],
    orders_stuck: ['count==0'],
    order_time_to_settle: ['p(95)<5000'],
  },
};

const BASE = __ENV.BASE || 'http://localhost:5000';
const SKUS = ['SL-TEE-BASIC-M', 'SL-NOTE-A5', 'SL-MOUSE-WL', 'SL-BOTTLE-500-SLV'];
const FINAL = ['Paid', 'Cancelled', 'Completed'];

export default function () {
  const user = `loadtest-${__VU}-${__ITER}`;
  const headers = { 'Content-Type': 'application/json', 'X-Test-User': user };
  const sku = SKUS[Math.floor(Math.random() * SKUS.length)];

  const add = http.post(`${BASE}/api/cart/items`, JSON.stringify({ sku, qty: 1 }), { headers, tags: { name: 'add-to-cart' } });
  if (!check(add, { 'thêm giỏ 200': (r) => r.status === 200 })) return;

  const res = http.post(`${BASE}/api/ordering/checkout`, '{}', {
    headers: { ...headers, 'Idempotency-Key': uuidv4() },
    tags: { name: 'checkout' },
  });
  if (!check(res, { 'checkout 202': (r) => r.status === 202 })) return;

  const orderId = res.json('orderId');
  const started = Date.now();
  for (let i = 0; i < 60; i++) {
    sleep(0.5);
    const o = http.get(`${BASE}/api/ordering/orders/${orderId}`, { headers, tags: { name: 'poll-order' } });
    if (o.status !== 200) continue;
    const status = o.json('status');
    if (FINAL.includes(status)) {
      settled.add(1);
      timeToSettle.add(Date.now() - started);
      if (status === 'Paid' || status === 'Completed') paid.add(1);
      else cancelled.add(1);
      return;
    }
  }
  stuck.add(1); // 30 giây chưa xong: saga kẹt hoặc consumer chết
}
```

#### `docs/notes/s3-outbox.md`

````markdown
# S3 — Ghi chú: outbox, inbox, saga

Điền phần "Kết quả của tôi" khi chạy lab. Phần còn lại là tóm tắt thiết kế để đối chiếu với code.

## 1. Vì sao không publish thẳng sau SaveChanges

```
SaveChanges()  ✔
Publish()      ✘  (RabbitMQ đứt / process chết)   → đơn đã lưu, không ai biết → kẹt mãi
```

Outbox: event là một dòng trong `outbox_messages`, cùng transaction với dữ liệu nghiệp vụ
(`IOutbox.Enqueue` → `EfOutbox<T>` thêm entity vào DbContext đang dùng). `OutboxPublisher<T>` chạy nền:

1. `SELECT … FROM outbox_messages WHERE processed_at IS NULL AND (next_attempt_at IS NULL OR next_attempt_at <= now()) ORDER BY id LIMIT 50 FOR UPDATE SKIP LOCKED` (vòng lặp 500 ms)
2. Publish từng message (publisher confirms), lỗi thì `attempts++`, `next_attempt_at = now + backoff`.
3. Gửi xong đặt `processed_at`. `OutboxCleanup<T>` mỗi giờ xoá outbox đã gửi và inbox cũ hơn 7 ngày.

`SKIP LOCKED` cho phép chạy nhiều instance của cùng service mà không gửi trùng một batch.

## 2. At-least-once ⇒ consumer phải idempotent

RabbitMQ có thể giao lại (consumer chết trước khi ack, publisher gửi lại sau timeout confirm).
Mỗi consumer có inbox:

| Kiểu | Dùng ở | Cách chống trùng |
| --- | --- | --- |
| `InboxKind.EntityFramework` | Catalog, Customer, Inventory, Ordering, Payment | Insert `(event_id, consumer)` vào `inbox_messages` **cùng transaction** với thay đổi của handler; trùng khoá ⇒ bỏ qua |
| `InboxKind.Redis` | Cart, Notification | `SET inbox:{consumer}:{eventId} NX EX 7d` trước khi xử lý |
| `InboxKind.None` | handler tự idempotent | vd. release reservation theo orderId |

Inventory còn có lớp chống trùng thứ hai: unique index `(ref_type, ref_id, sku, type, location_id)` trên `stock_movements`.

## 3. Retry và DLQ

Handler ném lỗi → publish bản sao sang `<queue>.retry` với `Expiration` 1s, 5s, 25s (tối đa 30s) và header `x-retry-count`,
hết TTL thì dead-letter về `<queue>`.

- Lỗi tạm thời (`ITransientErrorClassifier`: timeout, Npgsql transient, xung đột version, RabbitMQ/Redis mất kết nối): retry **không giới hạn** — chờ hạ tầng sống lại.
- Lỗi khác (bug, dữ liệu sai): retry tới `MaxAttempts = 3` rồi vào `<queue>.dlq`, kèm header `x-last-error`.

Xem DLQ: RabbitMQ management → Queues → `*.dlq` → Get messages. Đẩy lại bằng shovel hoặc "Move messages".

## 4. Saga checkout (orchestration ở Ordering)

```
POST /checkout ─► Order(PendingStock) + Saga(AwaitingStock) + outbox OrderCreated      → 202
Inventory: OrderCreated ─► giữ hàng ─► StockReserved | StockReservationFailed
Ordering:  StockReserved ─► PendingPayment, outbox PaymentRequested
           StockReservationFailed ─► Cancelled, outbox OrderCancelled
Payment:   PaymentRequested ─► PaymentSucceeded | PaymentFailed
Ordering:  PaymentSucceeded ─► Paid, outbox OrderPaid
           PaymentFailed ─► Cancelled, outbox OrderCancelled  (Inventory release)
           PaymentSucceeded sau khi đã Cancelled ─► outbox PaymentRefundRequested
Job 1 phút: saga quá PaymentDeadline ─► Cancelled + OrderCancelled
OrderPaid ─► Inventory commit, Cart xoá giỏ, Customer cộng điểm, Notification gửi mail
```

Mỗi bước saga kiểm tra `Step` hiện tại trước khi chuyển; event tới sai thứ tự hoặc trùng ⇒ bỏ qua (test `Duplicate_events_are_ignored_once_saga_has_moved_on`).
`Version` (xmin) chặn hai event cùng lúc ghi đè nhau ⇒ một bên `DbUpdateConcurrencyException` ⇒ retry queue.

## 5. Ba kịch bản gãy của S2 — S3 sửa thế nào

| Kịch bản | S2 (đồng bộ) | S3 | Test |
| --- | --- | --- | --- |
| 1. Inventory chết sau khi giữ hàng | đơn kẹt / hàng giữ mãi | OrderCreated nằm trong queue; job huỷ đơn khi quá hạn; OrderCancelled release khi Inventory sống lại | `Gay_1_inventory_down_leaves_order_pending_then_job_cancels_it` |
| 2. Payment chậm hơn timeout | đơn bị huỷ nhưng tiền đã trừ | PaymentSucceeded muộn ⇒ PaymentRefundRequested | `Gay_2_slow_payment_never_leaves_customer_charged_for_cancelled_order` |
| 3. Double submit | 2 đơn | `Idempotency-Key` (Redis filter + unique index) | `Gay_3_double_submit_with_same_key_creates_one_order`, `tools/double-submit.sh` |

## 6. Kết quả của tôi

| Đo | S2 | S3 |
| --- | --- | --- |
| p95 POST /checkout (k6) | | |
| Lỗi HTTP checkout | | |
| Thời gian tới Paid p95 | — | |
| Đơn kẹt sau `check-reservations.sql` | | |
| `receipt-500.sh` tắt RabbitMQ 20s: event mất | — | |

Nhận xét:

-
````

#### `http/gate-s3.http`

```http
# Gate S3 — chạy lần lượt, đối chiếu với Aspire dashboard (Traces) và RabbitMQ management (http://localhost:15672).
@gateway = http://localhost:5000
@token = paste-customer-token
@admin = paste-admin-token
@payment = http://localhost:5106

### 1. Thêm giỏ
POST {{gateway}}/api/cart/items
Authorization: Bearer {{token}}
Content-Type: application/json

{ "sku": "SL-MOUSE-WL", "qty": 1 }

### 2. Checkout → 202 Accepted, status PendingStock, header Location
POST {{gateway}}/api/ordering/checkout
Authorization: Bearer {{token}}
Idempotency-Key: 6b1c2f2a-3333-4c4c-9999-000000000001
Content-Type: application/json

{ }

### 3. Gửi lại CÙNG key → 202 với CÙNG orderId (không tạo đơn thứ hai)
POST {{gateway}}/api/ordering/checkout
Authorization: Bearer {{token}}
Idempotency-Key: 6b1c2f2a-3333-4c4c-9999-000000000001
Content-Type: application/json

{ }

### 4. Thiếu Idempotency-Key → 400 IDEMPOTENCY_KEY_REQUIRED
POST {{gateway}}/api/ordering/checkout
Authorization: Bearer {{token}}
Content-Type: application/json

{ }

### 5. Hỏi trạng thái (dán orderId): PendingStock → PendingPayment → Paid trong vài giây
GET {{gateway}}/api/ordering/orders/paste-order-id
Authorization: Bearer {{token}}

### 6. Email xác nhận: mở http://localhost:8025 (Mailpit) — có thư "Đơn hàng … đã thanh toán"

### 7. Giỏ đã trống sau khi Paid (Cart nghe OrderPaid)
GET {{gateway}}/api/cart
Authorization: Bearer {{token}}

### 8. Điểm loyalty tăng (Customer nghe OrderPaid)
GET {{gateway}}/api/customer/me
Authorization: Bearer {{token}}

### 9. Tồn kho: reserved đã chuyển thành xuất kho
GET {{gateway}}/api/inventory/stock/SL-MOUSE-WL

### 10. Admin: đơn mới nhất
GET {{gateway}}/api/admin/ordering/orders
Authorization: Bearer {{admin}}

# ---- Kịch bản gãy (làm sau khi 1–10 xanh) ----
# A. Payment chậm: Payment.Api/appsettings.json đặt FakePayment:MinLatencyMs=MaxLatencyMs=60000,
#    Ordering.Api/appsettings.json đặt Checkout:PaymentDeadlineSeconds=30 → đơn Cancelled, sau đó PaymentSucceeded tới muộn
#    → Ordering phát PaymentRefundRequested, Payment hoàn tiền. Xem trong RabbitMQ: queue payment.paymentrefundrequested.
# B. Inventory chết: Stop "inventory" trên Aspire dashboard, checkout 3 đơn, đợi quá hạn → job huỷ đơn.
#    Start lại inventory → OrderCreated trong queue được xử lý, rồi OrderCancelled release lại hàng. Chạy tools/sql/check-reservations.sql.
# C. Consumer lỗi: Payment.Api FakePayment:FailureRate=1.0 → PaymentFailed → đơn Cancelled, hàng được release.
#    Lỗi tạm thời trong handler (vd Stop paymentdb) → message vào <queue>.retry rồi quay lại; quá 3 lần → <queue>.dlq.
```

**Kiểm tra cuối buổi**

```bash
dotnet test
git add -A && git commit -m "s3: event-driven" && git tag s3-done
```

**Xong tuần 3 khi:**

- [ ] `orders_stuck == 0`
- [ ] Không mất event khi tắt RabbitMQ
- [ ] Ghi chú S3 đầy đủ, tag `s3-done`

---

## Gate S3

| # | Kiểm tra |
| --- | --- |
| 1 | k6 1000 checkout: `orders_stuck == 0`, p95 `POST /checkout` < 500 ms |
| 2 | `tools/sql/check-reservations.sql`: mọi truy vấn kiểm tra trả rỗng |
| 3 | Tắt RabbitMQ 20s khi chạy `receipt-500.sh`: không mất event |
| 4 | Có mail trong Mailpit; trace một đơn đi qua Ordering → Inventory → Payment → Notification |
| 5 | `dotnet test` xanh |
