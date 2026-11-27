# Task 8 — Payment Link Creation: OperatorDirect Flow

Ngày: 25/11/2026. Branch: `fix/linhnv-payout-and-refund`.  
Nguồn kế hoạch: `plans/direct-operator-payment-refund-plan.md` Task 8.  
**Chưa thực thi. Phải duyệt spec này trước khi code.**

---

## 1. Mục tiêu

Mở rộng `CreateOrReusePaymentLinkCommandHandler` để khi `booking.PaymentFlow == OperatorDirect` thì tạo link payOS bằng **channel riêng của TO** (credentials mã hoá trong `OperatorPaymentChannel`), không dùng platform credentials. Các ràng buộc cứng:

| Rule | Nội dung |
|---|---|
| CR-01 | Không fallback sang platform channel khi kênh TO lỗi hoặc chưa sẵn sàng |
| CR-02 | Credentials TO không được đưa vào FE, log, hoặc response body |
| CR-03 | Link expiry = `min(booking.PaymentExpiresAtUtc, now + 15 phút)` cho OperatorDirect |
| CR-04 | Xác nhận thanh toán bằng webhook/API, không dựa vào return URL |
| CR-05 | Channel cũ (phiên bản ChannelVersion cũ) không bị thay đổi; chỉ giao dịch mới dùng channel hiện tại Ready |
| CR-06 | Không tự phát sinh giao dịch tiền thật ngoài luồng được phép riêng |

---

## 2. Hiện trạng (trước Task 8)

### 2.1 Handler hiện tại
`CreateOrReusePaymentLinkCommandHandler` inject một `IPaymentGateway paymentGateway` duy nhất (platform PayOS, singleton). Handler không đọc `booking.PaymentFlow` và không có logic nào phân biệt kênh TO.

### 2.2 Booking entity
`Booking.cs` **chưa có** property `PaymentFlow`. DB column `payment_flow` đã được tạo bởi migration Task 5 (giá trị `PlatformCollectedLegacy` cho tất cả 309 booking hiện tại), nhưng domain entity chưa map.

### 2.3 `IPaymentGateway`
Interface hiện có dùng `PaymentLinkRequest` không chứa credentials. `PayOsPaymentGateway` lấy credentials từ `PaymentOptions` (singleton config) — không thể dùng cho TO channel.

### 2.4 `OperatorPaymentChannel`
Entity đã có sau Task 7. Trường credential là encrypted refs (`ClientIdRef`, `ApiKeyRef`, `ChecksumKeyRef`). Chỉ channel ở trạng thái `StatusReady` mới được dùng để tạo link.

---

## 3. Các thay đổi cần thực hiện

### 3.1 Domain — thêm `PaymentFlow` vào `Booking`

**File**: `src/TripMate.Domain/Entities/Booking.cs`

Thêm property:
```csharp
public PaymentFlow PaymentFlow { get; private set; } = PaymentFlow.PlatformCollectedLegacy;
```

Thêm method (gọi từ booking creation handler khi TO có Ready channel):
```csharp
public void StampPaymentFlow(PaymentFlow flow)
{
    // Only settable before first payment attempt (PendingPayment + Unpaid).
    if (Status != BookingStatus.PendingPayment || PaymentStatus != PaymentStatus.Unpaid)
        throw new InvalidOperationException("PaymentFlow can only be set on a PendingPayment/Unpaid booking.");
    PaymentFlow = flow;
}
```

> **Ghi chú**: `StampPaymentFlow` được gọi từ `CreateBookingCommandHandler` khi tạo booking mới (ngoài scope Task 8 — xem §6). Task 8 chỉ cần EF map và đọc giá trị.

### 3.2 EF Configuration — map `payment_flow` column

**File**: `src/TripMate.Infrastructure/Persistence/Configurations/BookingConfiguration.cs`

Tìm đúng `IEntityTypeConfiguration<Booking>` và thêm:
```csharp
builder.Property(b => b.PaymentFlow)
    .HasColumnName("payment_flow")
    .HasConversion<string>()
    .IsRequired()
    .HasDefaultValue(PaymentFlow.PlatformCollectedLegacy);
```

Dùng `HasConversion<string>()` để khớp giá trị `'PlatformCollectedLegacy'`/`'OperatorDirect'` đã có trong DB.

### 3.3 Application — `IOperatorPaymentGatewayFactory`

**File mới**: `src/TripMate.Application/Features/Traveler/Payments/Common/IOperatorPaymentGatewayFactory.cs`

```csharp
namespace TripMate.Application.Features.Traveler.Payments.Common;

/// <summary>
/// Creates a per-call IPaymentGateway instance backed by a TO's decrypted payOS credentials.
/// </summary>
public interface IOperatorPaymentGatewayFactory
{
    IPaymentGateway Create(OperatorChannelSnapshot snapshot);
}

/// <summary>Decrypted, per-call credentials for one TO's payOS channel.</summary>
public sealed record OperatorChannelSnapshot(
    string ClientId,
    string ApiKey,
    string ChecksumKey,
    string ReturnUrl,
    string CancelUrl);
```

> **Luật bảo mật**: `OperatorChannelSnapshot` là in-process only — không log, không serialize ra HTTP response, không bao giờ đi vào DTO/command.

### 3.4 Infrastructure — `OperatorDirectPayOsGateway`

**File mới**: `src/TripMate.Infrastructure/Services/Payment/OperatorDirectPayOsGateway.cs`

Implements `IPaymentGateway`. Tương tự `PayOsPaymentGateway` nhưng:
- Nhận `HttpClient` + `OperatorChannelSnapshot` trong constructor (không dùng `PaymentOptions`)
- `Gateway` property trả về `PaymentGateway.PayOS` (cùng enum)
- Logic tạo link, cancel link, HMAC signature: giống `PayOsPaymentGateway` nhưng dùng `snapshot.ClientId`, `snapshot.ApiKey`, `snapshot.ChecksumKey`, `snapshot.ReturnUrl`, `snapshot.CancelUrl`
- `SubmitRefundAsync`: trả về cùng `manual_review_required` như `PayOsPaymentGateway`

**File mới**: `src/TripMate.Infrastructure/Services/Payment/OperatorPaymentGatewayFactory.cs`

```csharp
public sealed class OperatorPaymentGatewayFactory(IHttpClientFactory httpClientFactory)
    : IOperatorPaymentGatewayFactory
{
    public IPaymentGateway Create(OperatorChannelSnapshot snapshot)
        => new OperatorDirectPayOsGateway(
            httpClientFactory.CreateClient("payos-operator"),
            snapshot);
}
```

Đăng ký DI trong `DependencyInjection.cs`:
```csharp
services.AddScoped<IOperatorPaymentGatewayFactory, OperatorPaymentGatewayFactory>();
```

Named `HttpClient` `"payos-operator"` cần được đăng ký với `AddHttpClient("payos-operator")` không có base address (URL được hardcode trong `OperatorDirectPayOsGateway` như trong `PayOsPaymentGateway`).

### 3.5 Application — Sửa `CreateOrReusePaymentLinkCommandHandler`

**Thay đổi constructor**: Thêm inject `IPaymentCredentialProtector credentialProtector` và `IOperatorPaymentGatewayFactory operatorGatewayFactory`. Giữ nguyên `IPaymentGateway paymentGateway` cho platform flow.

**Thay đổi `ClaimPaymentTransactionAsync`**:

1. Load booking kèm Include chain:
```csharp
var booking = await dbContext.Bookings
    .Include(b => b.TourSchedule)
        .ThenInclude(ts => ts.Tour)
    .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
```

2. Sau khi pass tất cả booking validation, resolve gateway:
```csharp
var (gateway, channelError) = ResolveGateway(booking);
if (channelError is not null)
    return Result.Failure<PaymentLinkClaim>(channelError.Code, channelError.Message);
```

3. `ResolveGateway` (private method):
```csharp
private (IPaymentGateway? Gateway, (string Code, string Message)? Error) ResolveGateway(Booking booking)
{
    if (booking.PaymentFlow == PaymentFlow.PlatformCollectedLegacy)
        return (paymentGateway, null);

    // OperatorDirect: resolve TO's Ready channel
    var operatorUserId = booking.TourSchedule.Tour.OperatorUserId;
    var channel = dbContext.OperatorPaymentChannels
        .FirstOrDefault(c => c.TourOperatorUserId == operatorUserId
                          && c.Status == OperatorPaymentChannel.StatusReady);

    if (channel is null)
        return (null, (PaymentErrorCodes.ChannelNotReady,
            "The tour operator's payment channel is not ready. Please try again later or contact support."));

    string clientId, apiKey, checksumKey;
    try
    {
        clientId = credentialProtector.Unprotect(channel.ClientIdRef!);
        apiKey   = credentialProtector.Unprotect(channel.ApiKeyRef!);
        checksumKey = credentialProtector.Unprotect(channel.ChecksumKeyRef!);
    }
    catch (Exception ex)
    {
        // Credential refs are corrupted — operational error, not user error.
        logger.LogError(ex, "Failed to decrypt credentials for operator channel {ChannelId}", channel.Id);
        return (null, (PaymentErrorCodes.ChannelNotReady,
            "The tour operator's payment channel is temporarily unavailable."));
    }

    var snapshot = new OperatorChannelSnapshot(clientId, apiKey, checksumKey,
        returnUrl: ..., cancelUrl: ...); // see §3.6
    return (operatorGatewayFactory.Create(snapshot), null);
}
```

> `ResolveGateway` chạy **ngoài** serializable transaction. Channel lookup chỉ cần read-committed. Gateway instance được tạo in-process và không bao giờ được persist.

4. Truyền `gateway` qua `ClaimPaymentTransactionAsync` → `PaymentTransaction.Create`:
```csharp
var transaction = PaymentTransaction.Create(
    bookingId: booking.Id,
    gateway: resolvedGateway.Gateway,  // PaymentGateway.PayOS cho cả hai flow
    ...
);
```

**Thay đổi `GetLinkExpiry`**:

```csharp
private DateTimeOffset? GetLinkExpiry(Booking booking, DateTimeOffset now)
{
    var linkExpiry = booking.PaymentExpiresAtUtc;

    if (booking.PaymentFlow == PaymentFlow.OperatorDirect)
    {
        // CR-03: 15-minute real UTC cap for OperatorDirect (not configurable)
        var directCap = now.AddMinutes(15);
        if (!linkExpiry.HasValue || directCap < linkExpiry.Value)
            linkExpiry = directCap;
    }
    else if (paymentOptions.Mock.ExpiryMinutes > 0)
    {
        var mockExpiry = now.AddMinutes(paymentOptions.Mock.ExpiryMinutes);
        if (!linkExpiry.HasValue || mockExpiry < linkExpiry.Value)
            linkExpiry = mockExpiry;
    }

    return linkExpiry;
}
```

Signature thay đổi từ `(DateTimeOffset? bookingExpiry, DateTimeOffset now)` → `(Booking booking, DateTimeOffset now)`.

### 3.6 Return URL / Cancel URL cho OperatorDirect

Dùng cùng `paymentOptions.PayOs.ReturnUrl` và `paymentOptions.PayOs.CancelUrl` với `AppendBookingId`. Lý do: return URL chỉ dùng để redirect UX, không phải để xác nhận thanh toán (webhook làm điều đó). Điều này đơn giản hoá config và an toàn vì CR-04 đảm bảo xác nhận bằng webhook.

### 3.7 Lỗi mới cần thêm vào `PaymentErrorCodes`

**File**: `src/TripMate.Application/Features/Traveler/Payments/Common/PaymentErrorCodes.cs` (hoặc tương đương)

```csharp
public const string ChannelNotReady = "payment.channel_not_ready";
```

### 3.8 `IPaymentGateway` — không thay đổi interface

Interface `IPaymentGateway` giữ nguyên signature. `OperatorDirectPayOsGateway` implement đầy đủ; không cần thêm method hay property mới vào interface.

---

## 4. Luồng hoàn chỉnh sau Task 8

```
POST /api/v1/traveler/payments
  → CreateOrReusePaymentLinkCommand
  → Handler: load booking (Include TourSchedule.Tour)
  → Validate booking (owner, status, expiry)
  → ResolveGateway(booking)
      ├─ PaymentFlow.PlatformCollectedLegacy → IPaymentGateway (platform)
      └─ PaymentFlow.OperatorDirect
            → Load OperatorPaymentChannel WHERE TourOperatorUserId = ? AND Status = 'Ready'
            ├─ NOT FOUND → Failure "payment.channel_not_ready" (no fallback)
            └─ FOUND → Unprotect 3 credential refs → OperatorChannelSnapshot
                      → operatorGatewayFactory.Create(snapshot) → IPaymentGateway (TO)
  → ClaimPaymentTransactionAsync (serializable tx)
      → PaymentTransaction.Create(gateway: PaymentGateway.PayOS, ...)
      → persist Pending claim
  → resolvedGateway.CreatePaymentLinkAsync(...)
  → persist CheckoutUrl / QrCode
  → return CheckoutUrl
```

---

## 5. Idempotency

Giữ nguyên logic hiện tại:
- Nếu đã có `PaymentTransaction.Status = Pending` với `CheckoutUrl` hợp lệ và chưa hết hạn → trả lại link cũ (không tạo mới)
- Nếu Pending nhưng chưa có `CheckoutUrl` (đang tạo link) → trả lỗi "try again shortly"
- Nếu Pending nhưng đã hết hạn → expire, tạo mới
- Nếu link mới vừa tạo nhưng persist thất bại → cancel link tại TO's gateway (dùng `resolvedGateway.CancelPaymentLinkAsync`)

> **Quan trọng**: Khi cancel TO's link (do persist thất bại), phải dùng đúng `resolvedGateway` đã tạo link, không dùng platform gateway.

---

## 6. Ngoài scope Task 8 (ghi chú cho tasks sau)

### StampPaymentFlow tại booking creation
`CreateBookingCommandHandler` hiện tại cần được sửa (task riêng hoặc gộp vào Task 10) để:
1. Load `OperatorPaymentChannel` của TO (theo `TourSchedule.Tour.OperatorUserId`)
2. Nếu `Status == StatusReady` → `booking.StampPaymentFlow(PaymentFlow.OperatorDirect)`
3. Nếu không → `PaymentFlow.PlatformCollectedLegacy` (default)

Task 8 không sửa booking creation; toàn bộ 309 booking legacy giữ nguyên `PlatformCollectedLegacy`. Booking mới trong test được tạo thủ công với `PaymentFlow` đúng.

### Task 9 — Webhook
`ProcessPayOsWebhookCommand` sẽ cần phân biệt webhook từ platform channel vs TO channel. Task 8 không sửa webhook handler.

---

## 7. Unit Tests (Red → Green)

Tất cả test dùng `InMemory EF + FakeServices` như Task 7. Test file:  
`tests/TripMate.Application.UnitTests/Features/Traveler/Payments/CreateOrReusePaymentLinkHandlerTests.cs`

Thêm fake mới:
```csharp
public class FakeOperatorPaymentGatewayFactory : IOperatorPaymentGatewayFactory
{
    public OperatorChannelSnapshot? LastSnapshot { get; private set; }
    public IPaymentGateway GatewayToReturn { get; set; } = new FakePaymentGateway();

    public IPaymentGateway Create(OperatorChannelSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        return GatewayToReturn;
    }
}
```

| ID | Scenario | Expected |
|---|---|---|
| U1 | PlatformCollectedLegacy booking → tạo link thành công | Dùng platform gateway; `transaction.Gateway == PayOS` |
| U2 | OperatorDirect booking, channel Ready → tạo link thành công | Dùng TO gateway; `FakeFactory.LastSnapshot != null`; link trả về đúng |
| U3 | OperatorDirect booking, channel NOT FOUND → lỗi ngay | `payment.channel_not_ready`; không gọi gateway nào |
| U4 | OperatorDirect booking, channel Status = PendingVerification → lỗi ngay | `payment.channel_not_ready`; không gọi gateway nào |
| U5 | OperatorDirect booking, channel Status = ConnectionError → lỗi | `payment.channel_not_ready` |
| U6 | OperatorDirect booking, channel Status = Disabled → lỗi | `payment.channel_not_ready` |
| U7 | OperatorDirect, channel Ready, link tồn tại (Pending, có CheckoutUrl, chưa hết hạn) → reuse | Không gọi factory; trả CheckoutUrl cũ |
| U8 | OperatorDirect, link Pending nhưng hết hạn → expire + tạo mới | Gọi factory một lần; transaction mới |
| U9 | OperatorDirect, gateway call thất bại (throws) → lỗi `payment.gateway_unavailable` | Transaction marked Failed |
| U10 | OperatorDirect, link tạo thành công nhưng persist thất bại → cancel gọi TO gateway | `FakeFactory.GatewayToReturn` nhận `CancelPaymentLinkAsync` |
| U11 | OperatorDirect expiry = min(bookingExpiry, now+15min) khi bookingExpiry > now+15min | `transaction.ExpiresAtUtc <= now + 15min + 1s` |
| U12 | OperatorDirect expiry = bookingExpiry khi bookingExpiry < now+15min | `transaction.ExpiresAtUtc == bookingExpiry` |
| U13 | PlatformCollectedLegacy, Mock.ExpiryMinutes = 30 → expiry = now+30min | Không đổi từ hành vi cũ |
| U14 | OperatorDirect, Mock.ExpiryMinutes bị bỏ qua → luôn dùng 15 phút | `transaction.ExpiresAtUtc <= now + 15min + 1s` dù `Mock.ExpiryMinutes = 60` |
| U15 | Traveler không phải owner của booking → `payment.not_found` | Trả lỗi trước khi resolve gateway |
| U16 | Booking status != PendingPayment → `payment.booking_not_eligible` | Không gọi factory |
| U17 | Booking PaymentExpiresAtUtc đã qua → `payment.booking_expired` | Không gọi factory |

### Thay đổi test helper `SeedBooking`

Cần thêm tham số `paymentFlow = PlatformCollectedLegacy` vào helper, và helper tạo `Booking` phải gán navigation `TourSchedule.Tour.OperatorUserId` nếu test dùng OperatorDirect.

---

## 8. Integration Tests

File: `tests/TripMate.Api.IntegrationTests/DirectPayment/OperatorDirectPaymentLinkSqlServerTests.cs`

Dùng `[SqlServerFact]` + `TripMateApiFactory` như Task 7. Cần mock `IOperatorPaymentGatewayFactory` trong test host (không gọi payOS thật).

| ID | Scenario | Verify |
|---|---|---|
| I1 | Booking với PaymentFlow = OperatorDirect, channel Ready, POST /payments → 200 với CheckoutUrl | CheckoutUrl trả về, row `PaymentTransactions` có `gateway = 'PayOS'`, `booking.payment_flow = 'OperatorDirect'` |
| I2 | Booking OperatorDirect, channel Status = PendingVerification → 422/400 với `payment.channel_not_ready` | Không có row mới trong `PaymentTransactions` |
| I3 | Booking PlatformCollectedLegacy, channel bất kỳ → platform gateway được dùng | `FakeOperatorPaymentGatewayFactory.LastSnapshot` vẫn null |
| I4 | Booking OperatorDirect, link đã tồn tại (Pending có CheckoutUrl) → 200 reuse cùng link | Chỉ 1 row trong PaymentTransactions cho booking đó |

---

## 9. Files thay đổi

| File | Loại thay đổi |
|---|---|
| `src/TripMate.Domain/Entities/Booking.cs` | Thêm `PaymentFlow` property + `StampPaymentFlow` method |
| `src/TripMate.Infrastructure/Persistence/Configurations/BookingConfiguration.cs` | Thêm EF map `payment_flow` |
| `src/TripMate.Application/Features/Traveler/Payments/Common/IOperatorPaymentGatewayFactory.cs` | **MỚI** — interface + snapshot record |
| `src/TripMate.Application/Features/Traveler/Payments/Common/PaymentErrorCodes.cs` | Thêm `ChannelNotReady` |
| `src/TripMate.Application/Features/Traveler/Payments/Create/CreateOrReusePaymentLinkCommandHandler.cs` | Sửa handler: Include chain, ResolveGateway, GetLinkExpiry |
| `src/TripMate.Infrastructure/Services/Payment/OperatorDirectPayOsGateway.cs` | **MỚI** — per-credentials PayOS gateway |
| `src/TripMate.Infrastructure/Services/Payment/OperatorPaymentGatewayFactory.cs` | **MỚI** — factory |
| `src/TripMate.Infrastructure/DependencyInjection.cs` | Thêm DI `IOperatorPaymentGatewayFactory`, named HttpClient |
| `tests/TripMate.Application.UnitTests/TestUtilities/FakeServices.cs` | Thêm `FakeOperatorPaymentGatewayFactory` |
| `tests/TripMate.Application.UnitTests/Features/Traveler/Payments/CreateOrReusePaymentLinkHandlerTests.cs` | **MỚI** — 17 unit tests |
| `tests/TripMate.Api.IntegrationTests/DirectPayment/OperatorDirectPaymentLinkSqlServerTests.cs` | **MỚI** — 4 integration tests |

---

## 10. Done Criteria

- [ ] `Booking.PaymentFlow` property mapped và build thành công
- [ ] `IOperatorPaymentGatewayFactory` và `OperatorDirectPayOsGateway` được implement
- [ ] Handler đọc `booking.PaymentFlow` và route đúng gateway
- [ ] Không có fallback nào từ TO channel sang platform channel
- [ ] OperatorDirect link expiry capped tại 15 phút (không configurable)
- [ ] `OperatorChannelSnapshot` không bao giờ xuất hiện trong log hoặc HTTP response
- [ ] Toàn bộ 17 unit tests pass (Red → Green)
- [ ] 4 integration tests pass với SQL Server
- [ ] `dotnet build TripMate.slnx` → 0 lỗi, 0 warning mới
- [ ] `dotnet test TripMate.slnx` → tất cả pass, không có test bị skip

---

## 11. Quyết định chưa được chốt (cần confirm trước khi code)

| # | Vấn đề | Lựa chọn |
|---|---|---|
| D-01 | `StampPaymentFlow` tại booking creation: gộp vào Task 8 hay task riêng? | Đề xuất: gộp vào Task 8 để test E2E flow OperatorDirect hoàn chỉnh. Nếu tách thì Task 8 test chỉ seed booking trực tiếp bằng raw SQL/EF với `payment_flow = 'OperatorDirect'`. |
| D-02 | Cancel link khi persist fail (OperatorDirect): `resolvedGateway.CancelPaymentLinkAsync` hay bỏ qua? | Đề xuất: gọi cancel như platform flow (tránh để link trả tiền treo). |
| D-03 | Named HttpClient `"payos-operator"` có cần timeout/retry policy riêng không? | Đề xuất: dùng Polly retry 2 lần như platform HttpClient nếu đã có, không thêm mới. |
