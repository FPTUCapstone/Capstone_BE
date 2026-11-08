# DAILY-PAYOUT-TRANSFER — Plan triển khai (BE), v3

Nguồn sự thật: `specs/DAILY-PAYOUT-TRANSFER-be-spec.md` (v3). SRS Report3 v12 mục 3.9.9, 3.10.4, 3.10.5.
Nhánh: `feature/linhnv-daily-payout-transfer` (từ `origin/develop`).

Thay đổi so với v1: thêm Task 0 kiểm kê bảng sẵn có; thêm clock abstraction (Task 2A); thêm dòng điều chỉnh và đối soát (Task 2B, Task 7A); thêm quyền đọc cho TO (Task 10); thêm lượt bị lỡ (Task 6A); thêm audit log (Task 2C).

Thay đổi so với v2 (SRS v12): thêm Task 6B — Awaiting Window 24 giờ (BR-141, đã triển khai); thêm
Task 10A — hiển thị trạng thái "Awaiting Window" ở UC-72 (chưa triển khai, theo dõi ở mục "Điểm
lệch đã biết" của spec v3 cùng với 2 điểm lệch khác không liên quan tới SRS v12 (cutoff thử lại,
`PayoutReconciliationItem`).

## Quy tắc làm việc

- Theo `AGENTS.md` và `CLAUDE.md`: TDD cho mọi thay đổi có logic (test đỏ trước, xác nhận đỏ đúng lý do, code tối thiểu, xanh, refactor).
- Một task một lần. Sau mỗi task: chạy kiểm tra, rà diff với spec. Finding Critical chặn task tiếp theo.
- Database-first: không dùng `dotnet ef migrations add`. Schema qua SQL idempotent trong `database/migrations/` (mẫu `IF OBJECT_ID(...) IS NULL`).
- Không magic string: status, action, mã lỗi là `public const string` trên type sở hữu.
- `Result<T>` và `HandleFailure` cho lỗi nghiệp vụ, theo `AGENTS.md`.
- Không commit, push hay mở PR cho đến khi developer chọn (Task 12).

Lệnh kiểm tra:
`dotnet build` và `dotnet test tests/TripMate.Application.UnitTests --nologo --filter "FullyQualifiedName~DailyPayout"`

## Task 0 — Kiểm kê code và DB payout hiện có (chỉ đọc)

- Liệt kê trên nhánh: handler, controller, entity, migration, test liên quan đến payout (UC-44, UC-46, `RequestPayoutCommandHandler`, `PayoutConfigKeys`, `OperatorPayoutEligibilityReader`).
- Liệt kê bảng payout đã có trong DB và trong migration (ví dụ `payout_settlement_request`, `payout_available_balance`, và bất kỳ bảng settlement nào đã tạo). Đối chiếu với bảng mới trong spec để tránh trùng.
- Tìm cơ chế scheduler/job nền đang có trong repo (để dùng cho Task 6).
- Tìm cơ chế audit log hiện có (PD-10).
- Xác nhận cách lưu `CommissionRate` trên booking hiện có.
- **DoD:** file kiểm kê trong `plans/` hoặc `specs/`. Developer xác nhận trước khi xóa bất kỳ thứ gì.

## Task 1 — Gỡ luồng yêu cầu rút tiền (UC-46) và duyệt của Staff

- **Red:** test rằng route và handler yêu cầu rút tiền không còn (404 hoặc không đăng ký).
- **Green:** xóa handler, command, validator, controller action và test liên quan, theo kết quả Task 0 đã được xác nhận.
- Nếu migration `payout_settlement_request` và `payout_available_balance` đã chạy trên DB dev, tạo migration gỡ cột/bảng thay vì sửa file cũ.
- **DoD:** `dotnet build` xanh, không còn tham chiếu đến yêu cầu rút tiền. Các test UC-44 còn lại vẫn xanh.

## Task 2 — Migration schema settlement

- File SQL idempotent: `payment.PayoutRun`, `payment.PayoutBatch`, `payment.PayoutSettlement`, `payment.PayoutSettlementAttempt`.
- `PayoutRun`: `RunDate` là khóa duy nhất (PD-11).
- `PayoutBatch`: ràng buộc duy nhất `(RunDate, TourOperatorId)`.
- `PayoutSettlement`: ràng buộc duy nhất `(BookingId, Kind)`; `Kind` là `Main` hoặc `Retained` (PD-2, PD-5); index theo `TourOperatorId` và `RunDate`.
- Thêm cột `CommissionRate` trên booking nếu chưa có. Backfill cho booking cũ bằng 10%, là giá trị cố định toàn hệ thống theo BR-140. Ghi rõ trong migration rằng đây là giá trị mặc định cho booking cũ, không đọc từ hồ sơ TO.
- Chạy hai lần qua `database/apply-schema.sh` để kiểm tra idempotent.
- **DoD:** script chạy hai lần không lỗi, schema khớp spec.

## Task 2A — Clock abstraction

- **Red:** test lượt chạy và thử lại phụ thuộc thời gian dùng `IClock` giả, không dùng `DateTime.UtcNow` trực tiếp.
- **Green:** interface `IClock` (UTC và giờ Việt Nam), triển khai thật và giả lập cho test.
- **DoD:** test xanh; không còn `DateTime.UtcNow` trong code payout mới.

## Task 2B — Dòng điều chỉnh (`PayoutAdjustment`)

- **Red:** test tạo điều chỉnh từ settlement thất bại vào settlement kế tiếp của cùng TO; settlement gốc giữ số tiền (PC-02); điều chỉnh không được là settlement mới.
- **Green:** entity `PayoutAdjustment`, migration idempotent cho bảng này.
- **DoD:** test xanh; migration chạy hai lần không lỗi.

## Task 2C — Audit log cho settlement

- **Red:** test mỗi thay đổi trạng thái settlement và mỗi lần gửi lệnh ghi một bản ghi audit (PD-10).
- **Green:** dùng cơ chế audit log hiện có (xác nhận ở Task 0). Nếu chưa có, thêm bảng và helper tối thiểu.
- **DoD:** test xanh.

## Task 3 — Entity, trạng thái và state machine

- **Red:** test chuyển trạng thái hợp lệ và từ chối các chuyển không hợp lệ (PD-10).
- **Green:** `PayoutSettlementStatus` và `PayoutBatchStatus` là các `public const string`. Entity có hành vi chuyển trạng thái, từ chối bằng `Result`.
- Navigation property được gán cho entity mới (theo `AGENTS.md`).
- **DoD:** test state machine xanh.

## Task 4 — Tính số tiền settlement (PD-3, PD-4)

- **Red:** test `Gross`, `Commission = floor(Gross × CommissionRate)`, `Net`, trừ nợ theo TO (PD-15), `Payout ≤ 0` (loại khỏi lệnh gom, giữ nợ), số tiền không đổi sau khi tạo (BR-146), settlement `Retained` (PD-2).
- **Green:** `PayoutAmountCalculator` thuần, không phụ thuộc DB.
- **DoD:** test calculator xanh, gồm ca biên: nợ lớn hơn `Net`, làm tròn, hủy một phần.

## Task 5 — Gateway chuyển tiền giả lập (adapter)

- **Red:** test `SimulatedPayoutTransferGateway`: thành công, lỗi tạm thời, lỗi vĩnh viễn (tài khoản sai), bị từ chối sau khi gửi, timeout không rõ kết quả, tra cứu theo mã tham chiếu; mã tham chiếu ổn định `SIM-{RunDate}-{TourOperatorId}-{BatchId}`.
- **Green:** interface `IPayoutTransferGateway` (gửi lệnh, tra cứu kết quả). Triển khai giả lập cấu hình được kịch bản.
- Đăng ký trong DI, mặc định là giả lập. Không có triển khai thật ở nhánh này.
- **DoD:** test adapter xanh.

## Task 6 — Lượt chạy 10:00 và gom lệnh theo TO (UC-71, PD-14)

- **Red:** test lượt chạy tạo đúng một settlement `Main` cho mỗi booking đã thanh toán và chưa hủy toàn bộ; tạo settlement `Retained` cho hủy một phần trước lượt (PD-2); bỏ qua booking hủy 100% trước lượt (BR-142); gom một lệnh cho mỗi TO; chạy lại cùng `RunDate` không tạo trùng (PD-11); lượt chạy cả cuối tuần và ngày lễ (BR-141).
- **Green:** job nền kích hoạt lúc 10:00 mỗi ngày theo scheduler đã xác nhận ở Task 0. Ghi `PayoutRun` trước khi tạo settlement, trong cùng transaction DB (để lượt chạy không tạo nửa vời khi lỗi giữa chừng).
- **DoD:** test lượt chạy xanh.

## Task 6B — Awaiting Window 24 giờ (BR-141, PD-1A) — done

- **Red:** test booking thanh toán 2 giờ trước lượt chạy bị loại khỏi lượt đó; booking thanh toán
  23 giờ trước, chạy lại ngày hôm sau (hơn 24 giờ), được đưa vào lượt kế tiếp.
- **Green:** `DailyPayoutEligibilityReader.LoadEligibleBookingsAsync` nhận `nowUtc`, loại các
  booking (cả `Main` và `Retained`) có `OrderKey` (thời điểm thanh toán) chưa quá
  `DailyPayoutClock.AwaitingWindowDuration` (24 giờ) so với `nowUtc`.
- **DoD:** test xanh (`DailyPayoutRunProcessorTests`); test tích hợp SQL Server tương ứng
  (`DailyRun_BookingStillInsideItsAwaitingWindow_IsExcludedUntilTheWindowEnds`) biên dịch sạch,
  chạy thật khi có `TRIPMATE_SQLSERVER_TEST_CONNECTION`.

## Task 6A — Lượt bị lỡ (PD-16)

- **Red:** test khởi động lại trong cùng ngày sau 10:00 thì chạy bù đúng một lần; khởi động lại ngày hôm sau không chạy bù ngày trước.
- **Green:** kiểm tra `PayoutRun` của ngày hiện tại khi khởi động.
- **DoD:** test xanh.

## Task 7 — Gửi lệnh, thử lại và tra cứu trước khi thử lại (UC-74, PD-6, PD-7, PD-7a, PD-8)

- **Red:** test lệnh gom chuyển sang `Processing` khi gửi; `Settled` khi thành công kèm mã tham chiếu; thử lại mỗi giờ, tối đa 3 lần thử lại (tổng tối đa 4 lần gửi); trước mỗi lần thử lại phải gọi tra cứu theo mã tham chiếu, nếu gateway báo đã chuyển thì `Settled` và không gửi lại (PD-7a); timeout không được coi là thất bại chắc chắn; hết số lần thử thì `SettlementFailed` và thông báo Administrator; thiếu tài khoản TO thì `SettlementFailed` ngay (PD-8, MSG146); mỗi lần gửi ghi `PayoutSettlementAttempt` (PC-03).
- **Green:** xử lý trong lượt chạy. Thử lại được lên lịch bằng cơ chế hẹn giờ của job (dùng `IClock`), không ngủ trong thread.
- **DoD:** test xanh; số lần gọi gateway đúng; không gọi gửi lại khi tra cứu cho thấy đã chuyển.

## Task 7A — Đối soát sau lượt (PD-17)

- **Red:** test lệnh `Processing` được tra cứu sau lượt; gateway xác nhận đã chuyển thì giữ `Settled`; gateway báo từ chối sau khi gửi thì chuyển về `SettlementFailed`; chênh lệch số tiền, mã tham chiếu hoặc trạng thái được ghi `PayoutReconciliationItem`, ghi audit log và gửi cảnh báo.
- **Green:** job đối soát chạy sau mỗi lượt, dùng adapter.
- **DoD:** test xanh.

## Task 8 — TO bị khóa tạm dừng chuyển (BR-160, PD-9)

- **Red:** test settlement của TO bị khóa giữ `Scheduled`, không đưa vào lệnh; mở khóa thì được chuyển ở lượt đầu tiên; hoàn tiền đã thu (BR-142) không bị ảnh hưởng.
- **Green:** kiểm tra trạng thái khóa TO khi chọn settlement cho lượt.
- **DoD:** test xanh.

## Task 9 — Xử lý thất bại, carry over và chuyển thủ công (UC-73, PD-10)

- **Red:** test Administrator thử lại: qua tra cứu PD-7a, số tiền không đổi; carry over tạo `PayoutAdjustment` vào settlement kế tiếp của cùng TO; ghi nhận chuyển thủ công bắt buộc có mã tham chiếu; mọi thay đổi có audit log.
- **Green:** endpoint cho Administrator, handler, validator.
- **DoD:** test xanh.

## Task 10 — API đọc (UC-72, PD-13)

- **Red:** test Staff và Administrator xem được toàn bộ settlement có lọc theo trạng thái và khoảng ngày, chỉ đọc; TO chỉ xem được settlement của chính mình, theo booking; TO không xem được của TO khác (trả 403 hoặc 404).
- **Green:** endpoint đọc có phân trang, và endpoint cho TO với kiểm tra quyền sở hữu.
- **DoD:** test xanh.

## Task 10A — Hiển thị trạng thái "Awaiting Window" ở UC-72 (chưa làm)

SRS v12 §3.9.9.1 liệt kê "Awaiting Window" là một trong các trạng thái UC-72 phải hiển thị, cùng
`Scheduled, Processing, Settled, SettlementFailed, Carried Over`. Hiện `GetPayoutSettlementsQuery`
chỉ đọc bảng `PayoutSettlements`, không có dòng nào cho booking còn trong cửa sổ (Task 6B) vì
settlement chỉ được tạo sau khi cửa sổ kết thúc.

- **Red:** test UC-72 trả về một dòng trạng thái `Awaiting Window` cho booking đã thanh toán,
  chưa hết 24 giờ, chưa có settlement; dòng này biến mất (nhường cho dòng settlement thật) ngay
  khi lượt chạy tạo settlement cho nó.
- **Green:** mở rộng handler đọc để gộp (a) các dòng `PayoutSettlements` hiện có và (b) các
  booking đã thanh toán, chưa hủy, chưa hết cửa sổ Awaiting Window và chưa có settlement — hiển
  thị dòng loại (b) với trạng thái `Awaiting Window`, không có `SettlementId`/`TransferReference`.
  Áp dụng filter trạng thái và khoảng ngày của UC-72 trên tập gộp này.
- **DoD:** test xanh; không phá vỡ các test UC-72 hiện có (`DailyPayoutReadQueryTests`).

## Task 11 — Kiểm thử tích hợp

- Lượt 10:00 end-to-end trên SQL Server: tạo settlement và lệnh gom, gửi lệnh giả lập, `Settled`.
- Chạy lại cùng ngày không tạo trùng.
- Booking còn trong Awaiting Window bị loại khỏi lượt; hết cửa sổ thì vào lượt kế tiếp (Task 6B) — done.
- Timeout được tra cứu trước khi thử lại, không chuyển trùng.
- Lỗi giả lập được thử lại đúng 3 lần, rồi `SettlementFailed`.
- TO bị khóa rồi mở khóa.
- Đối soát phát hiện lệnh bị từ chối sau khi gửi.
- Lượt bị lỡ được chạy bù trong ngày.
- **DoD:** test tích hợp xanh.

## Task 12 — Báo cáo và kết thúc

- Chạy `dotnet build` và `dotnet test` toàn bộ solution.
- Rà diff với spec, liệt kê các điểm lệch (nếu có).
- Hỏi developer: merge, mở PR, giữ nhánh, hay hủy.

## Rủi ro

- **Gỡ luồng UC-46 chạm nhiều code và test có sẵn.** Chạy test toàn bộ sau mỗi task gỡ.
- **Chuyển trùng khi timeout.** Được chặn bằng PD-7a và Task 7. Đây là rủi ro tiền thật nếu triển khai thật, cần kiểm thử kỹ.
- **Xung đột với nhánh `feature/linhnv-post-trip-refund`.** Cả hai đụng `OperatorPayoutEligibilityReader` và cùng nhóm migration. Thống nhất thứ tự merge trước khi merge.
- **Trùng bảng sẵn có.** Kiểm kê ở Task 0 phải xong trước Task 2.
- **Hoa hồng đổi.** Chụp `CommissionRate` trên booking (Task 2) để booking cũ không bị tính lại.
- **Phần FE chưa có plan.** Màn hình UC-72, UC-73 và lịch sử TO cần nhánh hoặc spec FE riêng.
