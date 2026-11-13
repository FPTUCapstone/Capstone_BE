# UC-41 Cancel Tour Booking — Implementation Plan (BE)

Spec: `specs/UC-41-spec.md`
Branch: `feature/linhnv-uc41-cancel-booking-72h` (base `develop`)

## Vấn đề thiết kế cần quyết định trước

`BookingCancellationPolicy` lưu trong snapshot JSON theo BR-107 (không đổi cho booking đã tạo). Tên trường hiện tại (`atLeastSevenDaysPercent`, `threeToSevenDaysPercent`, `oneToThreeDaysPercent`) và mặc định `100/70/30` đang diễn giải theo mốc 7/3/1 ngày. BR-135 dùng mốc 14/7/3 ngày (72 giờ) với 100/50/30/0. Hai cách làm:

- **A (đề xuất):** thêm `version` vào snapshot. Booking mới dùng v2 (BR-135, mốc 14 ngày/7 ngày/72 giờ). Booking cũ không có version giữ v1 để đọc đúng bậc đã lưu. Không cần migration dữ liệu.
- **B:** đổi nghĩa các trường cũ. Vi phạm BR-107 vì booking đã tạo sẽ đổi mức hoàn.

## Các task (TDD: đỏ → xanh → refactor)

1. **T1 — Policy v2 (BR-135).** `TravelerCancellationPolicy`: thêm version, mốc `>14 ngày=100`, `7–14 ngày=50`, `72 giờ–7 ngày=30`, `<72 giờ=0` (cho phép 0 nhưng không chặn). Test biên: 14d, 7d, 72h, 71.99h, 0h. Giữ test v1 hiện có để bảo vệ booking cũ.
2. **T2 — Snapshot mặc định.** `Booking.DefaultCancellationPolicySnapshot` chuyển sang v2. Test booking mới lưu v2, booking cũ đọc v1.
3. **T3 — Cửa sổ 0% và xác nhận (MSG170).** Thay `WindowClosed` (24 giờ, dòng ~188) bằng: nếu mức hoàn 0% và request không có `confirmed = true` thì trả lỗi xác nhận (MSG170). Thêm `Confirmed` vào `CancelBookingRequest` và `CancelBookingCommand`. Test: 0% không confirm → từ chối; 0% có confirm → hủy, không tạo Refund; Tour Operator không bị ảnh hưởng.
4. **T4 — BR-157.** Hủy trong 24 giờ sau xác nhận thanh toán (`Payment.ConfirmedAt`) → hoàn 100% bất kể thời gian còn lại. Test: 23h sau thanh toán, 25h sau thanh toán.
5. **T5 — Preview.** `GetBookingCancellationPreviewQueryHandler` trả mức hoàn theo cùng logic T1/T3/T4 để FE hiển thị trước khi xác nhận.
6. **T6 — Không gọi cổng.** Test xác nhận handler không gọi `IPaymentGateway`/refund gateway. Refund chỉ tạo Pending.
7. **T7 — Integration SQL Server.** Hủy một transaction (booking, slot, vé QR, Refund, audit), Idempotency-Key lặp lại và khác payload, quyền sở hữu. Chạy với Docker SQL local.
8. **T8 — Mã lỗi/MSG.** MSG131–137, MSG170 map vào `BookingCancellationErrorCodes`. Gán mã thực sẽ do Lead/PO quyết (xem Task 8 của daily payout), tạm dùng mã nội bộ.

## Ngoài phạm vi (không làm)

- Gọi cổng để hoàn tiền (UC-42, UC-67).
- Định tuyến theo settlement đã chuyển (BR-142/143) — đây là UC-41 bước 7, để sang UC-42/67.
- FE và Mobile sẽ làm sau khi BE xong.

## Cần người duyệt

1. Chọn cách A (snapshot có version) hay B.
2. Xác nhận T8: dùng mã nội bộ tạm cho MSG, hay chờ Lead/PO gán.
