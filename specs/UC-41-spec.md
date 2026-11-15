# UC-41 Cancel Tour Booking — Spec (draft, chờ duyệt)

Nhánh: `feature/linhnv-uc41-cancel-booking-72h` (BE, FE, Mobile từ `develop`)
Nguồn: SRS v12 mục 3.8.4.2 (UC-41), BR-105 → BR-107, BR-135, BR-136, BR-137, BR-138, BR-155, BR-157.

## 1. Phạm vi

Trong phạm vi (chỉ phần hủy):
- Hủy booking của Traveler (chính chủ) và Tour Operator (tour của mình).
- Kiểm tra quyền sở hữu, trạng thái booking, vé đã dùng, Idempotency-Key.
- Chuyển trạng thái booking sang `Cancelled`, trả slot, vô hiệu vé QR chưa dùng, ghi audit.
- Quy tắc hủy của Traveler: thay cửa sổ 24 giờ (MSG83) bằng quy tắc 72 giờ với mức hoàn 0% và xác nhận (MSG170).

Ngoài phạm vi:
- Gọi cổng thanh toán để hoàn tiền (UC-42 / UC-67).
- Định tuyến hoàn tiền theo settlement đã chuyển hay chưa (BR-142 / BR-143, UC-41 bước 7).
- Khiếu nại và hoàn một phần (UC-68, UC-69, UC-75).

## 2. Hiện trạng code (đã kiểm tra)

| Hạng mục | Vị trí | Trạng thái |
|---|---|---|
| Lệnh + handler hủy | `Features/Bookings/Cancel/CancelBookingCommandHandler.cs` | Có |
| Idempotency-Key + kiểm tra payload | handler dòng ~46, ~161–170 | Có |
| Kiểm tra quyền theo actor | handler dòng ~150–159 | Có |
| `BookingCancellationOperation` (audit) | Domain + handler dòng ~93–95 | Có |
| Snapshot chính sách hủy | `TravelerCancellationPolicy.cs` | Có, nhưng dùng bậc ≥7 ngày / 3–7 ngày / 1–3 ngày |
| Tạo `Refund` Pending | handler dòng ~109–113 | Có |
| Chặn hủy trong 24 giờ | handler dòng ~189 (`WindowClosed`) | Cần thay bằng quy tắc 72 giờ |
| Mức hoàn 0% + xác nhận (MSG170) | — | Chưa có |
| BR-157: hủy trong 24 giờ sau thanh toán được hoàn 100% | — | Chưa có |

Lệch so với SRS: bậc hoàn tiền trong snapshot là `≥7 ngày / 3–7 ngày / 1–3 ngày`, còn BR-135 là `>14 ngày 100% / 7–14 ngày 50% / 72 giờ–7 ngày 30% / <72 giờ 0%`.

## 3. Quy tắc nghiệp vụ cần triển khai

- BR-106: chỉ hủy booking `PendingPayment` hoặc `Confirmed`. Không hủy `Cancelled`, `Completed`, đã check-in, hoặc có vé đã dùng.
- BR-106: Tour Operator bắt buộc có lý do và mô tả. Traveler thì lý do là tùy chọn.
- BR-136: Tour Operator hủy thì hoàn 100% số tiền đã thanh toán đã xác minh. Hủy booking `PendingPayment` thì không tạo refund.
- BR-135 (Traveler): mức hoàn tính trên `PaidAmount`, đo từ thời điểm hệ thống xác nhận yêu cầu (BR-155).
- BR-157: hủy trong 24 giờ sau xác nhận thanh toán thì hoàn 100%, bất kể còn bao lâu đến ngày khởi hành.
- BR-137: toàn bộ thao tác (trạng thái, trả slot, vô hiệu vé, tạo refund nếu có, audit) nằm trong một transaction.
- BR-138: Idempotency-Key bắt buộc. Cùng key + cùng payload trả kết quả cũ. Cùng key + payload khác bị từ chối (409).
- BR-86: hủy booking vô hiệu vé QR chưa dùng ngay lập tức.

## 4. Mã lỗi và thông báo

| Tình huống | HTTP | Thông báo |
|---|---|---|
| Không sở hữu booking/tour | 403 | MSG126 |
| Thiếu lý do/mô tả (Tour Operator) | 400 | MSG132 |
| Booking đã hủy/hoàn tất/đã check-in/vé đã dùng | 409 | MSG133 |
| Traveler hủy trong 72 giờ | 200 sau xác nhận | MSG170 (hiện mức 0%) |
| Hủy thành công | 200 | MSG82 |
| Đã có refund Pending | 200 | MSG131 |
| Booking không tồn tại | 404 | MSG134 |
| Idempotency-Key trùng payload khác | 409 | MSG135 |
| Không có thanh toán đã xác minh / số tiền không khớp | 200 (không tạo refund) | MSG136 |
| Lỗi hệ thống / mạng | 500 | MSG127 |

Lưu ý: nhiều MSG trong bảng này chưa được gán mã trong FE (xem Task 8 của daily payout, đang bị chặn chờ Lead/PO).

## 5. Quyết định đã chốt

1. **Refund Pending:** UC-41 tạo một Refund Pending khi có thanh toán đã xác minh khớp số tiền. UC-41 không gọi cổng thanh toán. UC-42/67 xử lý sau.
2. **Bậc hoàn tiền Traveler:** theo BR-135 (>14 ngày 100%, 7–14 ngày 50%, 72 giờ–7 ngày 30%, <72 giờ 0%). Snapshot hiện tại (≥7 ngày / 3–7 ngày / 1–3 ngày) phải được thay đổi cách tính. Cần xác định booking đã có snapshot cũ thì xử lý thế nào (xem mục 6).
3. **Xác nhận 0%:** request hủy phải có cờ `confirmed`. Nếu mức hoàn là 0% mà chưa có `confirmed = true` thì từ chối và hiển thị MSG170.

## 6. Câu hỏi còn mở (chưa chặn spec)

- Booking đã tạo với snapshot cũ: dùng bậc cũ (theo snapshot) hay áp BR-135? Đề xuất: giữ snapshot, vì BR-107 yêu cầu không đổi quyền hủy/mức hoàn của booking đã có.
- MSG131–137 và MSG170: có trong SRS, chưa có trong code. Cần gán mã trước khi làm FE.

## 6. Kiểm thử dự kiến (TDD, khi được duyệt)

- Unit: bảng quyết định hoàn tiền theo khoảng thời gian (≥14 ngày, 7–14, 72 giờ–7, <72 giờ), BR-157, PendingPayment không tạo refund.
- Unit: Tour Operator hủy hoàn 100%, bắt buộc lý do/mô tả.
- SQL Server integration: một transaction (trạng thái, slot, vé, refund, audit), Idempotency-Key lặp lại và payload khác, quyền sở hữu.
- Mục tiêu: không gọi cổng thanh toán trong UC-41.


## Known limitation (đã chấp nhận)

- Refund Pending của booking đã có settlement chuyển cho Tour Operator vẫn được `RefundProcessingBackgroundService` xử lý ngay, vì `RefundProcessor` chưa kiểm tra trạng thái settlement (BR-143).
- Sẽ xử lý trong UC-42 (xác nhận hoàn tiền của Tour Operator) và UC-67.
- Khi test UC-41, chỉ dùng booking chưa có settlement.
