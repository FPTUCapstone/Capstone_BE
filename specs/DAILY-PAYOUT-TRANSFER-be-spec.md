# DAILY-PAYOUT-TRANSFER — Chuyển tiền tự động cho Tour Operator mỗi ngày (Backend Spec, v7)

## Trạng thái

v7. Nguồn: SRS mới `SRS_rev10 (5).docx` (12/11/2026), mục 3.10.4–3.10.6 (UC-71, UC-74, UC-73) viết lại đầy đủ so với bản rev10-5 đã dùng cho v6; BR-141, BR-142, BR-144, BR-145, BR-146, BR-160, BR-168, BR-169, BR-172, BR-175, BR-176, BR-177, BR-188, BR-189; MSG145, MSG146, MSG175-178, MSG198, MSG199.

Điều kiện triển khai: các quy tắc BR-177, BR-188, BR-189 chỉ được merge sau khi biên bản phê duyệt của Owner được ghi vào SRS (Approver và Evidence) — SRS mới vẫn ghi "Approver: PENDING, Evidence: PENDING" cho các quyết định 10/11, 11/11 và 12/11/2026.

Nhánh: `fix/linhnv-daily-payout-transfer` (từ `origin/develop` `35868374`).

## Thay đổi so với v6 (đối chiếu SRS mới 12/11)

Phát hiện khi đối chiếu code đã viết (Task 0–11, v6) với SRS mới mục 3.10.4–3.10.6:

- **BR-160 — TO bị khóa (sửa kiến trúc).** SRS: đợt vẫn được tạo (Scheduled) đúng lượt kể cả khi TO đang bị khóa; chỉ việc CHUYỂN TIỀN (UC-74) bị hoãn tới khi mở khóa, đợt giữ `Scheduled`. Trước đây (v6) loại TO bị khóa khỏi *eligibility* hoàn toàn, không tạo đợt nào trong thời gian khóa — sai. Đã sửa: `DailyPayoutEligibilityReader` không còn lọc theo trạng thái khóa; việc kiểm tra khóa chuyển sang bước gửi tiền (`DailyPayoutRetrySweepProcessor.AttemptAsync`), đợt bị khóa giữ `Scheduled`, không tiêu tốn lần thử, được quét lại ở các lượt sau cho tới khi TO được mở khóa.
- **Trạng thái giữa các lần thử lại (sửa kiến trúc).** SRS 3.10.5 bước 3.a1: thất bại thì trạng thái **về lại `Scheduled`** (không phải `Processing`), tăng attempt count, gửi MSG175, thử lại sau 1 giờ. Đã gộp `DailyPayoutRunProcessor` (lần gửi đầu) và `DailyPayoutRetrySweepProcessor` (các lần thử lại) thành một luồng dùng chung (`AttemptAsync`), quét theo `Status == Scheduled` thay vì `Processing`. Lần thử cuối (thứ 4) thất bại thì chuyển `SettlementFailed` ngay trong cùng lượt quét đó (SRS 3.10.5, 3.b1), không cần chờ thêm một giờ.
- **MSG175 (mới).** Mỗi lần một lượt thử lại được lên lịch (thất bại, còn hạn mức), gửi thông báo cho Administrator — trước đây chỉ báo khi hết hẳn 3 lần.
- **Kiểm tra tài khoản ngân hàng TO (mới).** Trước khi gửi, kiểm tra `OperatorProfile.HasBeneficiaryBankAccount`; thiếu thì chuyển `SettlementFailed` ngay (không tốn lần thử), báo TO bằng MSG146 — khớp SRS 3.10.5 bước 1 và bước 1.b1 (trước đây gộp chung vào kết quả `Rejected` của gateway giả lập).
- **UC-73 `[Retry Transfer]` (sửa kiến trúc).** SRS 3.10.6: hành động gửi lại là đồng bộ — gửi một lần, kết quả (Settled hoặc về lại SettlementFailed, MSG176) phản ánh ngay trong phản hồi. Trước đây chỉ đổi trạng thái sang `Processing` rồi chờ sweep xử lý. Đã thêm `DailyPayoutRetrySweepProcessor.ManualRetryAsync` dùng riêng cho hành động này (không chia sẻ hạn mức 3 lần tự động).
- Các phần đã đúng, không đổi: BR-141/142/144/146/172/177/188/189 (Advance 70%, Final 20%×(PaidAmount−RefundAmount), bù nợ, khóa tự động 15 ngày, lý do khóa, MSG198/199), trạng thái `Resolved`, MSG177/178.

## Thay đổi so với v5

- **Dữ liệu dev cũ (Q3):** 24 đợt `Main` (tất cả `Settled`, ngày chạy 10/11–13/11/2026, chạy trên gateway giả lập) được sao lưu sang bảng archive rồi gỡ khỏi bảng chính, trong một migration có ghi rõ. Thực hiện sau khi developer duyệt bản sao lưu.
- **Bảng điều chỉnh (Q3):** ngừng dùng `payment.PayoutSettlementAdjustments` và `payment.PayoutAdjustments` trong nghiệp vụ. Giữ nguyên bảng. Xoá code carry over (`LoadOutstandingAdjustmentsAsync` và luồng liên quan).
- **Khóa tài khoản (Q4):** không tái sử dụng cơ chế khóa vì không tồn tại. Làm tối thiểu: `Lock(lý do)` và `Unlock` trên `User`, cột lý do và thời điểm khóa, endpoint Staff mở khóa chỉ dành cho TO và có kiểm tra nợ. Khóa thủ công (BR-168) để sau.
- **Sổ nợ TO (mới):** `payment.TourOperatorDebt` do payout tạo ra và đọc. Nhánh hoàn tiền sẽ ghi vào bảng này. Payout có interface để test khi chưa có dữ liệu thật.
- **Tên cột (Q5):** không thêm cột tỷ lệ. Kind xác định 70% hoặc 20%, `PayoutAmount` lưu số tiền cố định (BR-146).
- **Thời điểm đợt Final (Q6):** lượt 10:00 (giờ VN) đầu tiên sau `TourSchedule.EndAtUtc`.
- **Bỏ cửa sổ 13:00 (PD-7).** SRS chỉ nói thử lại mỗi giờ, tối đa 3 lần. Lịch thử là lỗi + 1 giờ, + 2 giờ, + 3 giờ.
- **Bỏ chạy bù trong ngày (PD-16).** SRS UC-71 mục 1.a1 nói chạy ở lượt kế tiếp. Các booking bị lỡ vẫn đủ điều kiện ở lượt sau.
- **Giữ PD-7a và PD-17** như yêu cầu kỹ thuật, ghi rõ không có trong SRS.
- **Làm tròn (PD-3):** TO nhận phần làm tròn xuống, TripMate giữ phần dư.
- **Thêm thu hồi toàn bộ refresh token (BR-175)** khi khóa.

## Phạm vi

Trong phạm vi:
- Lượt chạy tự động 10:00 (giờ VN) mỗi ngày, kể cả cuối tuần và ngày lễ.
- Đợt `Advance` (70%) và `Final` (20%) cho mỗi booking không bị hủy, gom thành một lệnh chuyển cho mỗi TO mỗi ngày.
- Bù trừ nợ treo theo TO, và khóa tự động TO khi nợ quá 15 ngày (BR-189).
- Trạng thái, gửi lệnh, thử lại, đối soát, và hai thao tác của Administrator (UC-73): `[Retry Transfer]` và `[Mark as Resolved]`.
- API đọc cho Staff/Administrator (UC-72) và lịch sử của TO (PD-13).
- Endpoint Staff mở khóa TO (phần con của UC-48, chỉ cho TO).

Ngoài phạm vi (nhánh hoàn tiền hoặc báo cáo):
- Quy trình hoàn tiền UC-41, UC-42, UC-67 và các công thức hoàn (BR-135, BR-139, BR-140, BR-157, BR-158, BR-186, BR-187). Bao gồm cách ghi nợ vào `TourOperatorDebt`.
- Báo cáo doanh thu và hoa hồng (UC-44, UC-45, BR-173).
- Khóa thủ công của Staff (BR-168) và giao diện.
- Màn hình FE (spec FE riêng).

## Quy tắc nghiệp vụ

**PD-1. Lượt chạy.** Mỗi ngày lúc 10:00 (giờ VN), kể cả cuối tuần và ngày lễ (BR-141). Lưu và so sánh bằng UTC.

**PD-1A. Awaiting Window (BR-141).** Booking đã thanh toán chờ đủ 24 giờ kể từ thời điểm xác nhận thanh toán rồi mới vào đợt `Advance`.

**PD-2. Điều kiện vào đợt.**
- `Advance`: booking đã thanh toán và xác nhận, Awaiting Window đã kết thúc, và không ở trạng thái Cancelled khi lượt chạy bắt đầu.
- `Final`: booking có `EndAtUtc` đã qua và chưa có `Final`. Chọn ở lượt 10:00 đầu tiên sau `EndAtUtc` (Q6), và booking không ở trạng thái Cancelled.
- Ví dụ: tour kết thúc 20/11 lúc 18:00 (giờ VN) thì `Final` được tạo ở lượt 10:00 ngày 21/11.

**PD-2A. Booking đã hủy trước lượt chạy.** Lượt chạy không tạo đợt nào và ghi audit log với lý do "cancelled before run" (BR-142). Cách xử lý tiền liên quan đến hoàn thuộc nhánh hoàn tiền.

**PD-3. Số tiền.**
- `Advance` = `floor(PaidAmount × 70%)`.
- `Final` = `floor((PaidAmount − RefundAmount) × 20%)`. Trong phạm vi này `RefundAmount` luôn bằng 0 cho booking không bị hủy.
- Hoa hồng `Commission` = `floor(PaidAmount × CommissionRate)`, với `CommissionRate` chụp khi xác nhận booking (BR-140).
- Làm tròn: TO nhận số đã làm tròn xuống đến đồng. Phần dư thuộc TripMate (nhất quán với BR-139).
- `DebtOffset` = phần nợ TO được trừ vào đợt này (PD-15).
- `Payout` = số tiền đợt − `DebtOffset`. Nếu `Payout ≤ 0`, không tạo lệnh chuyển, phần nợ còn lại giữ cho lượt sau (BR-188).

**PD-4. Số tiền cố định.** Sau khi tạo, số tiền không tính lại (BR-146).

**PD-5. Tối đa một đợt mỗi loại mỗi booking.** Tối đa một `Advance` và một `Final`. Không có `Main`, `Retained`, hay điều chỉnh.

**PD-6. Chuyển tiền.** Lệnh gom chuyển sang `Processing` khi gửi. Khi gateway xác nhận, chuyển sang `Settled`, ghi mã tham chiếu cho lệnh và từng đợt. Kênh vẫn là chuyển khoản ngân hàng (SRS 3.10.5).

**PD-7. Thử lại (BR-145).** Lần đầu tại thời điểm lượt chạy. Lỗi thì thử lại sau 1 giờ, 2 giờ, 3 giờ (tối đa 3 lần thử lại). Sau lần cuối thất bại thì chuyển `SettlementFailed` và thông báo Administrator trong vòng 24 giờ (BR-172).

**PD-7a. Tra cứu trước khi thử lại (yêu cầu kỹ thuật).** Trước mỗi lần thử lại, tra cứu theo mã tham chiếu đã gửi. Nếu đã chuyển thì `Settled`, không gửi lại. Không thay đổi số tiền hay kết quả nhìn từ người dùng. Không có trong SRS, ghi rõ trong tài liệu.

**PD-8. Thiếu tài khoản.** TO thiếu hoặc sai tài khoản ngân hàng: lệnh không gửi, đợt chuyển `SettlementFailed`, gửi MSG146 cho TO.

**PD-9. TO bị khóa thủ công (BR-160).** Đợt giữ `Scheduled` và được chuyển sau khi mở khóa.

**PD-9A. TO bị khóa tự động (BR-189).**
- Điều kiện: nợ treo chưa thu hồi đủ sau 15 ngày kể từ thời điểm phát sinh (đọc từ `TourOperatorDebt`).
- Hành động: gọi `Lock(lý do = "Unrecovered pending debt (BR-189)")` trên `User`; thu hồi toàn bộ refresh token của người dùng (BR-175); ghi audit log bất biến (BR-169); gửi MSG198 cho TO và Staff.
- Mở khóa: endpoint Staff chỉ cho TO. Từ chối với MSG199 khi nợ còn lại lớn hơn 0. Chỉ mở khi nợ đã thu hồi đủ.
- Khi bị khóa, đợt của TO giữ `Scheduled` (PD-9).

**PD-10. Vòng đời trạng thái.**
- `Scheduled` → `Processing` → `Settled`.
- `Processing` → `SettlementFailed` (hết số lần thử, hoặc thiếu tài khoản).
- `SettlementFailed` → `Processing` qua `[Retry Transfer]`: gửi một lần, cùng số tiền, đi qua PD-7a.
- `SettlementFailed` → `Resolved` qua `[Mark as Resolved]`: bắt buộc có lý do (MSG178). Không chuyển tiền.
- Mọi thay đổi ghi audit log.
- Không còn `Carried Over` và không còn chuyển thủ công có mã tham chiếu.

**PD-11. Lệnh đã gửi được coi là đã chuyển.** `Processing` hoặc `Settled` là đã chuyển. Lượt chạy idempotent theo (BookingId, Kind).

**PD-12. Không cần duyệt.** TO và Administrator không cần duyệt trước khi chuyển.

**PD-13. Quyền đọc.**
- Staff và Administrator: xem toàn bộ đợt (UC-72), chỉ đọc.
- TO: xem lịch sử đợt của chính mình (screen 94), theo booking.
- Chỉ Administrator xử lý đợt thất bại (UC-73).

**PD-14. Gom lệnh theo TO.** Mỗi TO một lệnh mỗi ngày, tổng `Payout` các đợt trong lượt.

**PD-15. Nợ treo của TO (BR-144, BR-188).** Đọc `TourOperatorDebt` (số còn lại, thời điểm phát sinh). Trừ theo thứ tự tạo đợt. Phần vượt nợ mới được chuyển. Nợ còn lại giữ cho lượt sau.

**PD-16. Lượt bị lỡ (theo SRS UC-71 mục 1.a1).** Không chạy bù trong ngày. Các booking bị lỡ được xử lý ở lượt kế tiếp, không tạo trùng (PD-11).

**PD-17. Đối soát sau khi gửi (yêu cầu kỹ thuật).** Tra cứu từng lệnh `Processing` sau mỗi lượt. Khi trạng thái gateway khác trạng thái đang ghi, ghi `PayoutReconciliationItem`, audit log, và cảnh báo Administrator. Không có trong SRS, ghi rõ trong tài liệu.

## Chế độ giả lập

Lệnh đi qua `IPayoutTransferGateway` giả lập: thành công, lỗi tạm thời, lỗi vĩnh viễn, từ chối sau khi gửi, timeout. Mã tham chiếu `SIM-{RunDate}-{TourOperatorId}-{BatchId}`.

## Mô hình dữ liệu

- `payment.PayoutSettlement.Kind`: `Advance` và `Final` (CHECK). Ràng buộc duy nhất `(BookingId, Kind)`.
- `payment.PayoutSettlementStatus`: thêm `Resolved`.
- `payment.TourOperatorDebt` (mới): `Id`, `TourOperatorUserId`, `Amount`, `RecoveredAmount`, `IncurredAtUtc`, `CreatedAtUtc`. Số còn lại = `Amount − RecoveredAmount`.
- `dbo.Users`: thêm `lock_reason NVARCHAR(200) NULL`, `locked_at_utc DATETIME2 NULL`.
- Archive: `payment.PayoutSettlement_Archive_v3` (sao chép 24 dòng `Main` và các bản ghi liên quan trước khi xoá).
- `payment.PayoutRun`, `payment.PayoutBatch`, `payment.PayoutSettlementAttempt`, `payment.PayoutReconciliationItem`: giữ nguyên.
- `payment.PayoutSettlementAdjustments`, `payment.PayoutAdjustments`: giữ bảng, ngừng dùng.
- Migration SQL idempotent, cột thời gian `DATETIME2`. Không dùng EF migrations.

## Thông báo

- MSG145: chuyển thành công (TO).
- MSG146: thiếu tài khoản (TO).
- MSG176: `[Retry Transfer]` thất bại lại (Administrator).
- MSG177: đợt không ở `SettlementFailed` (Administrator).
- MSG178: `[Mark as Resolved]` thiếu lý do (Administrator).
- MSG198: khóa tự động (TO, Staff).
- MSG199: không mở khóa được khi nợ còn lại (Staff).
- BR-172: thông báo Administrator trong 24 giờ khi hết số lần thử.
- Thử lại đã lên lịch và chênh lệch đối soát: chưa có mã trong SRS, cần Lead/PO gán.

## Quyết định đã chốt (v6)

1. Dữ liệu dev: sao lưu 24 đợt `Main` sang archive, gỡ khỏi bảng chính, qua migration có ghi rõ.
2. Bảng điều chỉnh: ngừng dùng, giữ bảng, xoá code carry over.
3. Khóa: tối thiểu (`Lock`/`Unlock` trên `User`, cột lý do và thời điểm, endpoint Staff mở khóa TO), khóa thủ công để sau.
4. Sổ nợ: `payment.TourOperatorDebt` do payout tạo và đọc, nhánh hoàn tiền ghi vào.
5. Không thêm cột tỷ lệ.
6. Đợt `Final`: lượt 10:00 đầu tiên sau `EndAtUtc`.
7. Bỏ cửa sổ 13:00 và chạy bù trong ngày.
8. Giữ PD-7a và PD-17 như yêu cầu kỹ thuật.
9. Làm tròn: TO nhận phần làm tròn xuống, TripMate giữ phần dư.
10. Thu hồi toàn bộ refresh token khi khóa (BR-175).

## Điều kiện còn lại

- Biên bản phê duyệt của Owner cho BR-177, BR-188, BR-189 và các quyết định 10/11, 11/11 được ghi vào SRS (Approver và Evidence) trước khi merge.
- Developer duyệt bản sao lưu dữ liệu dev trước khi chạy migration archive.
- Lead/PO gán mã MSG cho thử lại, chênh lệch đối soát.

## Kiểm thử

- Đơn vị: `Advance`, `Final`, làm tròn xuống, hoa hồng, trừ nợ và phần vượt nợ (PD-3, PD-15).
- Đơn vị: `Payout ≤ 0` không tạo lệnh, nợ giữ lại.
- Đơn vị: chuyển trạng thái hợp lệ và từ chối không hợp lệ, gồm `Resolved`.
- Đơn vị: `[Mark as Resolved]` thiếu lý do bị từ chối (MSG178).
- Đơn vị: `Lock`/`Unlock` trên `User`; mở khóa bị từ chối khi nợ còn lại.
- Tích hợp: lượt 10:00 tạo đúng một `Advance` cho booking đủ 24 giờ, một `Final` ở lượt đầu tiên sau `EndAtUtc`.
- Tích hợp: booking Cancelled trước lượt không tạo đợt và có audit log (PD-2A).
- Tích hợp: chạy lại cùng `RunDate` không trùng (PD-11).
- Tích hợp: nợ lớn hơn đợt thì không chuyển đồng nào (PD-15).
- Tích hợp: khóa tự động sau 15 ngày, có audit log, thu hồi token, MSG198; mở khóa bị từ chối khi còn nợ (PD-9A).
- Tích hợp: thử lại 1 giờ, 2 giờ, 3 giờ rồi `SettlementFailed` và thông báo trong 24 giờ (PD-7, BR-172).
- Tích hợp: tra cứu trước khi thử lại (PD-7a).
- Tích hợp: archive 24 dòng `Main` đủ và khớp trước khi xoá.
