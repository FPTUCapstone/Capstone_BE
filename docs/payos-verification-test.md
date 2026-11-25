# PayOS Multi-TO Capability Verification

**Phase 0 — Task 2**  
Ngày 25/11/2026. Chỉ đọc/kiểm tra; không thực hiện giao dịch tiền thật.  
Nguồn: code review (`PayOsPaymentGateway.cs`, `PayOsWebhookRegistrar.cs`, `PayOsWebhookController.cs`, `ProcessPayOsWebhookCommand.cs`, `PaymentOptions.cs`) + PayOS API docs (`https://payos.vn/docs/api/`, `https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/`).

---

## 1. Tóm tắt kết quả

| Capability | Cần cho direct model | Kết quả | Ghi chú |
|---|---|---|---|
| Mỗi TO có channel riêng (ClientId/ApiKey/ChecksumKey độc lập) | ✅ Bắt buộc | ✅ **Có** | PayOS: webhook registration per channel; mỗi x-client-id là một kênh thanh toán riêng |
| Webhook URL đăng ký per channel | ✅ Bắt buộc | ✅ **Có** | `POST /confirm-webhook` nhận x-client-id+x-api-key → gắn URL cho đúng kênh đó |
| Signature verification per channel key | ✅ Bắt buộc | ✅ **Có** | ChecksumKey thuộc về kênh; HMAC_SHA256 riêng per channel |
| Payload chứa channel/merchant identifier | ✅ Cần để route | ❌ **Không có** | Payload chỉ có orderCode, paymentLinkId, reference — không có merchantId |
| Sub-merchant / merchant delegation | ❓ Optional | ❌ **Không tìm thấy** | Không có khái niệm này trong docs |
| Key rotation support | ✅ Cần | ⚠️ **Không rõ** | Docs không mô tả API thay key; cần kiểm tra PayOS dashboard |
| Beneficiary trong payload | ✅ Cần verify | ⚠️ **Suy luận** | `accountNumber` trong payload có thể là tài khoản nhận, nhưng docs không label rõ |

**Verdict: PayOS HỖ TRỢ multi-TO về mặt kỹ thuật**, nhưng có một constraint quan trọng cần giải quyết trong Phase 2.

---

## 2. Phân tích code hiện tại — Vấn đề cần sửa

### 2.1 Kiến trúc singleton (không tương thích với multi-TO)

**Hiện tại:**

```csharp
// PaymentOptions.cs — MỘT bộ credentials toàn hệ thống
public sealed class PayOsOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ChecksumKey { get; set; } = string.Empty;
    // ...
}

// PayOsPaymentGateway.cs — luôn dùng options.PayOs (singleton)
var payOs = options.PayOs;
requestMessage.Headers.Add("x-client-id", payOs.ClientId);
requestMessage.Headers.Add("x-api-key", payOs.ApiKey);

// PayOsWebhookRegistrar.cs — register một URL cho một merchant
request.Headers.Add("x-client-id", payOs.ClientId);
request.Headers.Add("x-api-key", payOs.ApiKey);

// ProcessPayOsWebhookCommandHandler — verify bằng một ChecksumKey toàn cục
var checksumKey = paymentOptions.PayOs.ChecksumKey;
var isValid = VerifyPayOsSignature(payload.Data, payload.Signature, checksumKey);
```

**Vấn đề:** Mô hình direct payment cần mỗi TO có credentials riêng (ClientId/ApiKey/ChecksumKey). Code hiện tại chỉ hỗ trợ một merchant toàn hệ thống.

---

### 2.2 Webhook routing constraint — Vấn đề quan trọng nhất

**Payload PayOS KHÔNG chứa channel/merchant identifier:**

```json
{
  "code": "00",
  "data": {
    "orderCode": 123456789,
    "amount": 1000000,
    "accountNumber": "12345678",
    "paymentLinkId": "abc...",
    "reference": "ref...",
    "counterAccountBankId": "",
    "counterAccountBankName": "",
    "counterAccountName": "",
    "counterAccountNumber": "",
    "virtualAccountName": "",
    "virtualAccountNumber": ""
  },
  "signature": "hmac..."
}
```

**Không có** `merchantId`, `clientId`, hay bất kỳ trường nào xác định kênh TO nào đã gửi webhook.

**Consequence:** Khi nhiều TO đều gọi về `POST /api/v1/payments/payos/webhook`, backend không biết dùng ChecksumKey nào để verify signature trước khi tra cứu DB.

---

### 2.3 Hai phương án giải quyết webhook routing

#### Phương án A — Route theo orderCode (lookup trước, verify sau)

```
Webhook đến → Extract orderCode → Tra DB → Tìm booking → Tìm TO → Lấy ChecksumKey → Verify signature
```

**Ưu:** Một endpoint duy nhất, không cần thay đổi URL đăng ký với PayOS.  
**Nhược:** DB lookup TRƯỚC khi verify signature — không lý tưởng về bảo mật; nhưng chấp nhận được vì:
  - Chỉ đọc (không write) trước khi verify
  - orderCode là identifier nội bộ của TripMate, không phải data nhạy cảm
  - Nếu orderCode không tồn tại → reject ngay (không cần verify)

#### Phương án B — URL riêng per TO (verify ngay, không lookup)

```
POST /api/v1/payments/payos/webhook/{operatorChannelId}
→ Extract operatorChannelId từ URL → Load ChecksumKey → Verify signature → Process
```

**Ưu:** Verify signature ngay không cần DB lookup; URL đăng ký với PayOS embed TO identity.  
**Nhược:** Mỗi TO phải đăng ký URL riêng khi onboard; URL thay đổi nếu channelId thay đổi; `operatorChannelId` lộ ra URL public (acceptable nhưng cần lưu ý).

**Recommendation:** Phương án A đơn giản hơn và phù hợp với architecture hiện tại. Phương án B tốt hơn về security nhưng phức tạp hơn khi TO onboard. **Cần owner quyết định trước Phase 2 Task 9.**

---

## 3. Phân tích từ PayOS docs

### 3.1 Webhook registration per channel

Từ `POST https://api-merchant.payos.vn/confirm-webhook`:

```
Request headers: x-client-id, x-api-key (của channel đó)
Request body: { "webhookUrl": "https://..." }
Response: { accountNumber, accountName, name, shortName } — thông tin của channel đó
```

**Kết luận:** Webhook URL được lưu per channel. Mỗi TO gọi API này với credentials của họ → URL gắn với kênh thanh toán của họ, không ảnh hưởng các kênh khác. ✅ Hỗ trợ multi-TO.

### 3.2 Signature verification per ChecksumKey

Checksum Key được tạo ra khi tạo cổng thanh toán (`kênh thanh toán`). HMAC_SHA256 với key này là độc lập per channel. ✅ Hỗ trợ multi-TO.

### 3.3 Trường `accountNumber` trong webhook

Payload chứa `accountNumber` ở mức root của `data`. Từ flow tạo payment link, `accountNumber` là tài khoản nhận tiền của merchant. Tuy nhiên docs không label rõ. **Cần xác nhận thực tế trong sandbox test.**

### 3.4 `x-partner-code` header

Docs đề cập header `x-partner-code` ("Partner Code tham gia chương trình tích hợp đối tác payOS"). **Không phải sub-merchant**. Đây là partner integration program, không liên quan đến multi-TO.

---

## 4. Capability checklist — Kết quả kiểm chứng

```
✅ CONFIRMED — Credentials per TO (ClientId/ApiKey/ChecksumKey độc lập)
   Source: PayOS docs — webhook registration per x-client-id
   Code impact: Cần OperatorPaymentChannel entity với encrypted credentials

✅ CONFIRMED — Webhook registration per TO
   Source: POST /confirm-webhook per x-client-id
   Code impact: PayOsWebhookRegistrar cần nhận credentials động, không dùng singleton

✅ CONFIRMED — Signature isolation per ChecksumKey
   Source: PayOS docs — ChecksumKey thuộc channel
   Code impact: ProcessPayOsWebhookCommandHandler cần resolve ChecksumKey per orderCode

❌ NOT SUPPORTED — Payload merchant identifier
   Source: PayOS docs + code review — không có merchantId trong payload
   Code impact: BẮT BUỘC giải quyết bằng Phương án A hoặc B (xem mục 2.3)

⚠️ UNVERIFIED — Key rotation
   Source: Không tìm thấy trong docs
   Action: Kiểm tra PayOS dashboard hoặc liên hệ PayOS support trước Phase 2 Task 7

⚠️ UNVERIFIED — accountNumber = beneficiary account
   Source: Suy luận từ docs
   Action: Xác nhận trong sandbox test (Phase 5 Task 22)

❌ NOT SUPPORTED — Sub-merchant / delegation
   Source: Không tìm thấy trong docs
   Impact: Không ảnh hưởng; direct model không cần delegation
```

---

## 5. Gaps cần giải quyết trước Phase 2

### Gap 1 — Webhook routing decision (BLOCKING Task 9)

**Quyết định cần owner approve:** Phương án A (lookup by orderCode) hay Phương án B (URL per TO)?  
**Default recommendation:** Phương án A, trừ khi có yêu cầu security mạnh hơn.

### Gap 2 — OperatorPaymentChannel credential storage design (Task 7)

Cần thiết kế entity lưu trữ:
- `ClientId` (encrypted)
- `ApiKey` (encrypted)
- `ChecksumKey` (encrypted)
- Linked to `OperatorId`
- Status: `Pending` → `Active` → `Deactivated`
- Webhook URL được đăng ký (để idempotent re-registration)

### Gap 3 — Key rotation policy (Task 7)

Chưa rõ PayOS có API thay ChecksumKey không. Nếu không có API, khi key rotation:
- Deactivate kênh cũ → tạo kênh mới
- Các booking cũ vẫn dùng key cũ (phải lưu historical keys?)
- **Cần quyết định trước khi implement Task 7.**

### Gap 4 — Webhook re-registration khi URL thay đổi (Task 9)

Nếu hệ thống deploy đổi domain (staging → production), tất cả TO phải re-register webhook.  
Cần mechanism: admin trigger re-registration cho tất cả active channels.

---

## 6. Sandbox test plan (Phase 5 Task 22)

Khi sandbox test được phép (cần 2 PayOS test merchant accounts từ owner):

```
Setup:
  Merchant A: ClientId_A, ApiKey_A, ChecksumKey_A, WebhookUrl_A
  Merchant B: ClientId_B, ApiKey_B, ChecksumKey_B, WebhookUrl_B

Test 1 — Channel isolation
  POST /confirm-webhook với credentials A → đăng ký WebhookUrl_A
  POST /confirm-webhook với credentials B → đăng ký WebhookUrl_B
  Expected: A nhận webhook từ link A; B nhận webhook từ link B

Test 2 — Signature isolation
  Webhook từ merchant A với ChecksumKey_A → ✅ Valid
  Webhook từ merchant A với ChecksumKey_B → ❌ Reject (FixedTimeEquals fail)

Test 3 — accountNumber verification
  Create payment link với credentials A
  Traveler pays → Check payload.data.accountNumber == A's bank account
  Expected: accountNumber = kênh của A, không phải B

Test 4 — Routing (Phương án A nếu được approve)
  2 bookings: booking_A (TO A), booking_B (TO B)
  Webhook đến cùng endpoint với orderCode_A → resolve credentials A → verify ✅
  Webhook đến cùng endpoint với orderCode_B → resolve credentials B → verify ✅

Test 5 — Key rotation (nếu PayOS hỗ trợ)
  Thay ChecksumKey của channel A
  Webhook mới dùng key mới → verify ✅
  Webhook cũ còn pending → verify ❌ (expected; re-fetch key)
```

**Lưu ý:** Toàn bộ test dùng PayOS sandbox, không tiền thật. Chỉ chạy test thật với tài khoản và số tiền được chủ hệ thống cho phép riêng.

---

## 7. Kết luận Phase 0 Task 2

**PayOS hỗ trợ multi-TO.** Mỗi TO có channel riêng với credentials độc lập, webhook registration riêng, signature verification riêng.

**Constraint duy nhất cần giải quyết:** Webhook payload không chứa merchant identifier — cần implement routing strategy (mục 2.3) trước Phase 2 Task 9.

**Code changes cần thiết (Phase 1-2, không phải Phase 0):**
1. `OperatorPaymentChannel` entity với encrypted credentials
2. Dynamic gateway resolver: lấy credentials per booking, không dùng singleton
3. Webhook handler: resolve ChecksumKey per orderCode trước khi verify
4. `PayOsWebhookRegistrar`: chấp nhận credentials động thay vì singleton

**Task 2 Done criteria:**
- ✅ Bằng chứng channel isolation từ docs (per x-client-id)
- ✅ Bằng chứng webhook per channel (POST /confirm-webhook per channel)
- ✅ Bằng chứng signature per channel key
- ✅ Giới hạn provider: không có merchantId trong payload → documented routing strategy
- ✅ Không đoán API merchant delegation (không tìm thấy → documented as NOT SUPPORTED)
- ✅ Key rotation: documented as UNVERIFIED, needs follow-up
- ⏳ Sandbox test: chờ owner cung cấp 2 test merchant accounts
