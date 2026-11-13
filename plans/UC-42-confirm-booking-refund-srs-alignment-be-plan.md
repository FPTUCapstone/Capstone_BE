# UC-41 / UC-42 / UC-67 — Plan căn chỉnh hoàn tiền theo SRS (BE) — Plan v1

Spec tương ứng: `specs/UC-42-confirm-booking-refund-srs-alignment-be-spec.md` (v1.1, đã chốt Q1–Q4).

Quy trình: chờ duyệt spec → tạo nhánh mới từ develop (sau khi có phần payout đã merge, xem spec mục 8) → thực hiện từng task theo TDD (Red → Green → Refactor) → review giữa các task → `dotnet build` + `dotnet format --verify-no-changes` + `dotnet test` (kể cả SQL Server) trước mỗi commit.

Quy ước: mỗi task một commit, Conventional Commits (`fix(refund): ...`, `feat(refund): ...`), không commit `.claude/`.

---

## Task 0 — Chuẩn bị (không đổi hành vi) — XONG
- Nhánh `fix/linhnv-confirm-booking-refund` tạo từ develop `999e6af6` (worktree `Capstone_BE-confirm-booking-refund`).
- `TourOperatorDebt`, settlement Advance/Final, `DailyPayoutAutoLock` đã có trên develop qua PR #155 (spec §8).
- Baseline tại `999e6af6`: `dotnet build` 0 lỗi. Unit test filter Refund/Cancel: 146/146 pass. SQL Server integration test filter Refund/Cancel: 43/43 pass.

## Task 1 — Bậc hoàn Traveler theo BR-135 mới (G5)
Red:
- `TravelerCancellationPolicyTests`: version 3 cho các mốc 15 ngày (100%), 12 ngày (50%), 9 ngày (0%), biên 14 ngày và 10 ngày.
- Snapshot version 1/2 cũ vẫn trả đúng kết quả (BR-107).
Green:
- Thêm version 3 trong `TravelerCancellationPolicy.cs` (ngưỡng 14 ngày, 10 ngày); giữ nhánh v1/v2.
- Đổi `Booking.DefaultCancellationPolicySnapshot` sang version 3 (`Booking.cs:19`).
Q1 đã chốt: chỉ đổi default cho booking tạo sau 11/11; booking cũ giữ v1/v2.

## Task 2 — Bỏ quy tắc 24 giờ hoàn 100% (G4)
Red: `BookingCancellationRefundRulesTests` — thanh toán 2 giờ trước, hủy 9 ngày trước khởi hành → 0% (không còn 100%).
Green: xóa nhánh `successfulPayment.SucceededAtUtc ... FromHours(24)` trong `BookingCancellationRefundRules.cs:22`.

## Task 3 — Trạng thái hoàn tiền và state machine trong Domain (G2, G7)
Red: `RefundStateMachineTests` — chuyển trạng thái hợp lệ/không hợp lệ:
- `PendingOperatorConfirmation → AwaitingPartnerReturn` (TO xác nhận, đặt hạn 3 ngày làm việc)
- `PendingOperatorConfirmation → AwaitingPartnerReturn` tự động (BR-187)
- `AwaitingPartnerReturn → ReturnReported` (cần mã tham chiếu, không rỗng)
- `ReturnReported → ReturnReceived` (Staff)
- `ReturnReceived → Pending` (sẵn sàng UC-67)
- Phần chia = 0: `PendingOperatorConfirmation → Pending` trực tiếp khi TO xác nhận
- `Processing → Escalated` sau hết lượt thử
- `Reconciliation` từ tạo mới khi số tiền lệch
- Không hợp lệ: xác nhận hai lần, báo chuyển khi chưa xác nhận, nhận đủ khi chưa báo.
Green:
- Thêm giá trị enum `RefundStatus.cs`; thêm trường trong `Refund.cs` (phần chia, hạn, mã tham chiếu, các mốc thời gian); thêm các hàm chuyển trạng thái.
- Giữ `Pending`, `ManualReview`, `Processed`, `Failed`, `Rejected`, `RetryPending`, `Processing` để không vỡ dữ liệu. `Pending` nghĩa là đủ điều kiện để UC-67 gửi gateway (như code hiện tại).

## Task 4 — Migration SQL (database-first)
- Script `database/migrations/<date>_refund_uc42_status_and_return_fields.sql`: nới `payment.Refund.status` từ 20 lên ≥ 30 ký tự (G13); thêm các cột trường mới của Refund. Không có CHECK constraint trên status nên không cần bỏ constraint nào.
- Kiểm thử: `…SqlServerTests` (kiểu mẫu `DailyPayoutV6SchemaMigrationSqlServerTests`) chạy trên DB rỗng, DB có refund cũ, và chạy lại lần 2 không lỗi.
- Xử lý dữ liệu Pending cũ theo Q3.

## Task 5 — Hủy booking: không gọi gateway, không rollback khi thiếu thanh toán (G1, G8)
Red (unit + SQL Server):
- Hủy booking đã thanh toán → RefundRequest `PendingOperatorConfirmation`, số lần gọi gateway = 0.
- Không có giao dịch thành công → hủy thành công, không RefundRequest, MSG136, booking Cancelled (không rollback).
- Số tiền giao dịch ≠ số tiền booking → RefundRequest `Reconciliation`, escalate Admin, MSG136.
- Idempotency (BR-138): cùng key + payload → cùng kết quả; khác payload → 409.
Green:
- `CancelBookingCommandHandler.cs:96`: bỏ `PaymentNotRefundable`; tạo trạng thái đúng theo nhánh.
- `CancelBookingCommandHandler.cs:118`: `Refund.CreatePending` → `CreatePendingOperatorConfirmation`.

## Task 6 — RefundProcessor chỉ gửi sau UC-42 (G1)
Red: `RefundProcessorTests`:
- Refund `PendingOperatorConfirmation` không được nhặt (gateway = 0 lần gọi).
- Refund `Pending` (đã xác nhận, đủ phần chia) được gửi đúng một lần, kèm idempotency key `tripmate-refund-{id}`.
- Refund `AwaitingPartnerReturn`/`ReturnReported` không được gửi.
Green:
- `RefundProcessor.cs:28`: điều kiện lọc chỉ `Pending`/`RetryPending` đã xác nhận (đánh dấu bằng trạng thái, không bằng cờ phụ).

## Task 6b — Cổng hoàn tiền giả lập (Q4)
Red: `SimulatedRefundGatewayTests`:
- Gửi thành công → kết quả có tham chiếu `SIM-REFUND-{refundId}`, số lần gọi đúng 1.
- Kịch bản lỗi tiêm qua interface → kết quả `RetryableFailure`/`Rejected` đúng như kịch bản.
- Hoàn tiền cho booking PayOS vẫn đi qua `SimulatedRefundGateway`, không gọi `PayOsPaymentGateway.SubmitRefundAsync`.
Green:
- Thêm `SimulatedRefundGateway` (Infrastructure), tương tự `SimulatedPayoutTransferGateway`.
- Đổi `RefundPaymentGatewayResolver` trả về cổng giả lập cho mọi `PaymentGateway`; giữ enum provider gốc trên bản ghi refund để truy vết (BR-76).
- Ghi chú trong PR: đây là chế độ giả lập có chủ đích, tích hợp hoàn tiền thật là đợt riêng.

## Task 7 — Tính phần chia của TO (BR-139) (G3)
Red: `OperatorReturnShareTests`: 0 trước Advance; 70% sau Advance; 90% sau Final; làm tròn xuống VND; phần chia 0 kích hoạt UC-67 ngay; refund một phần (partial cancel BR-158 nếu có) dùng số tiền của người bị hủy.
Green: thêm `RefundShareCalculator` thuần (không đọc DB, như `DailyPayoutAmountCalculator`). Nguồn "đã chuyển cho TO" lấy từ `PayoutSettlement` trạng thái Settled (đã có trên develop).

## Task 8 — Endpoint ghi UC-42 cho TO và Staff (G2)
Red: integration test (SQL Server, không dùng InMemory):
- TO khác chủ tour → 403 (MSG126); refund không ở `PendingOperatorConfirmation` → 409 (MSG171).
- Báo chuyển thiếu mã → 400 (MSG172).
- Staff ghi nhận đủ phần chia → `ReturnReceived` → UC-67 được xếp hàng đúng một lần.
- Quá 3 ngày làm việc không xác nhận → job tự xác nhận (dùng clock giả), MSG197 gửi TO.
Green:
- `ConfirmBookingRefundCommand`, `ReportBookingRefundReturnCommand`, `RecordBookingRefundReturnReceivedCommand` (+ handler, validator).
- Controller: `OperatorRefundsController` (route `api/v1/operator/refunds`), `AdminRefundsController` (thêm route, đã có `complete-manual`).
- `ApiControllerBase`: ánh xạ mã lỗi mới sang 403/404/409/400 (không để rơi vào 400 mặc định).
- Lịch quét `OperatorConfirmationDeadlineService` (BR-187) và `ReturnDeadlineService` (BR-144 — đặt nợ; bù trừ đã có trên develop).

## Task 8b — Endpoint đọc cho FE (Q-FE2, spec §3.3)
Red: integration test (SQL Server):
- `GET /api/v1/operator/refunds?status=PendingOperatorConfirmation` chỉ trả refund thuộc tour của TO đăng nhập, phân trang đúng.
- `GET /api/v1/operator/refunds/{refundId}` của TO khác → 403; không tồn tại → 404.
- `GET /api/v1/admin/refunds?status=Escalated` và `?status=ReturnReported` lọc đúng theo trạng thái, nhiều trạng thái cùng lúc.
- `GET /api/v1/admin/refunds/{refundId}` không tồn tại → 404.
Green:
- `GetOperatorRefundsQuery`/`GetOperatorRefundDetailQuery`, `GetAdminRefundsQuery`/`GetAdminRefundDetailQuery` (+ handler), theo đúng pattern phân trang của `GetPayoutSettlementsQuery`.
- Thêm route `GET` tương ứng vào `OperatorRefundsController`/`AdminRefundsController`.

## Task 9 — Thử lại 1 giờ × 3 và Escalated (G6, G7)
Red: `RefundRetryPolicyTests`:
- Lỗi gateway → `RetryPending`, chờ đúng 1 giờ.
- Tổng 4 lần gửi (Q2): sau lần gửi thứ 4 vẫn lỗi → `Escalated`, phát thông báo Administrator.
- Lỗi không retry được (`IsRetryable: false`) → `ManualReview`, không tính vào lượt thử.
Green:
- `RefundProcessingOptions.cs:9,12`: `RetryDelay = 1 giờ`; `MaxAttempts = 4` (1 lần gửi đầu + 3 lần thử lại).
- `Refund.cs`: `Escalated` thay vì `Failed` khi hết lượt (nhánh đã có `MarkFailed`).

## Task 10 — Refund dữ liệu cũ và rà soát (G1–G10) — XONG
- DB dev (`TripMateDb`) tại thời điểm rà soát chỉ có 11 dòng `payment.Refunds`, cả 11 đều `Processed` (terminal). Không có dòng nào ở `Pending`/`Processing`/`RetryPending`/`ManualReview` — không có dữ liệu cũ nào cần chuyển theo Q3 (đã chốt: refund đã gửi giữ trạng thái, refund chưa gửi chuyển `PendingOperatorConfirmation`). Không cần script migrate dữ liệu.
- Rà soát `grep -rn "SubmitRefundAsync\|TryStartAttempt" src/`: đúng một nơi gọi mỗi hàm trong code sản xuất, cả hai đều trong `RefundProcessor.cs` (gọi gateway tại dòng ~58 chỉ sau khi `ClaimAsync`→`TryStartAttempt` đã xác nhận refund ở `Pending`/`RetryPending`). `Refund.CreatePending` (đường cũ, không qua UC-42) không còn được gọi ở `src/`, chỉ còn dùng làm fixture trong test. Không có đường nào đi vòng qua xác nhận TO.
- PR description soạn theo `.github/pull_request_template.md`, đính kèm khi mở PR (Task 11).

## Task 11 — Verify cuối và PR
- `dotnet restore`, `dotnet format TripMate.slnx --verify-no-changes --no-restore`, `dotnet build TripMate.slnx -c Release --no-restore`, `dotnet test TripMate.slnx -c Release --no-build` (kể cả Category=SqlServer với `TRIPMATE_SQLSERVER_TEST_CONNECTION`).
- Ghi lại head SHA, lệnh, exit code, pass/fail/skip vào PR description.
- Tạo PR vào `develop` (không vào main).

---

## Ngoài phạm vi (không làm trong plan này)
- UC-66 hủy tour do thời tiết → hoàn 100% ngay (BR-186).
- Hủy một phần người tham gia (BR-158, G12): cần thay đổi `CancelBookingCommand` và mô hình booking.
- UC-68/69/75 khiếu nại và đề xuất hoàn một phần (BR-153): BE chưa có module complaint.
- FE: spec và plan riêng sau khi spec BE được duyệt (màn hình screen 90/100/101 chờ chốt layout Q6).

## Điều kiện dừng
- Task 0: baseline đỏ không liên quan → dừng, báo người dùng.
- Task 4: migration chạy lỗi trên DB có dữ liệu cũ → dừng, không chạy tiếp.
- Phát sinh câu hỏi mới ảnh hưởng task đang làm (Q1–Q4 đã chốt, Q5 đã đóng, chỉ Q6 là FE) → dừng và hỏi.
