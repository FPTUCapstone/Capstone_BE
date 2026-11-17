# DAILY-PAYOUT-TRANSFER — Kiểm kê code và DB payout hiện có (Task 0)

Chỉ đọc. Chưa xóa, sửa code nào. Trạng thái: chờ developer xác nhận bảng phân loại trước khi Task 1.

Nhánh: `feature/linhnv-daily-payout-transfer` (từ `origin/develop`, commit `68fa25aa`).

## 1. Điểm quan trọng

1. **Đã có mô hình payout theo yêu cầu rút tiền** (`Payout` với `PendingReview`, `ApprovedForSettlement`, `SettlementProcessing`, `Settled`, `SettlementFailed`, `Rejected`, `Cancelled`). Mô hình này trái với SRS v11 (không còn yêu cầu rút tiền, không còn duyệt). Đây là đối tượng của Task 1.
2. **Tên `PayoutAdjustment` đã được dùng** cho điều chỉnh từ hoàn tiền (`payment.PayoutAdjustments`, gắn với `SourceRefundId`, `AppliedPayoutId`). Spec v2 cũng dùng tên này với nghĩa khác. Cần quyết định: tái sử dụng hay đổi tên thành `PayoutSettlementAdjustment`.
3. **Trạng thái đã có khác spec.** Code dùng `SettlementProcessing`, `ApprovedForSettlement`, `PendingReview`. Spec v2 dùng `Processing`, `Scheduled`, `Carried Over`. Cần chốt tên trạng thái trước khi viết state machine (Task 3).
4. **Snapshot hoa hồng đã có ở mức giao dịch, không phải booking.** `PaymentTransaction.CommissionRateApplied` được ghi khi thanh toán hoàn tất (`PaymentCompletionProcessor`, dòng 124). `PayoutItem` và `PayoutRefundItem` cũng có `CommissionRateApplied`. Spec v2 (PD-3, Task 2) yêu cầu chụp trên booking. Đề xuất: dùng snapshot có sẵn trên giao dịch thanh toán, không thêm cột mới trên booking.
5. **Có sẵn cơ chế background service** (`AddHostedService` trong `DependencyInjection.cs`, ví dụ `PendingBookingExpiryBackgroundService`, `RefundProcessingBackgroundService`). Task 6 dùng cùng mẫu này cho lượt 10:00.
6. **Có sẵn audit log** (`AuditActionTypes`, `AuditEntityTypes`, `AdminAuditLogsController`). Task 2C dùng lại, không tạo mới.
7. **Có sẵn cấu hình payout** (`PayoutConfigKeys`: `payout.minimum_amount_vnd`, `payout.refund_hold_days`). Cần xác nhận có còn dùng sau khi bỏ mô hình rút tiền.

## 2. Bảng kiểm kê (đề xuất phân loại)

Ký hiệu: **Giữ** = dùng lại nguyên; **Sửa** = giữ nhưng đổi; **Bỏ** = xóa theo Task 1; **Chờ** = cần quyết định.

### 2.1. Domain entity

| File | Nội dung | Đề xuất |
|---|---|---|
| `Domain/Entities/Payout.cs` | Yêu cầu rút tiền (request) với trạng thái cũ | **Bỏ** hoặc **Chờ**: nếu settlement mới thay thế, bỏ; nếu dùng làm đơn vị lệnh gom (`PayoutBatch`), sửa |
| `Domain/Entities/PayoutItem.cs` | Dòng booking trong một yêu cầu rút tiền | **Chờ**: tương ứng `PayoutSettlement` (booking-level), có thể sửa |
| `Domain/Entities/PayoutRefundItem.cs` | Dòng hoàn tiền trong yêu cầu rút tiền | **Bỏ** (theo mô hình cũ) |
| `Domain/Entities/PayoutPeriodBreakdown.cs` | Tách theo kỳ (tháng) | **Bỏ** (SRS v11 không tính theo tháng) |
| `Domain/Entities/PayoutAdjustment.cs` | Điều chỉnh từ hoàn tiền, gắn với `Payout` | **Chờ**: xem điểm 2 |

### 2.2. Application: luồng yêu cầu rút tiền của TO

| Thư mục | Nội dung | Đề xuất |
|---|---|---|
| `Features/Operator/Payouts/RequestPayout/` | `RequestPayoutCommand`, handler | **Bỏ** (Task 1) |
| `Features/Operator/Payouts/CancelPayout/` | Hủy yêu cầu rút tiền | **Bỏ** (Task 1) |
| `Features/Operator/Payouts/GetSettlementMonths/` | Liệt kê kỳ tính theo tháng | **Bỏ** (Task 1) |
| `Features/Operator/Payouts/GetAvailableBalance/` | Số dư khả dụng để rút | **Bỏ** (Task 1) |
| `Features/Operator/Payouts/GetHistory/` | Lịch sử yêu cầu rút tiền | **Sửa**: đổi thành lịch sử settlement của TO (PD-13) |
| `Features/Operator/Payouts/BankAccount/` | Tài khoản ngân hàng của TO | **Giữ** (cần để chuyển tiền, PD-8) |
| `Features/Operator/Payouts/Common/OperatorPayoutCalculator.cs` | Công thức theo tháng | **Chờ**: công thức per-booking mới ở Task 4 có thể thay; cần đối chiếu với `CommissionRateApplied` |
| `Features/Operator/Payouts/Common/OperatorPayoutEligibilityReader.cs` | Lọc booking đủ điều kiện rút | **Sửa**: đổi thành lọc booking đủ điều kiện cho lượt chạy (PD-2). Ghi chú: nhánh post-trip-refund cũng sửa file này |
| `Features/Operator/Payouts/Common/PayoutConfigKeys.cs` | `minimum_amount_vnd`, `refund_hold_days` | **Chờ**: hai khóa này thuộc mô hình cũ |
| `Features/Operator/Payouts/Common/OperatorPayoutErrorCodes.cs`, `IPayoutPersistenceErrorClassifier.cs`, `BankAccountMasking.cs` | Mã lỗi, phân loại lỗi DB, che số tài khoản | **Giữ** phần dùng được, **Sửa** mã lỗi theo luồng mới |
| `Features/Operator/Revenue/` | Báo cáo doanh thu (UC-44) | **Giữ** (ngoài phạm vi) |

### 2.3. Application: luồng duyệt của Admin

| Thư mục | Nội dung | Đề xuất |
|---|---|---|
| `Features/Admin/Payouts/Approve/` | Duyệt yêu cầu rút tiền | **Bỏ** (Task 1) |
| `Features/Admin/Payouts/Reject/` | Từ chối yêu cầu rút tiền | **Bỏ** (Task 1) |
| `Features/Admin/Payouts/GetList/` | Danh sách yêu cầu | **Sửa**: đổi thành danh sách settlement (UC-72) |
| `Features/Admin/Payouts/GetDetail/` | Chi tiết yêu cầu | **Sửa**: đổi thành chi tiết settlement (UC-73) |
| `Features/Admin/Payouts/Common/PayoutErrorCodes.cs` | Mã lỗi | **Sửa** |
| `Features/Bookings/Refunds/CompleteManualRefundCommand.cs` | Hoàn tiền thủ công | **Chờ**: thuộc nhánh hoàn tiền, không đổi ở nhánh này |

### 2.4. Infrastructure và API

| File | Nội dung | Đề xuất |
|---|---|---|
| `Api/Controllers/V1/OperatorPayoutsController.cs` | Endpoint TO | **Sửa**: bỏ endpoint yêu cầu rút tiền, thêm endpoint lịch sử settlement (PD-13) |
| `Api/Controllers/V1/AdminPayoutsController.cs` | Endpoint Admin | **Sửa**: bỏ duyệt và từ chối, thêm đọc settlement và xử lý thất bại (UC-72, UC-73) |
| `Infrastructure/Persistence/Configurations/PayoutConfiguration.cs`, `PayoutItemConfiguration.cs`, `PayoutRefundItemConfiguration.cs`, `PayoutPeriodBreakdownConfiguration.cs`, `PayoutAdjustmentConfiguration.cs` | EF mapping | **Sửa** hoặc **Bỏ** theo quyết định ở mục 2.1 |
| `Infrastructure/Persistence/ApplicationDbContext.cs`, `IApplicationDbContext.cs` | DbSet | **Sửa** theo quyết định |
| `Infrastructure/Services/Payment/PayOsPaymentGateway.cs` | Cổng PayOS (thanh toán) | **Giữ**: không phải cổng chuyển tiền; adapter giả lập là file mới |
| `Infrastructure/DependencyInjection.cs` | Đăng ký service, background service | **Sửa**: thêm `PayoutTransferBackgroundService` và adapter giả lập |

### 2.5. Migration SQL

| File | Nội dung | Đề xuất |
|---|---|---|
| `20261012_add_payout_lifecycle_foundation.sql` | Vòng đời payout (nền) | **Chờ**: kiểm tra bảng và cột có dùng lại được không |
| `20261019_uc44_revenue_payment_snapshots.sql` | Snapshot cho báo cáo doanh thu | **Giữ** |
| `20261022_uc46_payout_settlement_request.sql` | `payment.PayoutRefundItems`, `payment.PayoutAdjustments`, bảng yêu cầu rút tiền | **Chờ**: bảng yêu cầu rút tiền và `PayoutRefundItems` thuộc mô hình cũ. Không xóa bảng trên DB dev trước khi developer xác nhận; nếu đã chạy thì tạo migration gỡ |
| `20261023_uc46_payout_available_balance.sql` | Số dư khả dụng | **Bỏ** (theo mô hình cũ), cần xác nhận |
| `20261022_add_refund_manual_completion_operations.sql` | Hoàn tiền thủ công | **Giữ** (nhánh hoàn tiền) |
| `20261028_uc70_emergency_cancellation.sql` | Hủy khẩn cấp | **Giữ** |

### 2.6. Test

| Nhóm | Đề xuất |
|---|---|
| `tests/.../Features/Admin/Payouts/*` (Approve, Reject, GetDetail, GetList) | **Bỏ** cùng code tương ứng, **Sửa** phần còn lại |
| `tests/.../Features/Operator/Payouts/RequestPayout/`, `CancelPayout/`, `GetAvailableBalance/`, `GetSettlementMonths/` | **Bỏ** (Task 1) |
| `tests/.../Features/Operator/Payouts/BankAccount/` | **Giữ** |
| `tests/.../Common/OperatorPayoutCalculatorTests.cs` | **Chờ** theo `OperatorPayoutCalculator` |
| `tests/.../Domain/PayoutAdjustmentTests.cs`, `PayoutEntityTests.cs` | **Chờ** theo quyết định entity |
| `tests/TripMate.Api.IntegrationTests/Payouts/*` (lifecycle, concurrency, endpoints, OpenAPI, SQL Server) | **Sửa** hoặc **Bỏ**, cần chạy lại sau Task 1 |
| `tests/TripMate.Infrastructure.UnitTests/Persistence/PayoutPersistenceModelTests.cs` | **Chờ** |

## 3. Quyết định cần developer xác nhận trước Task 1

1. **Payout cũ (request-based) bỏ hoàn toàn, hay giữ làm nền cho `PayoutBatch`?** Đề xuất: bỏ, vì mô hình khác về đơn vị (lệnh gom theo ngày, không theo yêu cầu).
2. **Tên `PayoutAdjustment`.** Đề xuất: đổi tên mới của bên settlement thành `PayoutSettlementAdjustment` để không đụng bảng `payment.PayoutAdjustments` (dùng cho hoàn tiền khiếu nại, nhánh khác).
3. **Tên trạng thái.** Đề xuất: dùng tên trong spec (`Scheduled`, `Processing`, `Settled`, `SettlementFailed`, `Carried Over`) cho settlement mới, và bỏ tên cũ khi bỏ entity cũ.
4. **Snapshot hoa hồng.** Đề xuất: dùng `PaymentTransaction.CommissionRateApplied` đã có, không thêm cột trên booking. Sửa spec và plan cho khớp.
5. **Bảng DB dev.** Có bảng nào đã có dữ liệu cần giữ không (`payment.Payouts`, `payment.PayoutItems`)? Nếu có, cần migration gỡ thay vì xóa.

## 4. Việc tiếp theo

Sau khi developer xác nhận các điểm trên và bảng phân loại, mình sẽ cập nhật plan (Task 1 và Task 2 theo tên đã chốt) và bắt đầu Task 1 với test đỏ trước.

## 5. Dọn phần còn lại sau Task 12 (2026-11-08)

Task 1 chỉ gỡ phần ghi (request/cancel/approve/reject). Phần đọc cũ (Admin GetList/GetDetail,
Operator GetHistory) và các entity `Payout`/`PayoutItem`/`PayoutRefundItem`/`PayoutPeriodBreakdown`
bị để lại "Chờ". Sau khi nhánh FE gỡ toàn bộ màn hình UC-46 (Task 0 của FE), không còn gì gọi
các endpoint đọc này và không có code mới nào đọc các entity này — developer xác nhận bỏ hoàn
toàn (mục 3, quyết định 1):

- Gỡ `Payout.cs`, `PayoutItem.cs`, `PayoutRefundItem.cs`, `PayoutPeriodBreakdown.cs`; **giữ**
  `PayoutAdjustment.cs` (thuộc nhánh hoàn tiền thủ công, không liên quan).
- Gỡ `Admin/Payouts/GetList`, `Admin/Payouts/GetDetail`, `Admin/Payouts/Common/PayoutErrorCodes`,
  `Operator/Payouts/GetHistory`, `Operator/Payouts/Common/{OperatorPayoutCalculator,
  OperatorPayoutEligibilityReader,PayoutConfigKeys}` (không còn nơi gọi).
- Gỡ `AdminPayoutsController` (toàn bộ); gỡ action `History` trong `OperatorPayoutsController`
  (giữ `bank-account`).
- Gỡ luôn `PayoutReviewConcurrencySqlServerTests.cs` — test này lẽ ra phải gỡ ở Task 1 (test
  approve/reject đã bị xóa) nhưng bị bỏ sót.
- Migration mới `20261108_remove_legacy_payout_request_tables.sql`: xóa FK từ
  `PayoutAdjustments.applied_payout_id` sang `Payouts`, xóa bảng `payment.Payouts`,
  `PayoutItems`, `PayoutRefundItems`, `PayoutPeriodBreakdowns`, và hai dòng cấu hình
  `payout.minimum_amount_vnd`/`payout.refund_hold_days` không còn ai đọc. Đã áp dụng và kiểm tra
  idempotent (chạy lại lần 2 không lỗi) trên DB dev; `payment.PayoutAdjustments` không bị đụng.
- `dotnet build` và `dotnet test` toàn bộ solution xanh sau khi gỡ.
