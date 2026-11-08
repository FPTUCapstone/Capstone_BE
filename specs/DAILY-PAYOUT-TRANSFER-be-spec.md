# DAILY-PAYOUT-TRANSFER — Chuyển tiền tự động cho Tour Operator mỗi ngày (Backend Spec, v2)

## Trạng thái

Draft v2, chờ developer duyệt. Thay thế v1 sau khi rà soát (xem mục "Thay đổi so với v1").
Nguồn: SRS Report3 v11 (mục 3.9.9, 3.10.4, 3.10.5; UC-71 đến UC-74; BR-139 đến BR-146, BR-154, BR-160).

Thay thế luồng payout cũ (yêu cầu rút tiền của TO, Staff duyệt, tính theo tháng, carry-forward). Luồng cũ đã bị bỏ trong SRS và không có trong spec này.

Nhánh: `feature/linhnv-daily-payout-transfer` (từ `origin/develop`).

## Phạm vi

Trong phạm vi:
- Lượt chạy tự động mỗi ngày lúc 10:00 (giờ Việt Nam), kể cả cuối tuần và ngày lễ.
- Tạo một settlement cho mỗi booking, gom thành một lệnh chuyển cho mỗi TO mỗi ngày (PD-14).
- Trạng thái settlement, gửi lệnh chuyển, thử lại, đối soát, đánh dấu thất bại để Administrator xử lý.
- Tạm dừng chuyển khi TO bị khóa do vi phạm hợp đồng (BR-160).
- API đọc: Staff và Administrator (UC-72); TO xem lịch sử settlement của chính mình.
- Administrator xử lý settlement thất bại (UC-73).

Ngoài phạm vi (nhánh khác):
- Hoàn tiền khiếu nại và UC-42, UC-67 (nhánh `feature/linhnv-post-trip-refund`). Spec này chỉ cung cấp trạng thái và số tiền settlement để luồng hoàn tiền đọc.
- Hoa hồng và báo cáo doanh thu (UC-44, UC-45), đã có ở code hiện tại.
- Màn hình FE (sẽ có spec riêng).

## Quy tắc nghiệp vụ

**PD-1. Lượt chạy.** Hệ thống chạy một lần mỗi ngày lúc 10:00 theo giờ Việt Nam, kể cả cuối tuần và ngày lễ (BR-141). Thời gian lưu và so sánh bằng UTC, quy đổi sang giờ Việt Nam khi xác định lượt.

**PD-2. Booking được đưa vào lượt chạy.** Booking được chọn nếu khi lượt chạy bắt đầu: đã thanh toán và xác nhận, và không bị hủy toàn bộ. Xử lý theo BR-142 và BR-143:
- Hủy 100% trước lượt chạy: không tạo settlement cho TO; toàn bộ tiền hoàn từ tiền đã thu (BR-142).
- Hủy một phần trước lượt chạy: tạo settlement loại `Retained` cho phần giữ lại. Phần giữ lại = tổng tiền của phần không hoàn. TripMate giữ hoa hồng 10% của phần giữ lại, TO nhận phần còn lại (BR-142).
- Hủy sau khi lượt chạy bắt đầu: booking đã được chuyển, đi theo luồng sau chuyển (BR-143, nhánh hoàn tiền).

**PD-3. Số tiền settlement.** Với mỗi settlement (loại `Main` hoặc `Retained`):
- `Gross` = tổng tiền khách đã thanh toán cho phần đó.
- `Commission` = `floor(Gross × CommissionRate)`. `CommissionRate` là tỷ lệ đã chụp trên booking khi xác nhận (BR-140), không đọc lại từ hồ sơ TO.
- `Net` = `Gross − Commission`.
- `DebtOffset` = phần nợ treo của TO được trừ vào settlement này (xem PD-15).
- `Payout` = `Net − DebtOffset`.
- Nếu `Payout ≤ 0` thì settlement không được đưa vào lệnh gom của lượt; phần nợ chưa trừ hết được giữ cho lượt sau.

**PD-4. Số tiền cố định.** Khi settlement được tạo, số tiền không được tính lại (BR-146). Điều chỉnh chỉ qua dòng điều chỉnh (`PayoutAdjustment`) hoặc bù trừ nợ.

**PD-5. Một settlement chính mỗi booking.** Mỗi booking có tối đa một settlement loại `Main`. Settlement `Retained` (PD-2) là settlement riêng, không tính là settlement thứ hai của phần chính. Phần chuyển tiếp (carry over) của settlement thất bại là một dòng điều chỉnh gắn với settlement kế tiếp của cùng TO, không phải settlement mới (PC-02 của SRS).

**PD-6. Chuyển tiền.** Lệnh gom chuyển sang `Processing` khi gửi. Khi gateway xác nhận thành công, chuyển sang `Settled` và ghi mã tham chiếu chuyển khoản cho lệnh và từng settlement trong lệnh.

**PD-7. Thử lại.** Lượt thử đầu tiên tại thời điểm lượt chạy. Nếu lỗi, thử lại mỗi giờ, tối đa 3 lần thử lại trong cùng ngày (tổng tối đa 4 lần gửi). Số lần và khoảng cách lấy từ cấu hình, không hardcode. Hết số lần thử thì chuyển sang `SettlementFailed` và thông báo Administrator (UC-73).

**PD-7a. Tra cứu trước khi thử lại (bắt buộc).** Trước mỗi lần thử lại, hệ thống tra cứu kết quả theo mã tham chiếu đã gửi. Nếu gateway báo đã chuyển thì chuyển sang `Settled`, không gửi lại. Chỉ gửi lại khi gateway xác nhận chưa chuyển hoặc chắc chắn thất bại. Mục đích: tránh chuyển trùng khi timeout (không biết đã chuyển hay chưa).

**PD-8. Thiếu tài khoản.** Nếu TO thiếu hoặc sai thông tin tài khoản ngân hàng, lệnh không được gửi và các settlement của lệnh chuyển sang `SettlementFailed` (MSG146). Administrator có thể thử lại sau khi TO sửa tài khoản.

**PD-9. TO bị khóa (BR-160).** Khi tài khoản TO bị khóa do vi phạm hợp đồng, settlement của TO không được đưa vào lệnh. Chúng giữ `Scheduled` và được chuyển ở lượt đầu tiên sau khi mở khóa. Hoàn tiền đã thu (BR-142) không bị ảnh hưởng.

**PD-10. Vòng đời trạng thái (settlement).**
- `Scheduled` → `Processing` → `Settled`.
- `Processing` → `SettlementFailed` (sau hết số lần thử, hoặc thiếu tài khoản).
- `SettlementFailed` → `Processing` (Administrator thử lại; vẫn phải qua tra cứu PD-7a; số tiền không đổi).
- `SettlementFailed` → `Carried Over` (tạo `PayoutAdjustment` gắn với settlement kế tiếp của cùng TO).
- `SettlementFailed` → `Settled` (Administrator ghi nhận chuyển thủ công ngoài hệ thống, bắt buộc có mã tham chiếu).
- Mỗi lần đổi trạng thái được ghi vào audit log (dùng cơ chế audit log hiện có của repo, xác nhận ở Task 0).

**PD-11. Lệnh đã gửi được coi là đã chuyển.** Lệnh ở `Processing` hoặc `Settled` được coi là đã chuyển. Mỗi booking chỉ được chuyển một lần. Lượt chạy phải idempotent: chạy lại cùng `RunDate` không tạo settlement trùng. Thử lại chỉ áp dụng cho settlement chưa được xác nhận đã chuyển (PD-7a).

**PD-12. Không cần duyệt.** TO không cần duyệt, Administrator không cần xác nhận trước khi chuyển.

**PD-13. Quyền đọc.**
- Staff và Administrator: xem toàn bộ settlement (UC-72), chỉ đọc.
- Tour Operator: xem lịch sử settlement của chính mình, theo booking (màn hình "Payout Settlement History" trong SRS). Không xem của TO khác.
- Chỉ Administrator được xử lý settlement thất bại (UC-73).

**PD-14. Gom lệnh chuyển theo TO.** Mỗi TO nhận một lệnh chuyển mỗi ngày, bằng tổng `Payout` của các settlement trong lượt. Mỗi settlement là một dòng trong lệnh, nên vẫn truy vết được từng booking. Lệnh gom là đơn vị chuyển, settlement là đơn vị đối soát.

**PD-15. Nợ treo của TO (BR-144).** Nợ được trừ theo TO, không theo booking. Trong một lượt, nợ được trừ lần lượt vào settlement của TO cho đến khi hết, theo thứ tự thời gian tạo. Nếu nợ còn lại sau lượt, giữ cho lượt kế tiếp. Nếu không còn lượt nào (TO ngừng bán), xử lý theo hợp đồng đối tác.

**PD-16. Lượt chạy bị lỡ.** Nếu ứng dụng ngừng lúc 10:00, khi khởi động lại trong cùng ngày hệ thống chạy bù lượt của ngày đó (một lần, nhờ PD-11 không trùng). Không chạy bù lượt của ngày trước đó.

**PD-17. Đối soát sau khi gửi lệnh.** Sau mỗi lượt, hệ thống tra cứu từng lệnh ở trạng thái `Processing` qua adapter để xác nhận kết quả cuối. Lệnh được xác nhận đã chuyển thì giữ `Settled`. Lệnh bị gateway từ chối sau khi gửi thì chuyển về `SettlementFailed`. Đối soát so sánh mã tham chiếu, số tiền và trạng thái gateway với dữ liệu trong DB. Chênh lệch được ghi audit log và gửi cảnh báo cho Administrator.

## Chế độ giả lập (quyết định đã chốt)

Bước chuyển tiền thật chưa làm. Lượt chạy vẫn đầy đủ mọi bước như khi có kết nối thật, nhưng lệnh đi qua adapter giả lập.

- Interface `IPayoutTransferGateway`: gửi lệnh, tra cứu kết quả theo mã tham chiếu.
- `SimulatedPayoutTransferGateway` là mặc định, cấu hình kịch bản: thành công, lỗi tạm thời (thử lại), lỗi vĩnh viễn (tài khoản sai), bị từ chối sau khi gửi, và timeout không rõ kết quả (để kiểm thử PD-7a).
- Mã tham chiếu ổn định, ví dụ `SIM-{RunDate}-{TourOperatorId}-{BatchId}` cho lệnh gom.
- Mọi lần gọi adapter ghi `PayoutSettlementAttempt` và audit log.
- Triển khai thật sau này chỉ thay adapter, không đổi logic lượt chạy.

## Thay đổi so với v1

- PD-2: sửa đúng theo BR-142. Hủy một phần vẫn có settlement loại `Retained` cho phần giữ lại.
- PD-5: phân biệt settlement `Main` và `Retained`.
- PD-7a: thêm tra cứu trước khi thử lại, để tránh chuyển trùng.
- PD-7: làm rõ số lần thử (1 lần đầu cộng tối đa 3 lần thử lại).
- PD-3: làm tròn hoa hồng và dùng `CommissionRate` đã chụp.
- PD-13: TO được xem lịch sử settlement của chính mình.
- PD-14, PD-15: gom lệnh theo TO; nợ treo trừ theo TO.
- PD-16: lượt bị lỡ.
- PD-17: đối soát có định nghĩa cụ thể.

## Mô hình dữ liệu (đề xuất, xác nhận ở Task 0)

- `payment.PayoutRun`: `RunDate` (khóa duy nhất), trạng thái lượt, thời điểm bắt đầu và kết thúc.
- `payment.PayoutBatch`: một lệnh gom cho một TO trong một lượt. `RunDate`, `TourOperatorId`, `TotalAmount`, `Status`, `TransferReference`, `AttemptCount`, `LastAttemptAt`. Ràng buộc duy nhất `(RunDate, TourOperatorId)`.
- `payment.PayoutSettlement`: một dòng cho mỗi booking hoặc phần giữ lại. `Id`, `BatchId`, `BookingId`, `TourOperatorId`, `RunDate`, `Kind` (`Main` hoặc `Retained`), `GrossAmount`, `CommissionRate`, `CommissionAmount`, `NetAmount`, `DebtOffsetAmount`, `PayoutAmount`, `Status`, `CreatedAt`, `UpdatedAt`. Ràng buộc duy nhất `(BookingId, Kind)`.
- `payment.PayoutSettlementAttempt`: lịch sử lần gửi lệnh, mã tham chiếu gateway, kết quả.
- `payment.PayoutAdjustment`: dòng điều chỉnh (carry over, bù trừ nợ). `SourceSettlementId`, `TargetSettlementId`, `Amount`, `Reason`.
- `payment.PayoutReconciliationItem`: kết quả đối soát từng lệnh (PD-17).
- Migration SQL idempotent trong `database/migrations/`, không dùng `dotnet ef migrations add`.
- Trước khi tạo bảng, kiểm kê bảng payout đã có trong DB (Task 0). Bảng trùng chức năng phải được gỡ hoặc đổi tên.

## Thông báo

- Thiếu tài khoản TO → MSG146 (đưa vào `SettlementFailed`).
- Chuyển thành công → MSG145 gửi TO.
- Thử lại đã lên lịch → [MSG-new-F] (chưa có mã, cần xác nhận).
- Hết số lần thử → [MSG-new-G] gửi Administrator (chưa có mã, cần xác nhận).
- Đối soát có chênh lệch → [MSG-new-H] gửi Administrator (chưa có mã, cần xác nhận).

## Phụ thuộc với nhánh khác

- Nhánh `feature/linhnv-post-trip-refund` dùng trạng thái và số tiền settlement. Thứ tự merge: nhánh này trước, nhánh hoàn tiền sau, hoặc thống nhất tên trường trước khi merge.
- BR-157 (hoàn 100% trong 24 giờ): booking thanh toán trước 10:00 được chuyển trong lượt đó. Nếu khách hủy trong 24 giờ sau khi đã chuyển, khoản hoàn đi theo luồng sau chuyển (UC-42), do nhánh hoàn tiền xử lý.

## Quyết định đã chốt

1. Số lần thử: 1 lần đầu cộng tối đa 3 lần thử lại, mỗi giờ.
2. Đối soát: có, giả lập qua adapter.
3. Kênh chuyển và cuối tuần: xử lý bằng chế độ giả lập.
4. Hoa hồng: chụp trên booking khi xác nhận.
5. Gom lệnh theo TO mỗi ngày (PD-14).

6. Cửa sổ thử lại trong ngày kết thúc lúc 13:00 (10:00 cộng 3 lần thử lại, mỗi giờ). Lần thử sau 13:00 không thực hiện, settlement chuyển `SettlementFailed` nếu chưa thành công.

## Kiểm thử

- Đơn vị: `Net`, `Payout`, làm tròn `Commission`, trừ nợ theo TO (PD-15), `Payout ≤ 0`.
- Đơn vị: chuyển trạng thái hợp lệ và từ chối không hợp lệ.
- Đơn vị: hủy một phần tạo settlement `Retained` đúng số tiền (PD-2).
- Tích hợp: lượt 10:00 tạo đúng một settlement mỗi booking và một lệnh gom mỗi TO; chạy lại không trùng (PD-11).
- Tích hợp: timeout được tra cứu trước khi thử lại, không chuyển trùng (PD-7a).
- Tích hợp: TO bị khóa thì giữ `Scheduled`, mở khóa thì chuyển ở lượt kế tiếp (PD-9).
- Tích hợp: lượt bị lỡ được chạy bù trong ngày, không chạy bù ngày trước (PD-16).
- Tích hợp: đối soát phát hiện lệnh bị từ chối sau khi gửi (PD-17).
