# Task 7 — Kênh thanh toán TO (Credential Protection / Ownership / Verification States)

Ngày: 25/11/2026. Nhánh: `fix/linhnv-payout-and-refund`.  
Trạng thái: **Viết spec; chưa code, chưa thay đổi DB hay flow đang chạy.**  
Nguồn: `specs/direct-operator-payment-refund-spec.md` mục 4, 13.7; `plans/direct-operator-payment-refund-plan.md` Task 7.

---

## 1. Phạm vi

Task 7 tạo luồng TO kết nối kênh payOS của mình vào TripMate và Admin duyệt kênh. Không bao gồm: tạo payment link (Task 8), webhook (Task 9), chặn mở bán (Task 10), hay bất kỳ flow refund/commission nào.

Các file DB / domain entity `payment.OperatorPaymentChannels`, `OperatorPaymentChannel.cs`, `OperatorPaymentChannelConfiguration.cs` đã có từ Task 5 — Task 7 **không tạo thêm migration**.

---

## 2. Hiện trạng

| Hạng mục | Hiện trạng |
|---|---|
| `IPaymentGateway` | Dùng một kênh TripMate; không có khái niệm kênh theo TO |
| `PayOsPaymentGateway.cs` | Dùng `PayOsOptions` đơn — không hỗ trợ nhiều kênh |
| `Features/Operator/PaymentChannels/` | Chưa tồn tại |
| `OperatorPaymentChannelController.cs` | Chưa tồn tại |
| Credential encryption | Chưa có infrastructure; không có `ICredentialProtector` |
| `payment.OperatorPaymentChannels` | Đã tạo, 0 row; cột `client_id_ref`, `api_key_ref`, `checksum_key_ref` là references tới secret store |

---

## 3. Phân quyền

| Actor | Thao tác |
|---|---|
| **TO** (chủ tour, đã Active) | Xem trạng thái kênh của mình; submit credentials; yêu cầu verify |
| **Admin** | Xem danh sách kênh; duyệt kênh khi API không đủ xác minh; vô hiệu hóa kênh |
| **Staff** | Chỉ xem trạng thái kênh (read-only, không thao tác) |
| **System** | Gọi payOS test API, đăng ký webhook, cập nhật trạng thái |

Quyền kiểm tra từ DB (FK `tour_operator_user_id`); không tin `operatorId` từ client.

---

## 4. Business rules (non-negotiable)

| ID | Rule |
|---|---|
| **CR-01** | Credentials (ClientId, ApiKey, ChecksumKey) **không được lưu raw** vào DB; lưu dưới dạng encrypted reference token trỏ vào secret store ngoài DB. |
| **CR-02** | Sau khi save, không trả credential value qua bất kỳ API nào (write-only). Log và audit không ghi credential value — chỉ ghi reference token prefix ≤8 ký tự (nếu cần debug). |
| **CR-03** | Mỗi TO chỉ có **một** kênh; submit credentials mới khi đã có kênh sẽ **cập nhật kênh hiện tại** (tăng `channel_version`) chứ không tạo kênh thứ hai. |
| **CR-04** | Trạng thái chỉ có thể chuyển theo: `NotConnected → PendingVerification` (khi TO submit) → `Ready` / `ConnectionError` (sau verify) → `Disabled` (Admin vô hiệu) → `NotConnected` (khi reset). `PendingVerification → Ready` chỉ khi verify thành công **và** Admin duyệt (nếu cần). |
| **CR-05** | Chỉ trạng thái `Ready` cho phép tạo payment link mới (Task 8 enforce). Task 7 chỉ **expose trạng thái** qua API; không tự enforce publishing gate. |
| **CR-06** | Test API payOS thành công **không tự động chứng minh** kênh người dùng = doanh nghiệp đúng. Backend cần đối chiếu `beneficiaryAccountName` với hồ sơ doanh nghiệp hoặc Admin duyệt thủ công trước khi `Ready`. |
| **CR-07** | Đổi credentials → tăng `channel_version`; payment/refund đang chờ vẫn liên kết `channel_version` cũ bất biến. Version mới chỉ dùng cho payment mới sau khi `Ready` lại. |
| **CR-08** | Vô hiệu hoá kênh (`Disabled`) không huỷ payment/refund đang xử lý; chỉ chặn tạo payment mới. |
| **CR-09** | TO không được tự chuyển trạng thái sang `Ready`. `Ready` chỉ được set bởi hệ thống sau verify thành công + Admin confirm (nếu CR-06 chưa thoả mãn tự động). |
| **CR-10** | Mọi thay đổi trạng thái phải có audit log bất biến (actor, timestamp, từ → đến, lý do). |

---

## 5. Luồng nghiệp vụ

### 5.1 TO kết nối kênh lần đầu

```
TO PUT /api/v1/operator/payment-channel (credentials + beneficiary)
  → validate format
  → encrypt credentials → lưu refs vào OperatorPaymentChannels (NotConnected → PendingVerification)
  → ghi audit

TO POST /api/v1/operator/payment-channel/verify
  → load encrypted refs → gọi payOS test payment API
  → đăng ký / xác nhận webhook URL
  → nếu API thành công:
      → so sánh beneficiary với hồ sơ doanh nghiệp
      → nếu khớp tự động → trạng thái = Ready  (channel_version tăng)
      → nếu không khớp / chưa kiểm chứng → giữ PendingVerification, ghi cờ cần Admin duyệt
  → nếu API lỗi → ConnectionError, lưu error_code
  → trả trạng thái mới (không trả credentials)
```

### 5.2 Admin duyệt kênh

```
Admin GET /api/v1/admin/operator-payment-channels?status=PendingVerification
Admin GET /api/v1/admin/operator-payment-channels/{channelId}
Admin POST /api/v1/admin/operator-payment-channels/{channelId}/approve
  → kiểm tra quyền riêng (không dùng quyền Staff)
  → operationId + expectedVersion (optimistic lock)
  → trạng thái PendingVerification → Ready
  → ghi audit (Admin actor, reason)
Admin POST /api/v1/admin/operator-payment-channels/{channelId}/disable
  → trạng thái bất kỳ → Disabled
  → ghi reason bắt buộc
  → ghi audit
```

### 5.3 TO đổi credentials

```
TO PUT /api/v1/operator/payment-channel (credentials mới)
  → tạo bản cập nhật trên kênh hiện tại (channel_version++)
  → trạng thái = PendingVerification (kể cả nếu trước đó là Ready/ConnectionError)
  → payment/refund cũ giữ liên kết version cũ bất biến
  → TO chạy lại verify
```

---

## 6. API contract

### 6.1 `GET /api/v1/operator/payment-channel`

**Actor**: TO đang đăng nhập.  
**Response**: trạng thái kênh của TO (không có credential values).

```json
{
  "channelId": 1,
  "opaqueId": "xxxxxxxx-...",
  "status": "PendingVerification",
  "channelVersion": 2,
  "beneficiaryAccountNumber": "••••5678",
  "beneficiaryAccountName": "NGUYEN VAN A",
  "beneficiaryBankCode": "VCB",
  "webhookRegisteredAt": "2026-11-25T10:00:00Z",
  "connectedAt": "2026-11-25T09:00:00Z",
  "lastVerifiedAt": "2026-11-25T09:30:00Z",
  "allowedActions": ["submit_credentials", "verify"]
}
```

Nếu TO chưa có kênh: 200 với `status: "NotConnected"`, `channelId: null`.

### 6.2 `PUT /api/v1/operator/payment-channel`

**Actor**: TO.  
**Body**:

```json
{
  "clientId": "...",
  "apiKey": "...",
  "checksumKey": "...",
  "beneficiaryAccountNumber": "0012345678",
  "beneficiaryAccountName": "NGUYEN VAN A",
  "beneficiaryBankCode": "VCB"
}
```

- `clientId`, `apiKey`, `checksumKey`: required, không trống, không log.  
- `beneficiaryAccountNumber`: required, 6–20 chữ số.  
- `beneficiaryAccountName`: required, 2–200 ký tự.  
- `beneficiaryBankCode`: required, 2–20 ký tự.  
- Không dùng `expectedVersion`; server tự increment.

**Response**: `ChannelStatusResponse` (không trả credential values).  
**Errors**: `400` validation; `403` TO không đủ điều kiện (chưa Active/ký hợp đồng); `409` nếu đang xử lý concurrent submit.

### 6.3 `POST /api/v1/operator/payment-channel/verify`

**Actor**: TO.  
**Body**: `{}` (không có credential; server load từ secret store).  
**Response**: `ChannelStatusResponse` với trạng thái mới.  
**Errors**: `400` nếu không có credentials đã lưu; `422` nếu payOS API không tiếp cận được (trả `ConnectionError`); `409` đang verify.

### 6.4 `GET /api/v1/admin/operator-payment-channels`

**Actor**: Admin, quyền riêng (không phải Staff).  
**Query params**: `status` (optional filter), `page`, `pageSize`.  
**Response**: paged list; bao gồm `beneficiaryAccountName`, `status`, `channelVersion`, `lastVerifiedAt`, `operatorName`.

### 6.5 `GET /api/v1/admin/operator-payment-channels/{channelId}`

**Actor**: Admin.  
**Response**: full detail; không trả credential values.

### 6.6 `POST /api/v1/admin/operator-payment-channels/{channelId}/approve`

**Actor**: Admin, quyền riêng `OperatorPaymentChannelApproval`.  
**Body**:

```json
{
  "operationId": "uuid",
  "expectedVersion": 2,
  "reason": "Đã đối chiếu hồ sơ doanh nghiệp và xác nhận chủ kênh."
}
```

- `reason`: required, không trống.  
- `operationId`: idempotency key; replay cùng operationId + version trả cùng kết quả.  
**Response**: `ChannelStatusResponse`.  
**Errors**: `409` version conflict hoặc trạng thái không phải `PendingVerification`; `404` không tìm thấy.

### 6.7 `POST /api/v1/admin/operator-payment-channels/{channelId}/disable`

**Actor**: Admin.  
**Body**:

```json
{
  "operationId": "uuid",
  "expectedVersion": 2,
  "reason": "..."
}
```

**Response**: `ChannelStatusResponse`.

---

## 7. Các handler / command cần tạo

```
Features/Operator/PaymentChannels/
  GetChannel/
    GetOperatorPaymentChannelQuery.cs
    GetOperatorPaymentChannelQueryHandler.cs
    OperatorChannelStatusDto.cs
  Submit/
    SubmitPaymentChannelCredentialsCommand.cs
    SubmitPaymentChannelCredentialsCommandHandler.cs
  Verify/
    VerifyPaymentChannelCommand.cs
    VerifyPaymentChannelCommandHandler.cs

Features/Admin/OperatorPaymentChannels/
  List/
    ListOperatorPaymentChannelsQuery.cs
    ListOperatorPaymentChannelsQueryHandler.cs
  Detail/
    GetOperatorPaymentChannelDetailQuery.cs
    GetOperatorPaymentChannelDetailQueryHandler.cs
  Approve/
    ApproveOperatorPaymentChannelCommand.cs
    ApproveOperatorPaymentChannelCommandHandler.cs
  Disable/
    DisableOperatorPaymentChannelCommand.cs
    DisableOperatorPaymentChannelCommandHandler.cs
```

```
Presentation/Controllers/
  OperatorPaymentChannelController.cs   (TO endpoints)
  AdminOperatorPaymentChannelsController.cs
```

---

## 8. Infrastructure cần tạo

### 8.1 `IPaymentCredentialProtector`

```csharp
public interface IPaymentCredentialProtector
{
    // Encrypt raw value → reference token (stored in DB)
    string Protect(string rawValue);
    // Decrypt reference token → raw value (for server-side use only)
    string Unprotect(string referenceToken);
}
```

Implement bằng `IDataProtector` (ASP.NET Data Protection) hoặc gọi Azure Key Vault / AWS Secrets Manager tùy environment. `Protect` / `Unprotect` không log value.

### 8.2 `IPaymentChannelVerifier`

```csharp
public interface IPaymentChannelVerifier
{
    Task<VerificationResult> VerifyAsync(ChannelCredentials credentials, string webhookBaseUrl, CancellationToken ct);
}
```

`ChannelCredentials` chứa raw values được unprotect từ refs — không serialize ra ngoài class này.  
`VerificationResult`: `{ IsSuccess, ErrorCode?, BeneficiaryName?, WebhookRegisteredAt? }`.

Implementation: `PayOsChannelVerifier.cs` trong Infrastructure/Services/Payment/. Gọi `PayOS /v2/payment-requests` test endpoint (hoặc tương đương) và đăng ký webhook. Nếu chưa xác định được API test của payOS: trả `VerificationResult.PendingAdminApproval`.

### 8.3 Logging redaction

Tất cả log liên quan credential phải dùng `[Redacted]` cho credential values. Không dùng structured logging properties chứa raw secret.

---

## 9. Test cases cần viết (Red → Green)

### 9.1 Unit tests (không cần SQL Server)

| # | Test | Done condition |
|---|---|---|
| U1 | `SubmitPaymentChannelCredentials` — TO hợp lệ chưa có kênh → tạo kênh mới `PendingVerification` | channel_version = 1 |
| U2 | `SubmitPaymentChannelCredentials` — TO đã có kênh `Ready` → cập nhật kênh, channel_version++, trạng thái = `PendingVerification` | version tăng, payment cũ không ảnh hưởng |
| U3 | `SubmitPaymentChannelCredentials` — raw credentials không xuất hiện trong audit/log | log assertion |
| U4 | `VerifyPaymentChannel` — payOS trả OK, beneficiary khớp hồ sơ → trạng thái = `Ready` | status=Ready |
| U5 | `VerifyPaymentChannel` — payOS trả OK, beneficiary không khớp → trạng thái vẫn `PendingVerification` | cần Admin approve |
| U6 | `VerifyPaymentChannel` — payOS lỗi network → trạng thái = `ConnectionError`, error_code lưu | ConnectionError |
| U7 | `ApproveOperatorPaymentChannel` — Admin có quyền riêng, trạng thái `PendingVerification` → `Ready` | Ready, audit ghi |
| U8 | `ApproveOperatorPaymentChannel` — sai expectedVersion → 409 VersionConflict | không thay đổi trạng thái |
| U9 | `ApproveOperatorPaymentChannel` — trạng thái không phải `PendingVerification` → 409 InvalidState | không thay đổi |
| U10 | `ApproveOperatorPaymentChannel` — cùng operationId + version lần 2 → trả kết quả cũ, không ghi audit thêm | idempotent |
| U11 | `DisableOperatorPaymentChannel` — Admin disable kênh `Ready` → `Disabled`, audit ghi reason | Disabled |
| U12 | `DisableOperatorPaymentChannel` — thiếu reason → validation error | 400 |
| U13 | Staff không có quyền gọi Admin approve/disable → 403 | Forbidden |
| U14 | TO không Active / chưa ký hợp đồng submit credentials → 403 | Forbidden |
| U15 | TO gọi GET channel khi chưa có kênh → 200 với status=NotConnected | không 404 |

### 9.2 Integration tests (SqlServerFact — cần `TRIPMATE_SQLSERVER_TEST_CONNECTION`)

| # | Test |
|---|---|
| I1 | Submit → verify → approve round-trip lưu đúng vào DB; credential values không lưu raw |
| I2 | Hai TO submit credentials concurrently → mỗi TO chỉ có một kênh |
| I3 | Submit kênh mới khi đang có kênh `Ready` → `channel_version` tăng, row cũ bị overwrite (không tạo row mới) |
| I4 | Approve với wrong `expectedVersion` trong concurrent scenario → chỉ một approve thắng |

---

## 10. Điều kiện done (Task 7)

- [ ] `dotnet build TripMate.slnx` 0 warnings 0 errors.
- [ ] Tất cả test unit U1–U15 pass.
- [ ] Integration tests I1–I4 pass (hoặc skip nếu không có SQL Server connection, không được xóa).
- [ ] `GET /api/v1/operator/payment-channel` trả đúng trạng thái, không trả credential values.
- [ ] `PUT /api/v1/operator/payment-channel` lưu encrypted refs, không lưu raw values.
- [ ] `POST /api/v1/operator/payment-channel/verify` gọi payOS, cập nhật trạng thái, không expose credentials ra ngoài handler.
- [ ] Admin approve/disable hoạt động với optimistic lock và idempotency.
- [ ] Không có đường nào TO tự chuyển status sang `Ready`.
- [ ] Credential values không xuất hiện trong log (kiểm tra bằng assertion trong U3 và I1).
- [ ] Không có migration mới (schema đã có từ Task 5).
- [ ] Không thay đổi flow thanh toán hiện tại (legacy `IPaymentGateway` / `PayOsPaymentGateway` không bị đổi logic).
- [ ] Code review theo AGENTS.md không có findings Critical/High.

---

## 11. Ngoài phạm vi Task 7

- Tích hợp kênh TO vào `CreateOrReusePaymentLinkCommandHandler` (Task 8).
- Webhook per-channel routing (Task 9).
- Chặn publish/bookability khi kênh chưa sẵn sàng (Task 10).
- Credential rotation plan (Phase 5 / điều kiện phát hành).
- E2E với payOS thật (Task 22).
