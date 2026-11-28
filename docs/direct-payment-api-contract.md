# Direct Payment API Contract

**Phase 0 — Task 3**  
Ngày 25/11/2026. Nguồn: `specs/direct-operator-payment-refund-spec.md` (Sections 1–13, duyệt 25/11/2026).  
**Contract này là input cho FE/Mobile trước khi implement.** Mọi route, DTO, error code và 72h boundary rule được khóa tại đây. Thay đổi phải update file này và được duyệt lại trước khi apply.

---

## Quy ước chung

### Authentication & Authorization

Mọi endpoint (trừ webhook) yêu cầu JWT Bearer. Ownership lấy từ DB claim, không tin `operatorId`/`userId` trong request body.

| Role | Enum value | Ghi chú |
|------|-----------|---------|
| `Traveler` | Chủ booking | Chỉ thao tác booking của mình |
| `TourOperator` | Chủ tour | Chỉ thao tác tour/refund của mình |
| `Staff` | Read-only | Không có mutation endpoint nào trong luồng direct |
| `Administrator` | Admin | Quyền riêng `DirectRefundDisputeReview`; không kế thừa quyền Staff |

### HTTP status codes

| Code | Khi nào |
|------|---------|
| 200 | Thành công (GET và mutation đều trả 200 với body mới) |
| 400 | Validation failed / dữ liệu không hợp lệ |
| 401 | Chưa xác thực |
| 403 | Đúng role nhưng không phải chủ resource |
| 404 | Resource không tồn tại hoặc không có quyền biết tồn tại |
| 409 | State conflict / version conflict / window đã đóng |
| 422 | Business rule violation (amount vượt giới hạn, state không hợp lệ...) |

### Idempotency

Mutation có side-effect tài chính dùng header `Idempotency-Key: <UUID v4>` (required).  
Retry cùng key → trả response đã lưu, không tạo lại.

### Error response

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Human-readable title",
  "status": 409,
  "detail": "Chi tiết lỗi",
  "errorCode": "direct_refund.version_conflict",
  "traceId": "00-abc..."
}
```

### Error codes (mới — direct payment)

| Error code | HTTP | Mô tả |
|-----------|------|-------|
| `payment_channel.not_ready` | 422 | Kênh TO chưa Ready — không tạo link mới |
| `payment_channel.verification_failed` | 422 | Test API PayOS thất bại khi verify |
| `payment_channel.not_found` | 404 | TO chưa có kênh hoặc không có quyền xem |
| `payment_channel.version_conflict` | 409 | Optimistic concurrency conflict |
| `payment.reconciliation_required` | 409 | Webhook lạ / amount sai / trùng → reconciliation |
| `direct_refund.not_found` | 404 | Refund không tồn tại hoặc không có quyền |
| `direct_refund.invalid_state` | 409 | Hành động không hợp lệ ở trạng thái hiện tại |
| `direct_refund.version_conflict` | 409 | `expectedVersion` không khớp |
| `direct_refund.response_window_closed` | 409 | `now >= confirmationDeadlineUtc` — cửa sổ đã đóng |
| `direct_refund.recipient_required` | 422 | Chưa có recipient hợp lệ trước khi TO chuyển |
| `direct_refund.evidence_invalid` | 400 | File sai MIME / size / số lượng |
| `direct_refund.amount_invalid` | 422 | Amount không hợp lệ (0, âm, vượt hạn mức) |
| `direct_refund.access_denied` | 403 | Không phải chủ booking/tour/case |
| `direct_refund.duplicate_operation` | 409 | operationId đã tồn tại (idempotency) |
| `commission.invoice_not_found` | 404 | Invoice không tồn tại |
| `commission.already_paid` | 409 | Invoice đã thanh toán đủ |
| `commission.invalid_state` | 409 | Trạng thái không cho phép thao tác |

---

## Group 1 — TO: Payment Channel

### GET `/api/v1/operator/payment-channel`

**Auth:** `TourOperator`  
**Mô tả:** Xem trạng thái kênh PayOS của TO hiện tại.

**Response 200:**
```json
{
  "channelId": "ch_opaque_id",
  "version": 3,
  "status": "Ready",
  "beneficiarySnapshot": {
    "bankCode": "VCB",
    "accountNumberMasked": "****5678",
    "accountName": "NGUYEN VAN A"
  },
  "webhookRegisteredUrl": "https://api.tripmate.vn/api/v1/payments/payos/webhook",
  "verifiedAt": "2026-11-25T10:00:00Z",
  "allowedActions": ["reconfigure", "disable"]
}
```

**Channel status values:**
| Value | Mô tả |
|-------|-------|
| `NotConnected` | Chưa cấu hình |
| `PendingVerification` | Đã nhập credentials, chờ verify |
| `Ready` | Đã verify, có thể mở bán |
| `ConnectionError` | Verify thất bại |
| `Disabled` | Bị vô hiệu hoá |

---

### PUT `/api/v1/operator/payment-channel`

**Auth:** `TourOperator`  
**Header:** `Idempotency-Key: <UUID>`  
**Mô tả:** Tạo hoặc cập nhật cấu hình kênh. Secret fields write-only — không bao giờ trả về trong response.

**Request body:**
```json
{
  "expectedVersion": 2,
  "clientId": "string (required, 1-100 chars)",
  "apiKey": "string (required, 1-200 chars, write-only)",
  "checksumKey": "string (required, 1-200 chars, write-only)"
}
```

**Response 200:** Giống GET (không có secret fields).

**Errors:** 409 `payment_channel.version_conflict`, 422 `payment_channel.not_ready` (nếu có booking đang pending trên kênh cũ).

---

### POST `/api/v1/operator/payment-channel/verify`

**Auth:** `TourOperator`  
**Header:** `Idempotency-Key: <UUID>`  
**Mô tả:** Gọi PayOS API test + đăng ký webhook. Kết quả `Ready` hoặc `ConnectionError`.

**Request body:** `{}` (không nhận credentials — dùng credentials đã lưu)

**Response 200:**
```json
{
  "channelId": "ch_opaque_id",
  "version": 4,
  "status": "Ready",
  "verifiedAt": "2026-11-25T10:05:00Z",
  "beneficiarySnapshot": {
    "bankCode": "VCB",
    "accountNumberMasked": "****5678",
    "accountName": "NGUYEN VAN A"
  }
}
```

**Errors:** 422 `payment_channel.verification_failed` (kèm detail từ PayOS, không lộ secret).

---

## Group 2 — Webhook (PayOS → TripMate)

### POST `/api/v1/payments/payos/webhook`

**Auth:** None (AllowAnonymous) — xác thực bằng HMAC signature  
**Mô tả:** Nhận event PayOS. Routing: Extract `orderCode` → Tra DB → Tìm `OperatorPaymentChannel` → Lấy `ChecksumKey` → Verify HMAC → Process. (Decision: Phương án A, duyệt 25/11/2026)

**Request body (từ PayOS):**
```json
{
  "code": "00",
  "desc": "success",
  "data": {
    "orderCode": 123456789,
    "amount": 1000000,
    "description": "TM-B123456",
    "accountNumber": "12345678",
    "reference": "FT26329...",
    "transactionDateTime": "2026-11-25 10:30:00",
    "currency": "VND",
    "paymentLinkId": "abc123...",
    "code": "00",
    "desc": "Thanh cong"
  },
  "signature": "hmac_hex_string"
}
```

**Response 200:** `{ "success": true }`  
**Response 400:** `payment.reconciliation_required` (signature lỗi, amount lỗi, orderCode không tìm thấy → trả 200 với probe signal hoặc 400 tùy case — giữ theo logic hiện tại trong `ProcessPayOsWebhookCommandHandler`)

**Idempotency:** Trùng `orderCode` + `reference` → trả 200, không tạo lại.

---

## Group 3 — Traveler: Direct Refund

### GET `/api/v1/traveler/direct-refunds/{refundCaseId}`

**Auth:** `Traveler` (chủ booking)  
**Mô tả:** Chi tiết refund case + timeline + deadline + allowed actions.

**Response 200:**
```json
{
  "refundCaseId": "drc_123",
  "bookingId": 456,
  "status": "AwaitingTravelerConfirmation",
  "paymentFlow": "OperatorDirect",
  "refundAmount": 390000,
  "currency": "VND",
  "source": "TourCancellation",
  "recipientStatus": "Confirmed",
  "recipientMasked": {
    "bankCode": "VCB",
    "accountNumberMasked": "****1234",
    "accountHolderName": "TRAN THI B"
  },
  "activeTransfer": {
    "transferId": "drt_789",
    "reportedAmountVnd": 390000,
    "reportedAtUtc": "2026-11-25T08:00:00Z",
    "confirmationDeadlineUtc": "2026-11-26T08:00:00Z",
    "evidenceCount": 2
  },
  "serverNowUtc": "2026-11-25T10:00:00Z",
  "confirmedReceivedAmount": null,
  "remainingObligationAmount": 390000,
  "completionMethod": null,
  "completedAtUtc": null,
  "version": 5,
  "allowedActions": ["submit_response"],
  "timeline": [
    {
      "eventType": "RefundCreated",
      "occurredAtUtc": "2026-11-24T07:00:00Z",
      "actorRole": "System"
    },
    {
      "eventType": "RecipientConfirmed",
      "occurredAtUtc": "2026-11-24T09:00:00Z",
      "actorRole": "Traveler"
    },
    {
      "eventType": "TransferReported",
      "occurredAtUtc": "2026-11-25T08:00:00Z",
      "actorRole": "TourOperator"
    }
  ]
}
```

**Status values:**
| Value | Mô tả |
|-------|-------|
| `AwaitingRecipient` | Traveler chưa xác nhận tài khoản nhận |
| `AwaitingTransfer` | Chờ TO chuyển tiền |
| `AwaitingTravelerConfirmation` | TO đã báo chuyển, Traveler có 72h |
| `Disputed` | Traveler phản đối |
| `AwaitingAdminReview` | Hết lượt thương lượng khiếu nại |
| `Completed` | Hoàn tất |

**completionMethod values:** `TravelerConfirmed` | `AutoCompletedAfter72Hours` | `AdminResolved`

---

### PUT `/api/v1/traveler/direct-refunds/{refundCaseId}/recipient`

**Auth:** `Traveler` (chủ booking)  
**Header:** `Idempotency-Key: <UUID>`  
**Mô tả:** Xác nhận tài khoản nhận tiền + acknowledge quy tắc 72h. Chỉ cho phép trước khi TO báo chuyển.

**Request body:**
```json
{
  "expectedVersion": 2,
  "bankCode": "VCB",
  "accountNumber": "1234567890",
  "accountHolderName": "TRAN THI B",
  "acknowledges72hRule": true
}
```

**Validation:**
- `bankCode`: non-empty, max 10 chars
- `accountNumber`: non-empty, 6–20 chars, digits only
- `accountHolderName`: non-empty, max 100 chars
- `acknowledges72hRule`: must be `true`

**Response 200:** Giống GET detail (trạng thái mới).

**Errors:**
- 409 `direct_refund.invalid_state` nếu đã có transfer
- 409 `direct_refund.version_conflict`

---

### POST `/api/v1/traveler/direct-refunds/{refundCaseId}/response`

**Auth:** `Traveler` (chủ booking)  
**Header:** `Idempotency-Key: <UUID>`  
**Mô tả:** Traveler xác nhận đã nhận / phản đối. Chỉ trong cửa sổ `now < confirmationDeadlineUtc`.

**Request body:**
```json
{
  "expectedVersion": 5,
  "responseType": "ReceivedInFull",
  "reason": null,
  "actualReceivedAmountVnd": null
}
```

`responseType` values:
| Value | `reason` | `actualReceivedAmountVnd` |
|-------|----------|--------------------------|
| `ReceivedInFull` | Optional | Not required |
| `NotReceived` | **Required**, max 500 chars | Not required |
| `PartiallyReceived` | **Required**, max 500 chars | **Required**, > 0 và < refundAmount |

**Response 200:** Giống GET detail.

**Errors:**
- 409 `direct_refund.response_window_closed` (`now >= confirmationDeadlineUtc`)
- 409 `direct_refund.invalid_state`
- 409 `direct_refund.version_conflict`
- 422 `direct_refund.amount_invalid`

---

### POST `/api/v1/traveler/direct-refunds/{refundCaseId}/late-review`

**Auth:** `Traveler` (chủ booking)  
**Header:** `Idempotency-Key: <UUID>`  
**Mô tả:** Gửi yêu cầu xem xét muộn trong 7 ngày sau auto-completion. Không tự đảo trạng thái.

**Request body:**
```json
{
  "reason": "string (required, 10–1000 chars)",
  "evidenceUploadIds": ["evid_abc", "evid_def"]
}
```

**Window:** `now < completedAtUtc + 7 days` (server-side check)

**Response 200:**
```json
{
  "lateReviewId": "lr_123",
  "submittedAtUtc": "2026-11-26T09:00:00Z",
  "windowClosesAtUtc": "2026-12-02T08:00:00Z",
  "status": "PendingAdminReview"
}
```

**Errors:**
- 409 `direct_refund.invalid_state` (không phải `AutoCompletedAfter72Hours`)
- 409 `direct_refund.response_window_closed` (ngoài 7 ngày)

---

## Group 4 — TO: Direct Refund

### GET `/api/v1/operator/direct-refunds`

**Auth:** `TourOperator`  
**Query params:** `status`, `pageNumber=1`, `pageSize=20`, `isOverdue=false`  
**Mô tả:** Danh sách refund cases của TO.

**Response 200:**
```json
{
  "items": [
    {
      "refundCaseId": "drc_123",
      "bookingId": 456,
      "tourTitle": "Đà Nẵng 3N2Đ",
      "refundAmount": 390000,
      "currency": "VND",
      "status": "AwaitingTransfer",
      "isOverdue": false,
      "transferDeadlineUtc": "2026-11-28T08:00:00Z",
      "version": 3
    }
  ],
  "totalCount": 42,
  "pageNumber": 1,
  "pageSize": 20
}
```

---

### GET `/api/v1/operator/direct-refunds/{refundCaseId}`

**Auth:** `TourOperator` (chủ tour)  
**Response 200:** Tương tự Traveler GET nhưng:
- `recipientMasked` → `recipientFull` (TO không xem full account number, chỉ xem masked — bảo vệ Traveler)
- Thêm `obligationAmountVnd`, `deadline72hTransferUtc`, `isOverdue`
- `evidenceCount` per transfer nhưng không có download URL (download qua endpoint riêng)

---

### POST `/api/v1/operator/direct-refunds/{refundCaseId}/transfers`

**Auth:** `TourOperator` (chủ tour)  
**Header:** `Idempotency-Key: <UUID>`  
**Mô tả:** TO báo đã chuyển tiền. Bắt buộc evidence. Immutable sau khi tạo.

**Request body:**
```json
{
  "operationId": "uuid-v4",
  "expectedVersion": 3,
  "bankTransactionReference": "FT26329ABC123",
  "actualTransferredAmountVnd": 390000,
  "transferredAtLocal": "2026-11-25T15:30:00+07:00",
  "evidenceUploadIds": ["evid_abc", "evid_def"],
  "note": "string (optional, max 500 chars)"
}
```

**Validation:**
- `bankTransactionReference`: non-empty, max 100 chars — không chấp nhận text tự nhập không có reference
- `actualTransferredAmountVnd`: > 0, không vượt `remainingObligationAmount`
- `transferredAtLocal`: không ở tương lai (theo UTC server)
- `evidenceUploadIds`: 1–3 items (đã upload qua `/evidence` endpoint)

**Response 200:**
```json
{
  "transferId": "drt_789",
  "refundCaseId": "drc_123",
  "status": "AwaitingTravelerConfirmation",
  "reportedAtUtc": "2026-11-25T08:30:00Z",
  "confirmationDeadlineUtc": "2026-11-26T08:30:00Z",
  "serverNowUtc": "2026-11-25T08:30:01Z",
  "version": 4
}
```

**Note:** `reportedAtUtc` = server time, không phải `transferredAtLocal` của TO. 72h tính từ `reportedAtUtc`.

**Errors:**
- 409 `direct_refund.invalid_state`
- 409 `direct_refund.version_conflict`
- 409 `direct_refund.duplicate_operation` (cùng `operationId`)
- 422 `direct_refund.recipient_required`
- 422 `direct_refund.amount_invalid`
- 400 `direct_refund.evidence_invalid`

---

### POST `/api/v1/operator/direct-refunds/{refundCaseId}/evidence`

**Auth:** `TourOperator` (chủ tour)  
**Content-Type:** `multipart/form-data`  
**Mô tả:** Upload evidence file trước khi báo chuyển. Trả về `uploadId` để dùng trong transfer request.

**Form fields:**
- `file`: binary (required)

**File constraints (Section 13 decision 3):**
- MIME: `application/pdf`, `image/jpeg`, `image/png` (kiểm tra magic bytes, không chỉ extension)
- Max size: 5 MB/file
- Max per report: 3 files (kiểm tra khi tạo transfer, không phải khi upload)

**Response 200:**
```json
{
  "uploadId": "evid_abc123",
  "fileName": "transfer_receipt.pdf",
  "fileSizeBytes": 204800,
  "mimeType": "application/pdf",
  "uploadedAtUtc": "2026-11-25T08:25:00Z",
  "expiresIfUnusedAtUtc": "2026-11-25T09:25:00Z"
}
```

**Errors:**
- 400 `direct_refund.evidence_invalid` (sai MIME, quá size)

---

### GET `/api/v1/direct-refunds/{refundCaseId}/evidence/{evidenceId}`

**Auth:** Traveler (chủ booking) | TourOperator (chủ tour) | Staff | Admin (có `DirectRefundDisputeReview`)  
**Mô tả:** Download evidence. Server kiểm tra ownership/role trước khi serve. Không có URL public.

**Response 200:** Binary file với `Content-Type` và `Content-Disposition: attachment`.  
**Errors:** 403 `direct_refund.access_denied`, 404 `direct_refund.not_found`

---

## Group 5 — Staff: Direct Refund (Read-only)

### GET `/api/v1/staff/direct-refunds`

**Auth:** `Staff`  
**Query params:** `status`, `isOverdue=true`, `pageNumber=1`, `pageSize=20`  
**Mô tả:** Hàng chờ đối soát — read-only, không có mutation.

**Response 200:** Tương tự operator list nhưng có thêm cả hai phía (TO info + Traveler info masked).

---

### GET `/api/v1/staff/direct-refunds/{refundCaseId}`

**Auth:** `Staff`  
**Response 200:** Full timeline, cả hai phía, evidence count. Không có `allowedActions` mutation.

---

## Group 6 — Admin: Dispute Review

### GET `/api/v1/admin/direct-refund-disputes`

**Auth:** `Administrator` với quyền `DirectRefundDisputeReview`  
**Query params:** `status=Disputed,AwaitingAdminReview`, `pageNumber=1`, `pageSize=20`

**Response 200:**
```json
{
  "items": [
    {
      "refundCaseId": "drc_123",
      "bookingId": 456,
      "status": "Disputed",
      "refundAmount": 390000,
      "disputedSince": "2026-11-25T10:00:00Z",
      "source": "PostTripComplaint",
      "version": 7
    }
  ],
  "totalCount": 5,
  "pageNumber": 1,
  "pageSize": 20
}
```

---

### GET `/api/v1/admin/direct-refund-disputes/{refundCaseId}`

**Auth:** `Administrator` với quyền `DirectRefundDisputeReview`  
**Response 200:** Full detail gồm:
- Toàn bộ timeline
- Recipient full info
- Evidence list (không phải binary — dùng GET evidence endpoint để download)
- TO transfer history
- Traveler response history
- `allowedActions`: `["resolve"]`

---

### POST `/api/v1/admin/direct-refund-disputes/{refundCaseId}/resolve`

**Auth:** `Administrator` với quyền `DirectRefundDisputeReview`  
**Header:** `Idempotency-Key: <UUID>`  
**Mô tả:** Admin ra quyết định đối soát. Chỉ `Completed` nếu có đủ căn cứ.

**Request body:**
```json
{
  "operationId": "uuid-v4",
  "expectedVersion": 7,
  "decision": "Completed",
  "decisionReason": "string (required, 10–2000 chars)",
  "verifiedTransferIds": ["drt_789"],
  "additionalEvidenceIds": ["evid_xyz"],
  "remainingObligationAmountVnd": 0
}
```

`decision` values:
| Value | Mô tả | `remainingObligation` |
|-------|-------|----------------------|
| `Completed` | Đủ căn cứ, hoàn tất | 0 |
| `CorrectionRequired` | Chuyển lỗi/thiếu, TO cần khắc phục | > 0 |
| `MoreEvidenceRequired` | Chưa đủ, giữ Disputed | null |

**Khi `Completed`:** `CompletionMethod=AdminResolved`; hạch toán commission adjustment; notification cả hai bên.  
**Khi `CorrectionRequired`:** Lưu `remainingObligationAmountVnd`; TO có 72h liên tục để chuyển bổ sung (Section 13 decision 5).  
**Khi `MoreEvidenceRequired`:** Giữ `Disputed`; lưu ghi chú; không tạo credit.

**Response 200:**
```json
{
  "refundCaseId": "drc_123",
  "decision": "Completed",
  "completionMethod": "AdminResolved",
  "resolvedAtUtc": "2026-11-25T14:00:00Z",
  "version": 8
}
```

**Errors:**
- 409 `direct_refund.version_conflict`
- 409 `direct_refund.duplicate_operation`
- 409 `direct_refund.invalid_state` (không phải `Disputed`/`AwaitingAdminReview`)
- 422 `direct_refund.amount_invalid`

---

### POST `/api/v1/admin/direct-refund-disputes/{refundCaseId}/late-review-decisions`

**Auth:** `Administrator` với quyền `DirectRefundDisputeReview`  
**Header:** `Idempotency-Key: <UUID>`  
**Mô tả:** Xử lý late-review request từ Traveler sau auto-completion. Không tự đảo trạng thái gốc; điều chỉnh bằng linked event.

**Request body:**
```json
{
  "operationId": "uuid-v4",
  "lateReviewId": "lr_123",
  "decision": "AdjustmentGranted",
  "decisionReason": "string (required, 10–2000 chars)",
  "adjustmentAmountVnd": 100000,
  "evidenceIds": ["evid_xyz"]
}
```

`decision` values: `AdjustmentGranted` | `Dismissed`

**Response 200:**
```json
{
  "lateReviewId": "lr_123",
  "decision": "AdjustmentGranted",
  "decidedAtUtc": "2026-11-28T10:00:00Z"
}
```

---

## Group 7 — TO: Commission Statements

### GET `/api/v1/operator/commission-statements`

**Auth:** `TourOperator`  
**Query params:** `year`, `month`, `status`, `pageNumber=1`, `pageSize=12`

**Response 200:**
```json
{
  "items": [
    {
      "statementId": "cs_456",
      "periodYear": 2026,
      "periodMonth": 11,
      "status": "Issued",
      "totalEligibleBaseVnd": 6100000,
      "totalCommissionVnd": 610000,
      "totalAdjustmentsVnd": -50000,
      "netDueVnd": 560000,
      "issuedAtUtc": "2026-12-01T00:00:00Z",
      "dueAtUtc": "2026-12-08T00:00:00Z",
      "paidAmountVnd": 0,
      "isOverdue": false
    }
  ],
  "totalCount": 3,
  "pageNumber": 1,
  "pageSize": 12
}
```

**Statement status values:** `Draft` | `Issued` | `PartiallyPaid` | `Paid` | `Overdue` | `Disputed`

---

### GET `/api/v1/operator/commission-statements/{statementId}`

**Auth:** `TourOperator` (chủ statement)  
**Response 200:** Full statement với line items:

```json
{
  "statementId": "cs_456",
  "lines": [
    {
      "lineId": "csl_789",
      "bookingId": 456,
      "tourTitle": "Đà Nẵng 3N2Đ",
      "completedAtUtc": "2026-11-20T00:00:00Z",
      "paidAmountVnd": 1000000,
      "completedRefundsVnd": 390000,
      "eligibleBaseVnd": 610000,
      "snapshotRatePercent": 10.0,
      "commissionVnd": 61000,
      "adjustments": []
    }
  ],
  "totalCommissionVnd": 610000,
  "netDueVnd": 560000,
  "dueAtUtc": "2026-12-08T00:00:00Z",
  "allowedActions": ["pay"]
}
```

---

### POST `/api/v1/operator/commission-statements/{statementId}/payment-link`

**Auth:** `TourOperator` (chủ statement)  
**Header:** `Idempotency-Key: <UUID>`  
**Mô tả:** TO tạo link PayOS để trả hoa hồng cho TripMate. Dùng kênh TripMate, `purpose=CommissionCollection`.

**Request body:** `{}` (amount lấy từ `netDueVnd` trên statement — không nhận từ client)

**Response 200:**
```json
{
  "paymentLinkId": "pl_comm_123",
  "checkoutUrl": "https://pay.payos.vn/web/abc...",
  "amountVnd": 560000,
  "expiresAtUtc": "2026-12-01T00:30:00Z",
  "purpose": "CommissionCollection"
}
```

**Errors:**
- 409 `commission.already_paid`
- 409 `commission.invalid_state`
- 422 `payment_channel.not_ready` (TripMate channel lỗi)

---

## Group 8 — Admin: Commission Statements

### GET `/api/v1/admin/commission-statements`

**Auth:** `Administrator`  
**Query params:** `operatorId`, `year`, `month`, `status`, `isOverdue`, `pageNumber=1`, `pageSize=20`  
**Response 200:** Giống operator list nhưng bao gồm cả `operatorId`, `operatorName`.

---

### GET `/api/v1/admin/commission-statements/{statementId}`

**Auth:** `Administrator`  
**Response 200:** Full detail như TO view + thêm `operatorInfo`, `adjustmentHistory`.

---

## Boundary rules: 72h và 7 ngày

### 72h Transfer Deadline (Section 13 decision 1)

```
transferDeadlineUtc = recipientConfirmedAtUtc + 72h (continuous, all days)
```

- Tính 24/7, kể cuối tuần/lễ. **Không dùng lịch ngày làm việc.**
- Bắt đầu khi: `amount` đã chốt **VÀ** `recipient` hợp lệ đã xác nhận.
- Cờ `isOverdue = (now > transferDeadlineUtc && status == AwaitingTransfer)`.
- Quá hạn: tạm dừng nhận booking mới, vẫn xử lý booking/refund cũ.

### 72h Traveler Confirmation Window (Section 13 decision 1)

```
confirmationDeadlineUtc = reportedAtUtc + 72h (continuous, all days)
```

- `reportedAtUtc` = server time khi TO gọi POST transfers — không phải `transferredAtLocal` TO nhập.
- `now < confirmationDeadlineUtc`: Traveler có quyền submit response.
- `now >= confirmationDeadlineUtc`: Deadline processor tự hoàn tất nếu chưa có response (`AutoCompletedAfter72Hours`).
- Traveler response và deadline job dùng cùng optimistic lock — chỉ một thắng.
- Chỉnh sửa evidence **không reset** `confirmationDeadlineUtc`.
- Sau dispute, **không** tự mở lại cửa sổ 72h mới.

### 72h Supplemental Transfer (Section 13 decision 5)

```
supplementalDeadlineUtc = adminDecisionUtc + 72h (continuous, all days)
```

- Áp dụng khi Admin ra quyết định `CorrectionRequired`.

### 7-day Late Review Window (Section 13 decision 6)

```
lateReviewWindowClosesAtUtc = completedAtUtc + 7 days (continuous)
```

- Chỉ áp dụng khi `completionMethod = AutoCompletedAfter72Hours`.
- Server-side check; client countdown chỉ hiển thị, không quyết định quyền.

### 7-day Commission Invoice Due (Section 13 decision 8)

```
dueAtUtc = issuedAtUtc + 7 days (continuous, same hour)
```

- Quá hạn: `status = Overdue`, tạm dừng nhận booking mới cho TO đó.
- Mở lại sau khi thanh toán đủ được xác minh qua webhook.

---

## DTO conventions

### Pagination

```json
{
  "items": [...],
  "totalCount": 100,
  "pageNumber": 1,
  "pageSize": 20
}
```

### Decimal / Money

- Tất cả số tiền VND: `long` (đồng nguyên), suffix `Vnd` trong field name.
- Rate: `decimal` với 4 chữ số thập phân (e.g., `10.0000`).
- Rounding: `roundHalfUp` per booking, không round độc lập từng adjustment.

### Timestamps

- Tất cả timestamp: UTC ISO 8601 string, suffix `Utc` (e.g., `reportedAtUtc`).
- Local time do user nhập: suffix `Local` với timezone offset (e.g., `transferredAtLocal`).
- Response luôn kèm `serverNowUtc` trên các màn có countdown.

### allowedActions

Mảng string trong response cho client biết hành động nào hiện hợp lệ:

| Value | Áp dụng cho |
|-------|-------------|
| `confirm_recipient` | Traveler, `AwaitingRecipient` |
| `submit_response` | Traveler, `AwaitingTravelerConfirmation` |
| `submit_late_review` | Traveler, `Completed` (AutoCompleted) + trong 7 ngày |
| `report_transfer` | TO, `AwaitingTransfer` |
| `upload_evidence` | TO, trước hoặc khi báo chuyển |
| `resolve` | Admin, `Disputed`/`AwaitingAdminReview` |
| `pay` | TO, statement `Issued`/`Overdue`/`PartiallyPaid` |
| `reconfigure` | TO, channel bất kỳ trạng thái |
| `disable` | TO, channel `Ready` |

### Version / Optimistic concurrency

Mọi mutation nhận `expectedVersion: int` và trả `version: int` mới.  
Mismatch → 409 `version_conflict`.

---

## Endpoints không thay đổi (giữ contract legacy)

Các endpoint sau giữ nguyên contract, chỉ thêm `paymentFlow` trong response để client distinguish:

| Endpoint | Thêm field |
|----------|-----------|
| `GET /api/v1/bookings/{id}/refund` | `paymentFlow: "PlatformCollectedLegacy" | "OperatorDirect"` |
| `POST /api/v1/bookings/{id}/cancellation` | (không thay đổi request; routing xảy ra nội bộ) |
| `POST /api/v1/payments/payos/webhook` | (đã mô tả ở Group 2) |
| `GET /api/v1/operator/refunds` | (legacy — giữ nguyên) |
| `POST /api/v1/operator/refunds/{id}/confirm` | (legacy — giữ nguyên) |
| `POST /api/v1/operator/refunds/{id}/return` | (legacy — giữ nguyên) |

---

## Checklist trước FE/Mobile implementation

- [ ] Tất cả endpoint trong file này được review và approve bởi owner
- [ ] FE (Next.js) confirm: routes, DTO fields, error codes khớp
- [ ] Mobile (Flutter) confirm: routes, DTO fields, error codes khớp
- [ ] `serverNowUtc` được dùng để tính countdown — không dùng `Date.now()` phía client
- [ ] `allowedActions` được dùng để ẩn/hiện nút — không tự suy luận state phía client
- [ ] Evidence download qua endpoint có auth — không dùng URL public/signed URL
- [ ] Secret fields (clientId, apiKey, checksumKey) không bao giờ xuất hiện trong response
- [ ] Idempotency-Key header được implement ở tất cả mutation có side-effect tài chính
