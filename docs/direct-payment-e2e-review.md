# E2E Review — Direct Operator Payment / Refund / Commission

Ngày cập nhật: 25/11/2026. Branch: `fix/linhnv-payout-and-refund`.

> **Bảo mật:** Tiền thật E2E chỉ chạy với tài khoản, giá trị và người nhận được chủ hệ thống cho phép riêng. Tài liệu này mô tả kịch bản và bằng chứng cần thu thập — không tự phát sinh giao dịch trả tiền.

---

## 1. Điều kiện tiên quyết

| Hạng mục | Yêu cầu |
|---|---|
| Môi trường | Staging (không production) có feature flag `OperatorDirect.Enabled = true` |
| Tài khoản test | 2 TourOperator (TO-A, TO-B), 2 Traveler, 1 Administrator, 1 Staff |
| Kênh thanh toán | TO-A và TO-B đã đăng ký kênh payOS riêng; trạng thái `Ready`; đã Admin-duyệt |
| Giá trị tiền thật | Chỉ dùng số tiền tối thiểu được chủ hệ thống phê duyệt riêng bằng văn bản |
| Clock test | Dùng `IDateTimeProvider` override trong test harness riêng để mô phỏng 72h; không sửa clock production/payOS thật |
| Backup | Snapshot DB trước khi chạy; rollback script sẵn sàng |

---

## 2. Kịch bản E2E

### 2.1 Luồng thanh toán thành công (TO-A)

**Mục tiêu:** Traveler thanh toán qua kênh TO-A, booking Confirmed, vé QR hợp lệ.

Bước kiểm tra:
1. Traveler tạo booking tour của TO-A → `POST /api/v1/traveler/bookings`
2. Traveler tạo payment link → `POST /api/v1/traveler/payments/create-link`
   - Verify: `PaymentFlow = OperatorDirect`, link trỏ đúng payOS channel TO-A
3. Mô phỏng payOS webhook `PAYMENT_SUCCESS` với chữ ký hợp lệ
4. Verify: booking `Status = Confirmed`, `PaymentFlow = OperatorDirect`
5. Verify: vé QR trả về tại `GET /api/v1/traveler/bookings/{id}/qr-ticket`

**Bằng chứng cần thu:** screenshot booking status, QR code (che thông tin cá nhân), webhook log.

---

### 2.2 Hai TO độc lập không lẫn kênh

**Mục tiêu:** Webhook TO-B không xử lý được booking TO-A và ngược lại.

Bước kiểm tra:
1. Tạo booking cho TO-A và TO-B tương ứng
2. Gửi webhook `PAYMENT_SUCCESS` của TO-B với `orderCode` của booking TO-A
3. Verify: booking TO-A vẫn `Pending`; log ghi nhận `channel_mismatch`

**Bằng chứng cần thu:** log webhook error, booking status không đổi.

---

### 2.3 Refund — Traveler confirm nhận đủ

**Mục tiêu:** TO báo chuyển tiền, Traveler xác nhận nhận đủ → Case Completed.

Bước kiểm tra:
1. Tạo `DirectRefundCase` nguồn `PolicyCancellation` cho booking của TO-A
   - Verify: `Status = AwaitingRecipient`
2. Traveler xác nhận tài khoản nhận: `PUT /api/v1/traveler/direct-refunds/{id}/recipient`
   - Verify: `Status = AwaitingTransfer`
3. TO upload bằng chứng chuyển tiền (1–3 file ≤ 5MB): `POST /api/v1/operator/direct-refunds/{id}/evidence`
4. TO báo chuyển: `POST /api/v1/operator/direct-refunds/{id}/transfers`
   - Verify: `Status = AwaitingTravelerConfirmation`, `ConfirmationDeadlineAtUtc` đúng +72h
   - Verify: Traveler nhận notification
5. Traveler confirm nhận đủ: `POST /api/v1/traveler/direct-refunds/{id}/response` body `ResponseType = ReceivedInFull`
   - Verify: `Status = Completed`, `CompletionMethod = TravelerConfirmed`

**Bằng chứng cần thu:** ảnh chụp case detail sau mỗi bước (che số tài khoản, số tiền thật nếu > cho phép).

---

### 2.4 Refund — Traveler tranh chấp → Admin giải quyết

**Mục tiêu:** Traveler báo chưa nhận, Admin xét và xác nhận hoàn tất.

Bước kiểm tra:
1. Lặp bước 1–4 của 2.3
2. Traveler phản hồi `NotReceived`: `POST /api/v1/traveler/direct-refunds/{id}/response`
   - Verify: `Status = Disputed`
3. Admin xem case: `GET /api/v1/admin/direct-refund-disputes/{id}`
   - Verify: evidence hiển thị, tài khoản nhận che 4 số cuối
4. Admin resolve `Completed`: `POST /api/v1/admin/direct-refund-disputes/{id}/resolve`
   - Verify: `Status = Completed`, `CompletionMethod = AdminResolved`

**Bằng chứng cần thu:** trạng thái case tại bước tranh chấp và sau giải quyết.

---

### 2.5 Auto-complete 72h (clock-controlled test)

**Mục tiêu:** Processor tự hoàn case sau 72h khi Traveler không phản hồi.

Bước kiểm tra (dùng test harness với `IDateTimeProvider` override, không production clock):
1. Lặp bước 1–4 của 2.3; `ConfirmationDeadlineAtUtc = now + 72h`
2. Chạy `DirectRefundDeadlineProcessor.ProcessOverdueAsync()` với clock đặt tại `deadline + 1 phút`
3. Verify: `Status = Completed`, `CompletionMethod = AutoExpired`
4. Verify: không ghi thêm transfer; case không mở lại

**Bằng chứng cần thu:** log processor, trạng thái case sau sweep.

---

### 2.6 Staff chỉ xem, không có quyền mutation

**Mục tiêu:** Staff account không thể gọi PUT/POST trên direct-refund endpoints.

Bước kiểm tra:
1. Dùng Staff JWT gọi `PUT /api/v1/traveler/direct-refunds/{id}/recipient` → expect 403
2. Dùng Staff JWT gọi `POST /api/v1/operator/direct-refunds/{id}/transfers` → expect 403
3. Dùng Staff JWT gọi `POST /api/v1/admin/direct-refund-disputes/{id}/resolve` → expect 403
4. Verify Staff vẫn đọc được `GET /api/v1/admin/direct-refund-disputes/{id}` → expect 200

**Bằng chứng cần thu:** HTTP 403 responses, 200 response cho GET.

---

### 2.7 Commission statement và thanh toán invoice

**Mục tiêu:** Admin phát hành statement, TO thanh toán qua payOS TripMate channel.

Bước kiểm tra:
1. Admin phát hành: `POST /api/v1/admin/commission-statements` body `{TourOperatorUserId, Period}`
   - Verify: `CommissionAmountVnd = round_half_up(eligibleBase × rate)`
2. TO tạo payment link: `POST /api/v1/operator/commission-statements/{id}/payment-link`
   - Verify: link dùng TripMate payOS key, không dùng TO key
3. Mô phỏng webhook TripMate `PAYMENT_SUCCESS`
4. Verify: `Status = Paid`, không lẫn webhook booking

**Bằng chứng cần thu:** statement detail trước/sau thanh toán, verify webhook routing tách biệt.

---

### 2.8 Legacy flow không bị ảnh hưởng

**Mục tiêu:** Booking `PlatformCollectedLegacy` vẫn xử lý đúng song song.

Bước kiểm tra:
1. Tạo booking `PlatformCollectedLegacy` (nếu tour TO không active OperatorDirect)
2. Chạy toàn bộ luồng thanh toán và refund legacy
3. Verify: `PaymentFlow` không bị đổi sang `OperatorDirect`; payout worker legacy vẫn xử lý

**Bằng chứng cần thu:** `PaymentFlow` trên booking, payout settlement log.

---

### 2.9 Feature flag tắt

**Mục tiêu:** Khi `OperatorDirect.Enabled = false`, không thể tạo mới direct booking.

Bước kiểm tra:
1. Set `SystemConfigs.ConfigValue = 'false'` cho `OperatorDirect.Enabled`
2. Thử tạo booking qua kênh OperatorDirect → expect 422 hoặc fallback policy
3. Verify: booking OperatorDirect cũ đang pending vẫn được xử lý
4. Set lại `true` và confirm flow hoạt động lại

**Bằng chứng cần thu:** HTTP response khi flag off, trạng thái booking cũ.

---

## 3. Danh sách bằng chứng cần nộp

| # | Kịch bản | Bằng chứng |
|---|---|---|
| 2.1 | Payment success | Booking Confirmed + QR + webhook log |
| 2.2 | Channel isolation | Log mismatch error |
| 2.3 | Refund accept | Case Completed TravelerConfirmed |
| 2.4 | Refund dispute | Case Completed AdminResolved |
| 2.5 | Auto-complete 72h | Processor log + case status |
| 2.6 | Staff read-only | 403 + 200 responses |
| 2.7 | Commission invoice | Statement + payment log |
| 2.8 | Legacy coexistence | Booking PaymentFlow + payout log |
| 2.9 | Feature flag | Error response + old booking unaffected |

Tất cả ảnh chụp màn hình: **che số tài khoản ngân hàng, số tiền thật nếu vượt giới hạn được phép, và thông tin định danh cá nhân** trước khi lưu bằng chứng.

---

## 4. Giới hạn phạm vi E2E này

- Không chạy trên production database.
- Không tự phát sinh giao dịch trả tiền thật khi review tài liệu.
- Thời hạn 72h kiểm thử qua `IDateTimeProvider` override trong test harness riêng biệt.
- Tiền thật chỉ dùng số tiền tối thiểu được chủ hệ thống xác nhận bằng văn bản trước khi chạy.
- Webhook production payOS không được gọi từ test harness.
