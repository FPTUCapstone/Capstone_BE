# UC-41 / UC-42 / UC-67 — Căn chỉnh luồng hoàn tiền theo SRS (BE) — Spec v1.1 (đã chốt Q1–Q4)

- Nguồn chuẩn: `SRS_rev10 (5).docx` (12/11/2026). Các quyết định chủ sở hữu được áp dụng: 10/11 (BR-187, BR-188, BR-189 bản đầu), 11/11 (BR-135 bậc hoàn mới, BR-157 bị bãi bỏ, chia 70/30 + 20, BR-143 không còn đường hoàn trực tiếp, BR-189 khóa tự động 15 ngày), 12/11.
- Phạm vi: luồng hoàn tiền khi hủy booking (UC-41), xác nhận hoàn tiền của Tour Operator (UC-42), và hoàn tiền cho Traveler (UC-67) — chỉ phần BE.
- Ngoài phạm vi (đợt sau): UC-68/69/75 (khiếu nại và đề xuất hoàn một phần — BE chưa có module complaint), UC-66 (hủy tour do thời tiết, BR-186 hoàn 100% ngay). Điểm nối với UC-66 được ghi trong mục 6.
- Phụ thuộc: BR-139 (tỷ lệ chia của TO) cần số tiền đã chuyển cho TO theo UC-71 (Advance/Final), và BR-144/188/189 cần bảng `TourOperatorDebt`. Cả hai đã có trên develop qua PR #155 (merge `5716e653`, develop hiện ở `999e6af6`) — xem mục 8.

Đối chiếu code: nhánh `fix/linhnv-daily-payout-transfer` (đã chứa commit của PR #155).

---

## 1. Đánh giá hiện trạng: code đang sai ở đâu

| # | Mức | Quy định SRS | Hiện trạng code | Vị trí |
|---|---|---|---|---|
| G1 | Nghiêm trọng | **BR-143**: không có đường hoàn trực tiếp. Mọi hoàn tiền từ UC-41 phải qua UC-42 (TO xác nhận + TO trả phần chia) rồi mới đến UC-67. UC-41 "does not call the payment gateway" (3.8.4.2, BR-76). | Hủy booking tạo `Refund` ở trạng thái `Pending`, và `RefundProcessor` tự động nhặt và gửi sang gateway ngay trong sweep, không cần TO xác nhận. | `CancelBookingCommandHandler.cs:118`; `RefundProcessor.cs:28` (`ProcessDueRefundsAsync` lọc `Pending`/`RetryPending`); `Refund.cs:62` (`TryStartAttempt`) |
| G2 | Nghiêm trọng | **UC-42 (3.8.4.3)**: TO xác nhận hoàn tiền → `Awaiting Partner Return` (hạn 3 ngày làm việc, BR-144) → TO báo đã chuyển phần chia kèm mã tham chiếu → `Return Reported` → Staff ghi nhận đã nhận đủ → `Return Received` → kích hoạt UC-67. | Không có các trạng thái này, không có endpoint để TO xác nhận / báo chuyển khoản, không có thao tác Staff ghi nhận đã nhận. `RefundStatus` chỉ có `Pending, Processing, RetryPending, ManualReview, Processed, Failed, Rejected`. | `RefundStatus.cs`; `BookingsController.cs` (không có route nào của UC-42); FE dùng luồng "Initiate Refund" cũ, chạy ở chế độ `NO_BACKEND` | `src/features/operator/bookings/services/operatorBookingService.ts:378-392` |
| G3 | Cao | **BR-139**: phần chia của TO = `refund × đã chuyển cho TO / PaidAmount`, làm tròn xuống VND; 0 trước Advance, 70% sau Advance, 90% sau Final; phần còn lại TripMate tự trả. Khi phần chia = 0 thì hoàn ngay khi xác nhận (4.b1). | Không có phép tính này ở đâu trong BE. | — (không tìm thấy) |
| G4 | Cao | **BR-157 đã bị bãi bỏ (11/11)**: "the 24-hour full-refund rule is removed". | Vẫn còn rule: thanh toán trong vòng 24 giờ thì hoàn 100% bất kể thời điểm hủy. | `BookingCancellationRefundRules.cs:22` |
| G5 | Cao | **BR-135 (11/11)**: >14 ngày 100%; 10–14 ngày 50%; <10 ngày 0% (có cảnh báo, xác nhận riêng — MSG170). | Ngưỡng cứng là 14 ngày / 7 ngày / **72 giờ**, bậc thứ ba là 30%. Snapshot mặc định ghi `oneToThreeDaysPercent: 30`. | `TravelerCancellationPolicy.cs:42-44`; `Booking.cs:19` |
| G6 | Cao | **BR-161 / 3.10.2**: khi gửi gateway lỗi, thử lại **mỗi 1 giờ**, tối đa **3 lần thử lại**, sau đó đặt `Escalated` và báo Administrator trong 24 giờ. | Khoảng chờ mặc định 5 phút; `MaxAttempts = 3` tính là tổng số lần thử (không phải số lần thử lại). | `RefundProcessingOptions.cs:9,12` |
| G7 | Cao | **BR-161**: trạng thái `Escalated` + màn hình monitor (screen 101). | Không có `Escalated`. Các trường hợp tương đương bị gán `ManualReview` (ví dụ PayOS luôn trả `manual_review_required`) hoặc `Failed`. | `PayOsPaymentGateway.cs:140-146`; `RefundStatus.cs` |
| G8 | Trung bình | **UC-41 6.a1 / 6.b1**: không có thanh toán thành công → **hủy vẫn thành công**, không tạo RefundRequest, hiện MSG136. Số tiền không khớp → RefundRequest trạng thái `Reconciliation`, escalate Admin, MSG136. | Nếu có số tiền hoàn > 0 mà không có giao dịch thành công thì handler trả về lỗi `PaymentNotRefundable` và **rollback toàn bộ thao tác hủy**. Không có trạng thái `Reconciliation`. | `CancelBookingCommandHandler.cs:96` |
| G9 | Trung bình | **BR-187**: TO không xác nhận trong 3 ngày làm việc → hệ thống tự xác nhận, gửi MSG197. | Không có deadline, không có job tự xác nhận. | — |
| G10 | Trung bình | **BR-144 / BR-188 / BR-189**: phần chưa trả đưa thành nợ, bù trừ vào settlement kế tiếp; khóa TO tự động sau 15 ngày. | Cơ chế nợ và khóa tự động đã có trên develop (`TourOperatorDebt`, `DailyPayoutAutoLock`, PR #155). Phần còn thiếu là refund chưa đặt hạn trả và chưa tạo nợ khi quá hạn. | `TourOperatorDebt.cs`; `DailyPayoutAutoLock.cs`; refund chưa có deadline logic |
| G11 | Ghi chú | **BR-76 + 3.8.4.3**: đường trả tiền về Traveler "bank transfer [pending confirmation]". | PayOS không có API hoàn tiền về người trả → hiện đẩy về vận hành thủ công. SRS còn để ngỏ. | Mục 8 (Q4 đã chốt: giả lập) |
| G12 | Ngoài phạm vi | **BR-158**: hủy một phần người tham gia của booking nhóm, hoàn theo PaidAmount của người bị hủy. | `CancelBookingCommand` không có danh sách người tham gia, chỉ hủy cả booking. | `CancelBookingCommand.cs:5-6` |
| G13 | Cao (khi làm G2) | Cột trạng thái refund dài tối đa 20 ký tự. | Tên trạng thái mới dài hơn 20 ký tự (ví dụ `PendingOperatorConfirmation` = 27), nếu không nới cột thì insert sẽ lỗi. | `RefundConfiguration.cs:21` (`HasMaxLength(20)`) |

Ghi chú G5 về snapshot: `BR-107` yêu cầu snapshot chốt lúc tạo booking, nhưng BR-135 là quy tắc đã đổi theo quyết định chủ sở hữu. Đã chốt Q1: bậc mới chỉ áp cho booking tạo sau 11/11.

---

## 2. Hành vi đích theo SRS (sau khi sửa)

### 2.1 Hủy booking (UC-41)
- Traveler: bậc hoàn theo BR-135 mới (>14 ngày 100%; 10–14 ngày 50%; <10 ngày 0% phải xác nhận lại — MSG170). **Bỏ** quy tắc 24 giờ (BR-157).
- Tour Operator: hoàn 100% số tiền đã thanh toán (BR-136). Yêu cầu hoàn tiền do TO hủy vẫn đi qua UC-42 để TO xác nhận và trả phần chia, đúng như bước 6 của 3.8.4.2 (áp dụng cho cả hai actor).
- Hủy một phần người tham gia (BR-158): chưa hỗ trợ trong đợt này (G12).
- Booking `PendingPayment` hoặc đã hủy không sinh RefundRequest (BR-136, 3.8.4.2).
- Có giao dịch thành công và số tiền khớp → tạo **một** `RefundRequest` trạng thái `PendingOperatorConfirmation`. **Không gọi gateway.**
- Có giao dịch thành công nhưng số tiền không khớp → tạo RefundRequest `Reconciliation`, escalate Admin, vẫn hủy booking, hiện MSG136.
- Không có giao dịch thành công → hủy booking, không tạo RefundRequest, hiện MSG136 (**không** rollback).
- Toàn bộ (hủy, giải phóng chỗ, vô hiệu QR, tạo RefundRequest, audit) trong **một** transaction (BR-137).

### 2.2 Xác nhận hoàn tiền (UC-42)
Vòng đời:

```
PendingOperatorConfirmation ──TO xác nhận──▶ AwaitingPartnerReturn ──TO báo đã chuyển──▶ ReturnReported ──Staff ghi nhận đủ──▶ ReturnReceived ──▶ (UC-67) Processing → Processed
        │                                         │
        └─ quá 3 ngày làm việc (BR-187) ──────────┘  (tự xác nhận, bắt đầu đếm hạn trả)
                                                  └─ quá hạn trả (BR-144) ─▶ nợ TO (bù trừ settlement kế tiếp)
```

- Phần chia TO = `floor(refundAmount × đãChuyểnChoTO / PaidAmount)` (BR-139).
- Nếu phần chia = 0 (chưa có settlement nào được chuyển): bỏ qua `AwaitingPartnerReturn`/`ReturnReported`, chuyển thẳng sang UC-67 ngay sau khi TO xác nhận (4.b1).
- Mỗi bước ghi audit log bất biến (BR-130).
- Thiếu mã tham chiếu chuyển khoản khi báo → MSG172, không ghi nhận.
- Trạng thái không ghi được → MSG127, giữ nguyên trạng thái trước.

### 2.3 Hoàn tiền về Traveler (UC-67)
- Chỉ được kích hoạt khi: (a) `ReturnReceived` (đủ phần chia), hoặc (b) phần chia = 0 sau khi TO xác nhận, hoặc (c) hủy bởi TO theo UC-66 (ngoài phạm vi đợt này).
- Số tiền **không tính lại** — dùng đúng số đã xác nhận ở UC-42.
- Gửi qua cổng hoàn tiền **giả lập** (Q4 đã chốt): cùng pipeline như thật (claim, gửi, ghi kết quả, retry, escalate), chỉ kết quả cổng do giả lập tạo ra, giống cách chốt cho payout UC-71/74. Bản ghi giữ kênh thanh toán gốc và provider gốc (BR-76) để truy vết.
- Lỗi → chờ **1 giờ** rồi thử lại, tối đa **3 lần thử lại** = tổng **4 lần gửi** (BR-161, Q2 đã chốt). Hết lượt → `Escalated`, báo Administrator trong 24 giờ, màn hình monitor.
- `CompleteManualRefund` (đã có) giữ lại cho trường hợp Administrator xử lý `Escalated`/`Reconciliation`, không dùng cho luồng thường.

---

## 3. Thiết kế thay đổi BE

### 3.1 Domain
- `RefundStatus`: thêm `PendingOperatorConfirmation`, `AwaitingPartnerReturn`, `ReturnReported`, `ReturnReceived`, `Reconciliation`, `Escalated`. Giữ `Pending`, `Processing`, `RetryPending`, `Processed`, `Failed`, `Rejected`, `ManualReview` để không vỡ dữ liệu cũ. Ý nghĩa `Pending` giữ như code hiện tại: đã đủ điều kiện để UC-67 gửi gateway, đang chờ `RefundProcessor` nhặt (không phải "đã gửi"). Cột DB phải nới từ 20 lên ít nhất 30 ký tự (G13); không có CHECK constraint nên không cần sửa constraint.
- `Refund`: thêm `OperatorReturnShare`, `OperatorConfirmedAtUtc`, `ReturnDeadlineAtUtc`, `ReturnReference`, `ReturnReportedAtUtc`, `ReturnReceivedAtUtc`. Hành vi: `ConfirmByOperator`, `AutoConfirm`, `ReportReturn`, `RecordReturnReceived`. Mỗi hàm chỉ đi đúng một chiều trong vòng đời.
- `Refund.CreatePending` → `CreatePendingOperatorConfirmation` (không còn đặt `NextAttemptAtUtc`).
- `TravelerCancellationPolicy`: version 3 với 3 bậc theo BR-135 mới (14 ngày / 10 ngày / 0). Giữ đọc được version 1 và 2 cho booking cũ (BR-107).
- `Booking.DefaultCancellationPolicySnapshot`: version 3.

### 3.2 Application
- `BookingCancellationRefundRules.Evaluate`: bỏ nhánh 24 giờ (G4). Dùng bậc mới. Không ném lỗi khi thiếu giao dịch thành công — trả `refundAmount` và cờ `paymentMissing` để handler tạo `Reconciliation` hoặc bỏ qua RefundRequest theo 6.a1/6.b1 (G8).
- `CancelBookingCommandHandler`: bỏ `PaymentNotRefundable`. Tạo RefundRequest `PendingOperatorConfirmation` (không gọi gateway).
- `RefundProcessor`: chỉ nhặt refund `Pending`, tức đã qua UC-42 (TO xác nhận, và đã có `ReturnReceived` hoặc phần chia = 0). Không bao giờ nhặt `PendingOperatorConfirmation`, `AwaitingPartnerReturn`, `ReturnReported`.
- Lịch quét mới `OperatorConfirmationDeadlineService`: tự xác nhận các refund `PendingOperatorConfirmation` quá 3 ngày làm việc (BR-187), set `ReturnDeadlineAtUtc = now + 3 ngày làm việc`.
- Lịch quét `ReturnDeadlineService`: đặt nợ khi quá hạn (BR-144). Phần nợ kích hoạt bù trừ bởi UC-71 (đã có trên develop).
- `RefundProcessingOptions`: `RetryDelay = 1 giờ`; `MaxAttempts = 4` (1 lần gửi đầu + 3 lần thử lại, Q2 đã chốt).
- Cổng hoàn tiền giả lập `SimulatedRefundGateway` (Q4 đã chốt): thay `RefundPaymentGatewayResolver` cho mọi provider; `PayOsPaymentGateway.SubmitRefundAsync` không còn được gọi cho luồng hoàn tiền. Kết quả thành công sinh tham chiếu `SIM-REFUND-{refundId}`; kịch bản lỗi được tiêm qua interface (như `IPayoutTransferScenario`) để test retry và escalate.
- Các command/endpoint mới cho TO (UC-42): `ConfirmBookingRefundCommand`, `ReportBookingRefundReturnCommand` (bắt buộc mã tham chiếu, MSG172). Cho Staff: `RecordBookingRefundReturnReceivedCommand`.
- Mã lỗi mới: `refund.request_not_pending` (MSG171), `refund.operator_not_owner` (MSG126), `refund.return_reference_required` (MSG172), `refund.status_persist_failed` (MSG127).

### 3.3 API
- `POST /api/v1/operator/refunds/{refundId}/confirm` — TO xác nhận (UC-42 bước 3).
- `POST /api/v1/operator/refunds/{refundId}/return` — TO báo đã chuyển (body: `returnReference`).
- `POST /api/v1/admin/refunds/{refundId}/return-received` — Staff ghi nhận đủ phần chia (UC-42 bước 6).
- `POST /api/v1/admin/refunds/{refundId}/resolve` — Administrator xử lý `Escalated` / `Reconciliation`.
- Trả về `ProblemDetails` qua `HandleFailure`, không dùng envelope mới (TEAM_ENGINEERING_RULES §9).
- Mọi endpoint xác định người dùng từ session, không nhận `operatorId` từ body (§10).

### 3.4 Dữ liệu
- Script SQL mới trong `database/migrations/`, không dùng EF migration: nới `payment.Refund.status` lên ≥ 30 ký tự (G13), thêm cột trường mới (mục 3.1). Kèm kiểm tra: DB rỗng, DB có dữ liệu cũ (refund `Pending` đang tồn tại — xử lý theo Q3), và chạy lại không lỗi.

---

## 4. FE (ghi chú cho spec FE riêng)
- Màn hình TO "Confirm Booking Refund" (screen 90) thay luồng "Initiate Refund" hiện tại (`InitiateRefundDialog.tsx`, chế độ `NO_BACKEND`). Layout đang TBD trong SRS; các trường và nút đã được quy định (3.8.4.3).
- Màn hình Staff "Refund Return Reconciliation" (screen 100).
- Màn hình Administrator "Escalated Refunds Monitor" (screen 101).
- `RefundProgressPage.tsx` thêm nhãn cho các trạng thái mới.

---

## 5. Tiêu chí chấp nhận
1. Hủy booking đã thanh toán (Traveler, >14 ngày) **không** gọi gateway; RefundRequest ở `PendingOperatorConfirmation`.
2. Không còn đường từ `Pending` sang gateway mà không qua `ReturnReceived` hoặc phần chia = 0 (kiểm tra bằng test ở RefundProcessor).
3. Booking vừa thanh toán trong 24 giờ, Traveler hủy khi còn 9 ngày trước khởi hành: hoàn 0% (không còn được hoàn 100% nhờ rule 24h đã bãi bỏ — BR-157).
4. Booking tạo sau 11/11, Traveler hủy khi còn 12 ngày: hoàn 50%. Còn 9 ngày: 0% và yêu cầu xác nhận lại (MSG170). Booking tạo trước 11/11 vẫn theo snapshot cũ (BR-107).
5. Không có giao dịch thành công → hủy thành công, không RefundRequest, MSG136, không rollback.
6. Số tiền không khớp → `Reconciliation`, escalate Admin.
7. TO xác nhận → `AwaitingPartnerReturn`, hạn 3 ngày làm việc. Không xác nhận sau 3 ngày làm việc → tự xác nhận (BR-187).
8. Phần chia = 0 → UC-67 ngay sau xác nhận.
9. Phần chia theo BR-139 đúng tại 0 / 70% / 90% và làm tròn xuống.
10. Gateway lỗi → thử lại mỗi 1 giờ, tối đa 3 lần thử lại (tổng 4 lần gửi), sau đó `Escalated` và báo Administrator.
11. Mọi bước được ghi audit log bất biến (BR-130).
12. Test SQL Server thật cho migration và các truy vấn chuyển trạng thái (không dùng InMemory — TEAM_ENGINEERING_RULES §11–13).
13. Ghi nhận rõ trong PR các phần còn hoãn (G12 BR-158, UC-66, UC-68/69/75) ở mục Known limitations (TEAM_ENGINEERING_RULES §20, §30).

---

## 6. Điểm nối với UC-66 (ngoài phạm vi)
BR-186 (TO hủy tour theo UC-66 → hoàn 100% ngay qua kênh gốc, không chờ UC-42) là trường hợp thứ (c) của UC-67. Đợt này giữ `RefundProcessor` cho trường hợp (a)/(b); đường UC-66 sẽ làm ở đợt riêng cùng module weather-cancel. Không được để đường UC-66 mới đi vào hàng đợi `PendingOperatorConfirmation`.

---

## 7. Kiểm thử bắt buộc
- Unit: `BookingCancellationRefundRules` (ma trận 14/10/0 ngày, Traveler/TO, có/không thanh toán, khớp/lệch số tiền); `Refund` state machine (mọi chuyển trạng thái hợp lệ và không hợp lệ); tính phần chia BR-139 (0/70/90%, làm tròn).
- Integration SQL Server: hủy → `PendingOperatorConfirmation` mà không gọi gateway (dùng gateway giả đếm số lần gọi = 0); TO xác nhận → UC-67 chỉ sau `ReturnReceived`; tự xác nhận sau 3 ngày làm việc; thử lại 1 giờ × 3 → `Escalated`.
- Idempotency (BR-138): cùng `Idempotency-Key` + payload → cùng kết quả; khác payload → 409.
- Regression: booking cũ có snapshot version 1/2 vẫn tính đúng theo snapshot của nó (BR-107).

---

## 8. Phụ thuộc và quyết định

**Phụ thuộc (đã thỏa):**
- PR #155 `fix/linhnv-daily-payout-transfer` đã merge vào develop (`5716e653`). Có sẵn `TourOperatorDebt`, `DailyPayoutAmountCalculator` (70%/20%), `DailyPayoutAutoLock`, và settlement Advance/Final. G3 và G10 không còn bị chặn.
- Nhánh làm việc mới phải tạo từ develop mới nhất (`999e6af6` trở lên), không từ nhánh payout.

**Quyết định đã chốt (chủ sở hữu, ngày 13/11):**
- **Q1 — Bậc hoàn mới áp cho booking nào:** chỉ booking tạo sau ngày 11/11. Booking cũ giữ snapshot v1/v2 (BR-107).
- **Q2 — Số lần thử lại:** tổng 4 lần gửi (1 lần đầu + 3 lần thử lại), cách nhau 1 giờ, rồi `Escalated`.
- **Q3 — Refund `Pending` cũ:** đã gửi gateway thì giữ nguyên; chưa gửi thì đưa về `PendingOperatorConfirmation` để TO xác nhận.
- **Q4 — Kênh trả tiền về Traveler:** cổng hoàn tiền giả lập, vận hành như thật (xem mục 2.3, 3.2). Không gọi PayOS để hoàn tiền. Đây là phạm vi đợt này; tích hợp thật là đợt riêng.

**Câu hỏi còn lại (không chặn BE):**
- **Q5 (BR-189):** SRS quyết định 11/11 đã bỏ ngưỡng tiền, chỉ còn quy tắc 15 ngày. Đã đóng, chỉ cần test đúng quy tắc này.
- **Q6 (layout UC-42, screen 90/100/101):** SRS để TBD. Chỉ chặn spec FE, không chặn BE.
