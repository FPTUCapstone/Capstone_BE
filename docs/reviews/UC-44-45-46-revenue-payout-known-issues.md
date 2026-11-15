# UC-44 / UC-45 / UC-46 — Vấn đề cần team duyệt

Tổng hợp ngày 2026-10-22, trong lúc viết spec UC-46 (Request Payout Settlement). Mỗi vấn đề đều có dẫn chứng code cụ thể, không phải suy đoán.

## Tóm tắt nhanh

| # | Vấn đề | Mức độ | Đã xử lý chưa |
|---|---|---|---|
| 1 | Booking chưa bao giờ tự chuyển sang `Completed` | Chặn UC-46 chạy với dữ liệu thật | Đã chốt — làm sau |
| 2 | Hủy booking hiện chặn hoàn toàn nếu không còn `PendingPayment`/`Confirmed` | Chặn tính năng "giữ 3 ngày sau khi hoàn thành" | Đã chốt — làm sau |
| 3 | UC-74 (Staff từ chối payout) chưa tồn tại | Rủi ro tiền bị "giam" nếu quên hoàn trả adjustment | Cần ghi nhớ, chưa cần quyết định |
| 4 | Số ở Báo cáo doanh thu (UC-44) và số rút được (UC-46) không khớp | Dễ gây hiểu lầm cho operator | Đã chốt — chỉ sửa phía UC-46 |
| 5 | Báo cáo UC-44 của tháng đã qua có thể đổi số nếu sau đó có hủy đơn | Không chuẩn kế toán, nhưng chấp nhận được với MVP | Đã chốt — giữ nguyên |
| 6 | UC-44/45 đọc dữ liệu theo trạng thái hiện tại của booking, chưa tận dụng mốc thời gian sẵn có trên `PaymentTransactions`/`Refunds` | Là gốc rễ của vấn đề 4, 5; sửa được nhưng cần thời gian, không phải việc nhỏ | Cần team xác nhận hướng xử lý |

Không có vấn đề nào chặn việc tiếp tục code UC-46.

## Chi tiết từng vấn đề

**1. Booking không bao giờ chuyển sang `Completed`**
Tìm toàn bộ code, không có chỗ nào gán `Status = Completed` cho booking, cũng chưa có UC-43 (check-in) hay job tự động nào làm việc này.
→ UC-46 sẽ luôn trả về danh sách rỗng với dữ liệu thật, cho tới khi tính năng này được làm.
→ Đã xác nhận: sẽ làm trong tương lai, tour kết thúc thì tự chuyển `Completed`. Không cần quyết định gì thêm.

**2. Hủy/refund bị chặn ngay khi booking không còn `PendingPayment`/`Confirmed`**
File `CancelBookingCommandHandler.cs:66-67`.
→ Thiết kế "giữ 3 ngày sau khi hoàn thành mới cho thanh toán" cần hủy/refund được phép trong 3 ngày đó, nhưng hiện tại chưa cho phép.
→ Đã xác nhận: sẽ mở rộng guard này sau. Giá trị `refund_hold_days = 3` đã chốt sẵn, không cần đổi.

**3. UC-74 (Staff từ chối payout) chưa code**
Chỉ có UC-63 (danh sách) đã làm; UC-64/73/74 chưa có cả spec BE lẫn code.
→ Khi làm UC-74, **phải nhớ** gọi hoàn trả (`PayoutAdjustment.Revert()`) các khoản điều chỉnh đã áp vào payout bị từ chối, giống cách UC-46 đã làm khi hủy. Nếu quên, tiền sẽ bị kẹt vĩnh viễn.
→ Không cần quyết định ngay, chỉ cần không quên khi tới lúc làm UC-74.

**4. Số UC-44 và UC-46 không khớp nhau**
UC-44 tính theo tháng **thanh toán**, gồm cả `Confirmed` lẫn `Completed`. UC-46 tính theo tháng **hoàn thành + 3 ngày giữ**, chỉ tính `Completed`.
→ Operator thấy báo cáo ghi có tiền nhưng chưa rút được ngay, dễ thắc mắc.
→ Đã chốt: không sửa UC-44 (đã final). Chỉ thêm phần giải thích/số dư khả dụng bên màn UC-46.

**5. Báo cáo UC-44 của tháng đã qua có thể đổi số**
Code lọc theo trạng thái **hiện tại** của booking, không theo trạng thái tại thời điểm báo cáo. Nếu booking bị hủy sau đó, báo cáo tháng cũ sẽ hiện số thấp hơn khi xem lại.
→ Không đúng chuẩn kế toán thực tế (hệ thống thật dùng sổ cái cộng dồn, số liệu kỳ cũ không đổi).
→ Đã chốt: không sửa UC-44. Muốn giữ bản cố định thì dùng UC-45 (export) — tính năng này đã có sẵn, không cần làm thêm.

**6. UC-44/45 chưa tận dụng đúng dữ liệu đã có để tính theo mốc sự kiện**
Ban đầu tưởng đây là "thiếu hẳn sổ cái kế toán" — nhưng kiểm tra lại thì **không đúng**. `payment.PaymentTransactions` đã có `SucceededAtUtc` (gắn cố định lúc thanh toán thành công, không bị ghi đè) và `payment.Refunds` đã có `ProcessedAtUtc` — mỗi giao dịch, mỗi refund đều đã là một dòng có mốc thời gian riêng, không đổi. Về bản chất, TripMate đã có một "sổ cái" tối thiểu.

Vấn đề thật sự: `OperatorRevenueAggregator` **không dùng** các mốc thời gian đó để tổng hợp báo cáo — nó lọc theo `Booking.Status` **hiện tại** (dòng 32), nên khi trạng thái booking đổi (bị hủy), số liệu của một kỳ cũ đổi theo. Đây là vấn đề ở **cách truy vấn/tổng hợp**, không phải thiếu dữ liệu nền.
→ Theo mô hình các nền tảng trung gian thật (Booking.com, Grab, Airbnb): màn hình báo cáo vận hành vốn dĩ **không cần** là nguồn sự thật cuối cùng về tiền — cổng thanh toán (hoặc phần mềm kế toán riêng) mới giữ vai trò đó. TripMate hiện tại, `PaymentTransactions`/`Refunds` đã đóng đúng vai trò này.
→ Nếu muốn sửa: chỉ cần đổi cách `OperatorRevenueAggregator` tổng hợp — tính refund theo `ProcessedAtUtc` nằm trong kỳ, không phụ thuộc trạng thái booking hiện tại — **không cần** dựng bảng ledger mới hay migrate dữ liệu. Vẫn phải viết lại phần tính commission liên kỳ và sửa lại test hiện có, nhưng nhỏ hơn nhiều so với đánh giá ban đầu.
→ Với đồ án capstone: SRS hiện tại (BR-109, BR-112) không bắt buộc cách tính theo sự kiện, code hiện tại **đúng với spec đã viết**. Có thể để nguyên, chỉ cần biết rằng đây là điểm có thể cải tiến với chi phí vừa phải, không phải việc lớn như đánh giá lúc đầu.

## Cần team xác nhận

- Đồng ý vấn đề 1, 2 là việc làm sau, không phải lỗi UC-46.
- Ghi nhớ vấn đề 3 khi làm UC-74.
- Đồng ý vấn đề 4, 5 giữ nguyên, không sửa UC-44.
- Vấn đề 6: chọn một trong hai hướng — (a) giữ nguyên cho MVP, ghi vào báo cáo đồ án là quyết định phạm vi có chủ đích; hoặc (b) sửa `OperatorRevenueAggregator` để tính refund theo `ProcessedAtUtc` — chi phí thấp hơn đánh giá ban đầu vì dữ liệu mốc thời gian đã có sẵn, không cần ledger mới.
