# DAILY-PAYOUT-TRANSFER — Plan triển khai (BE), v7

Nguồn sự thật: `specs/DAILY-PAYOUT-TRANSFER-be-spec.md` (v7). SRS mới `SRS_rev10 (5).docx` mục 3.10.4–3.10.6.
Nhánh: `fix/linhnv-daily-payout-transfer` (từ `origin/develop` `35868374`).

## Tiến độ (chưa commit)

- **Xong (v6), có test xanh:** Task 0–10 (xem lịch sử v6 bên dưới); Task 11 (524 test SQL Server, toàn solution, không hồi quy); Task 12 một phần (1064 test tích hợp ngoài SQL Server xanh).
- **Task 13 (mới, đối chiếu SRS mới 12/11 và sửa) — đã code, test xanh:**
  - Bỏ lọc TO bị khóa khỏi *eligibility* (`DailyPayoutEligibilityReader`); đợt vẫn tạo đúng lượt kể cả khi khóa (BR-160).
  - Gộp lần gửi đầu (`DailyPayoutRunProcessor`) và các lần thử lại (`DailyPayoutRetrySweepProcessor`) thành một luồng `AttemptAsync` dùng chung, quét theo `Status == Scheduled` (không còn `Processing` là trạng thái nghỉ giữa các lần thử — SRS 3.10.5, 3.a1).
  - Thêm kiểm tra tài khoản ngân hàng TO (`OperatorProfile.HasBeneficiaryBankAccount`) trước khi gửi — thiếu thì `SettlementFailed` ngay, MSG146 (2 method mới trên `PayoutBatch`: `MarkFailedWithoutAttempt`, `ReturnToScheduledAfterFailedAttempt`).
  - Thêm thông báo MSG175 mỗi lần một lượt thử lại được lên lịch.
  - `UC-73 [Retry Transfer]` chuyển sang đồng bộ (`DailyPayoutRetrySweepProcessor.ManualRetryAsync`), không còn dùng `PayoutBatch.RecordRetryAttempt` (đã gỡ).
  - Test đã cập nhật: `PayoutBatchTests`, `DailyPayoutRetrySweepProcessorTests`, `DailyPayoutRunProcessorTests`, `DailyPayoutFailureHandlerTests`, `DailyPayoutSettlementRunSqlServerTests`. `dotnet test tests/TripMate.Application.UnitTests` toàn bộ: 4684/4685 xanh (1 fail là test đo thời gian thực thi không liên quan, `GreedyWeightedIncumbentTests`, xanh lại khi chạy riêng — máy bận lúc chạy song song, không phải hồi quy từ thay đổi này).
  - Test payout SQL Server (19/19) và test HTTP endpoint UC-73 (9/9) đã chạy lại trên SQL Server thật — xanh. **Task 13 xong.**
- **Còn lại:** Task 12 — hỏi developer merge/PR/giữ nhánh/huỷ.
- **Chưa làm:** chạy migration Task 2B trên DB dev (chờ developer duyệt bản sao lưu 24 dòng Main), spec/plan FE, biên bản phê duyệt Owner cho BR-177/188/189.
- Chưa commit, chưa push, chưa mở PR.

## Thay đổi so với v5

- Q3–Q6 và các quy tắc tự thêm đã được developer duyệt theo đề xuất; spec v6 ghi lại.
- Task 2: bỏ cột tỷ lệ (Q5). Thêm archive dữ liệu `Main` tách thành migration riêng, chạy sau khi developer duyệt bản sao lưu (Q3).
- Task 2: thêm `payment.TourOperatorDebt` và cột khóa trên `dbo.Users` (Q4).
- Task 4: `Final` theo lượt 10:00 đầu tiên sau `EndAtUtc` (Q6).
- Task 6: đọc nợ từ `payment.TourOperatorDebt` thay vì danh sách rỗng.
- Task 7: khóa tối thiểu (`Lock`/`Unlock` trên `User`), thu hồi toàn bộ refresh token (BR-175) bằng phương thức mới.
- Task 8: bỏ cửa sổ 13:00 và chạy bù trong ngày (PD-7, PD-16).
- Giữ PD-7a và PD-17 như yêu cầu kỹ thuật, ghi rõ không có trong SRS.

## Quy tắc làm việc

- Theo `AGENTS.md` và `CLAUDE.md`. TDD cho mọi thay đổi có logic: đỏ (xác nhận đỏ đúng lý do), code tối thiểu, xanh, refactor.
- Một task một lần. Finding Critical chặn task tiếp theo.
- Database-first: SQL idempotent trong `database/migrations/`, cột thời gian `DATETIME2`. Không dùng `dotnet ef migrations add`.
- Không magic string: status, kind, action, lý do là `public const string`.
- `Result<T>` và `HandleFailure` cho lỗi nghiệp vụ.
- Không commit, push, hay mở PR cho đến khi developer chọn (Task 12).
- Không chạy migration archive hay xoá dữ liệu dev trước khi developer duyệt bản sao lưu.

Lệnh kiểm tra:
`dotnet build` và `dotnet test tests/TripMate.Application.UnitTests --nologo --filter "FullyQualifiedName~DailyPayout"`
Tích hợp SQL Server: `TRIPMATE_SQLSERVER_TEST_CONNECTION` trỏ vào SQLEXPRESS local.

## Task 0 — Kiểm kê hiện trạng (chỉ đọc, đã làm)

Kết quả đã xác nhận:
- `DailyPayoutEligibilityReader.cs`: nhánh `Main` (dòng ~138), `Retained` (dòng ~148), đọc `AccountStatus.Locked` (dòng ~43, ~177), `LoadOutstandingDebtByOperatorAsync` trả dictionary rỗng (dòng ~163, điểm tích hợp nhánh hoàn tiền), `LoadOutstandingAdjustmentsAsync` (carry over, sẽ gỡ).
- `PayoutSettlement.cs`: `PayoutSettlementKind` có `Main` và `Retained` (dòng ~27, ~32).
- `PayoutSettlementStatus.cs`: có `Scheduled`, `Processing`, `Settled`, `SettlementFailed`, `CarriedOver`; chưa có `Resolved`.
- `DailyPayoutAmountCalculator.cs`: `CalculateCommission` = floor(gross × rate / 100).
- Carry over: `PayoutAdjustment.cs`, `PayoutSettlementAdjustment.cs`, các handler/endpoint liên quan (7 file tham chiếu `CarriedOver`/`Carried Over`). Cần liệt kê lại trước khi xoá.
- Migration: `20261108_daily_payout_settlement_foundation.sql` tạo `payment.PayoutRun` (~dòng 72 là PayoutSettlement, ~112 Attempt, ~130 Adjustment); `20261022_uc46_payout_settlement_request.sql` tạo `payment.PayoutAdjustments` (~265).
- Khóa tài khoản: chưa có lệnh hay endpoint khóa/mở khóa. `AccountStatus`: Active = 2, Locked = 3. Thu hồi refresh token chỉ theo hash (`RevokeRefreshTokenAsync`).
- Dữ liệu dev (chỉ đọc): `payment.PayoutSettlement` 24 dòng, tất cả `Main`, `Settled`, ngày chạy 10/11–13/11/2026; `PayoutBatch` 4 dòng `Settled`; `PayoutSettlementAttempt` 4 dòng; `PayoutSettlementAdjustment` 0 dòng; `PayoutAdjustments` 0 dòng; 0 TO bị `Locked`.

**DoD:** đạt. Danh sách file và bảng trên được dùng làm đầu vào cho Task 2–9.

## Task 1 — Cổng duyệt (chặn)

- Developer đã duyệt hướng và các đề xuất Q3–Q6 (chat: "duyệt theo đề xuất của bạn").
- Chờ developer duyệt văn bản spec v6 và plan v6 trước khi viết code.
- Điều kiện trước khi merge (không chặn TDD các task không liên quan):
  - Biên bản phê duyệt của Owner cho BR-177, BR-188, BR-189 và quyết định 10/11, 11/11 được ghi vào SRS (Approver và Evidence). Hiện SRS ghi "PENDING".
  - Owner xác nhận quy tắc làm tròn (PD-3): TO nhận phần làm tròn xuống, TripMate giữ phần dư.
  - Lead/PO gán mã MSG cho thử lại theo lịch và chênh lệch đối soát.
- Developer duyệt bản sao lưu dữ liệu dev trước Task 2 (migration archive).
- **DoD:** biên bản duyệt ghi trong spec (mục "Quyết định đã chốt"). Không có code trước khi xong task này.

## Task 2 — Migration schema (database-first)

Chia thành hai file để tách rủi ro dữ liệu:

**2A. Schema idempotent** (`database/migrations/20261112_daily_payout_v6_schema.sql`)
- **Red:** migration chạy hai lần không lỗi; `Kind` chỉ nhận `Advance` hoặc `Final`; trạng thái `Resolved` hợp lệ; cột khóa tồn tại với kiểu đúng; bảng `TourOperatorDebt` tồn tại; ràng buộc `(BookingId, Kind)` là duy nhất.
- **Green:**
  - Đổi CHECK `Kind` sang `Advance`, `Final`.
  - Thêm `Resolved` vào CHECK trạng thái.
  - Thêm `dbo.Users.lock_reason NVARCHAR(200) NULL`, `dbo.Users.locked_at_utc DATETIME2 NULL` (Q4, Q5).
  - Tạo `payment.TourOperatorDebt` (`Id`, `TourOperatorUserId`, `Amount`, `RecoveredAmount`, `IncurredAtUtc DATETIME2`, `CreatedAtUtc DATETIME2`).
  - Không thêm cột tỷ lệ.
  - Không xoá bảng `PayoutAdjustments`, `PayoutSettlementAdjustment`.
- **DoD:** script chạy hai lần không lỗi qua `database/apply-schema.sh`.

**2B. Archive và gỡ đợt `Main`** (`database/migrations/20261112_daily_payout_v6_archive_main_rows.sql`)
- Chỉ chạy sau khi developer duyệt bản sao lưu (Task 1).
- Tạo `payment.PayoutSettlement_Archive_v3` và `payment.PayoutSettlementAttempt_Archive_v3` (cùng cấu trúc, cột thời gian `DATETIME2`).
- Sao chép toàn bộ dòng `Main` và attempt liên quan, kiểm tra số dòng khớp (24 dòng, 4 attempt) trước khi xoá.
- Xoá dòng `Main` và attempt tham chiếu, trong một transaction, idempotent (chạy lại không lỗi và không xoá thêm).
- **Red:** archive có đủ 24 dòng; sau khi chạy, không còn dòng `Kind = 'Main'`.
- **DoD:** developer xác nhận số dòng và bản sao lưu trước khi chạy trên DB dev.

## Task 3 — Calculator: `Advance`/`Final` (PD-3, PD-4)

- **Red:** `Advance = floor(PaidAmount × 70%)`; `Final = floor((PaidAmount − RefundAmount) × 20%)` với `RefundAmount = 0` trong phạm vi này; hoa hồng `floor(PaidAmount × CommissionRate)`; làm tròn: TO nhận floor, phần dư thuộc TripMate (ví dụ `PaidAmount = 1.000.001`: Advance 700.000, Final 200.000, phần dư 1 VND thuộc TripMate); trừ nợ và `Payout ≤ 0` không tạo lệnh; số tiền không đổi sau khi tạo (BR-146). Bỏ test cũ cho `Main`/`Retained`.
- **Green:** sửa `DailyPayoutAmountCalculator`, bỏ đường `Retained`, giữ `CalculateCommission` floor.
- **DoD:** test calculator xanh, gồm ca biên: nợ lớn hơn đợt, làm tròn, `Payout ≤ 0`.

## Task 4 — Điều kiện vào đợt (PD-1A, PD-2, PD-2A)

- **Red:**
  - `Advance` chỉ khi Awaiting Window 24 giờ đã kết thúc và booking không Cancelled.
  - `Final` chỉ ở lượt 10:00 (giờ VN) đầu tiên sau `TourSchedule.EndAtUtc` (Q6), và chưa có `Final`. Ví dụ: kết thúc 20/11 18:00 VN thì `Final` ở lượt 10:00 ngày 21/11.
  - Booking Cancelled trước lượt không tạo đợt và có audit log "cancelled before run".
- **Green:** sửa `DailyPayoutEligibilityReader`: thay nhánh `Retained` bằng điều kiện `Final`; thêm nhánh loại booking Cancelled có audit log; gỡ `LoadOutstandingAdjustmentsAsync`.
- **DoD:** test eligibility xanh; test `DailyRun_BookingStillInsideItsAwaitingWindow_IsExcludedUntilTheWindowEnds` vẫn xanh.

## Task 5 — Lượt chạy tạo đợt (PD-1, PD-5, PD-11, PD-14)

- **Red:** lượt chạy tạo đúng một `Advance` và đúng một `Final` cho booking đủ điều kiện; chạy lại cùng `RunDate` không tạo trùng (ràng buộc `(BookingId, Kind)`); gom một lệnh cho mỗi TO mỗi ngày; lượt chạy chỉ ở 10:00 (giờ VN), kể cả cuối tuần và ngày lễ.
- **Green:** sửa `DailyPayoutRunProcessor` để tạo hai loại đợt; giữ `PayoutRun` trong cùng transaction.
- **DoD:** test lượt chạy xanh.

## Task 6 — Bù trừ nợ treo theo TO (PD-15, BR-144, BR-188)

- **Red:**
  - Nợ trừ lần lượt theo thứ tự `IncurredAtUtc`.
  - Phần vượt nợ mới được chuyển.
  - Khi nợ lớn hơn đợt thì `Payout ≤ 0`, không tạo lệnh, nợ giữ cho lượt sau.
  - Đọc đúng từ `payment.TourOperatorDebt` (số còn lại = `Amount − RecoveredAmount`).
- **Green:** triển khai `LoadOutstandingDebtByOperatorAsync` đọc `TourOperatorDebt` (thay dictionary rỗng); giữ `remainingDebt` trong `DailyPayoutAmountCalculator`.
- **Ghi chú:** nhánh hoàn tiền sẽ ghi vào `TourOperatorDebt`. Ngoài phạm vi, không triển khai ghi nợ ở đây.
- **DoD:** test bù trừ xanh.

## Task 7 — Khóa tự động TO (BR-189, BR-169, BR-175, PD-9, PD-9A)

- **Red:**
  - Nợ chưa thu hồi sau 15 ngày thì TO bị khóa với lý do `Unrecovered pending debt (BR-189)` (hằng số `public const string`).
  - Khóa gọi `User.Lock(lý do)`; thu hồi toàn bộ refresh token của user (BR-175), qua phương thức mới theo user (hiện chỉ có thu hồi theo hash).
  - Ghi audit log bất biến (BR-169).
  - MSG198 gửi TO và Staff.
  - Đợt của TO giữ `Scheduled`.
  - Endpoint Staff mở khóa chỉ cho TO: từ chối với MSG199 khi nợ còn lại lớn hơn 0; mở khi nợ đã thu hồi đủ; `User.Unlock` xoá `lock_reason` và `locked_at_utc`.
- **Green:** bước trong lượt chạy kiểm tra nợ quá hạn và gọi `User.Lock`; thêm `RevokeAllRefreshTokensAsync(userId)`; endpoint mở khóa.
- **Phạm vi:** khóa thủ công của Staff (BR-168) và UC-48 đầy đủ để sau. Không tạo cơ chế khóa thứ hai.
- **DoD:** test khóa xanh; kiểm tra không còn đường đăng nhập sau khi bị khóa.

## Task 8 — Gửi, thử lại, đối soát (PD-6, PD-7, PD-7a, PD-8, PD-17)

- Giữ logic hiện có, chạy lại toàn bộ test trên kind mới.
- **Thử lại (PD-7):** lỗi thì thử lại sau 1 giờ, 2 giờ, 3 giờ; tối đa 3 lần. Bỏ cửa sổ 13:00 khỏi code và test.
- **Bỏ chạy bù trong ngày (PD-16):** gỡ test cũ về chạy bù; booking bị lỡ xử lý ở lượt kế tiếp.
- **Red:** lịch thử đúng 1, 2, 3 giờ; hết số lần thì `SettlementFailed` và thông báo Administrator trong 24 giờ (BR-172); PD-7a tra cứu trước khi thử lại, nếu đã `Settled` thì không gửi lại; PD-17 ghi `PayoutReconciliationItem` khi lệch.
- **Green:** sửa scheduler thử lại, thêm thông báo BR-172.
- **DoD:** test xanh; số lần gọi gateway đúng; thông báo BR-172 có test.

## Task 9 — Thao tác của Administrator (UC-73, PD-10)

- **Red:**
  - `[Retry Transfer]` chỉ cho `SettlementFailed` (MSG177 nếu sai trạng thái); gửi lại cùng số tiền; đi qua PD-7a; thất bại lại thì `SettlementFailed` và MSG176.
  - `[Mark as Resolved]` bắt buộc lý do (MSG178), chuyển sang `Resolved`, không chuyển tiền.
  - Mọi thao tác ghi audit log.
  - Không có thao tác chuyển thủ công và carry over.
- **Green:** endpoint và handler cho hai thao tác. Gỡ endpoint, handler và test carry over hiện có (theo kết quả Task 0 và Q3).
- **DoD:** test xanh; không còn tham chiếu `CarriedOver` trong code nghiệp vụ.

## Task 10 — API đọc (UC-72, PD-13)

- **Red:** UC-72 hiển thị đợt `Advance` và `Final` riêng; `Resolved` hiển thị đúng; dòng ảo `Awaiting Window` vẫn đúng; TO chỉ thấy đợt của chính mình (lịch sử theo booking, screen 94).
- **Green:** sửa handler đọc và DTO cho kind và trạng thái mới.
- **DoD:** test xanh; không phá `DailyPayoutReadQueryTests`.

## Task 11 — Kiểm thử tích hợp SQL Server

- Lượt 10:00 end-to-end: `Advance` đúng sau 24 giờ; `Final` đúng ở lượt đầu tiên sau kết thúc tour; chạy lại không trùng.
- Khóa tự động BR-189 end-to-end, gồm audit, thu hồi token và MSG198.
- Mở khóa bị từ chối khi còn nợ (MSG199).
- Archive 24 dòng `Main` khớp trước khi xoá (Task 2B).
- Các test SQL Server v3 vẫn xanh (`DailyPayoutSettlementRunSqlServerTests`, `DailyPayoutSettlementActionsEndpointTests`, `PayoutLifecycleMigrationSqlServerTests`).
- Lưu ý: kiểu cột thời gian phải là `DATETIME2`; unit test InMemory không bắt được lỗi này.
- **DoD:** tổng test SQL Server liên quan payout xanh. Ghi pass/fail/skip và exit code.

## Task 12 — Báo cáo và kết thúc

- `dotnet build` và `dotnet test` toàn bộ solution.
- Rà diff với spec v6, liệt kê điểm lệch.
- Kiểm tra định dạng file C# đã thay đổi (`dotnet format --verify-no-changes --include <files>`), chuẩn hóa CRLF → LF theo develop.
- Hỏi developer: merge, mở PR, giữ nhánh, hay hủy.

## Phụ thuộc và rủi ro

- **Nhánh hoàn tiền** dùng `payment.TourOperatorDebt` và số tiền đã chuyển cho TO (BR-139). Thứ tự merge: payout trước.
- **Báo cáo doanh thu** (UC-44, UC-45, BR-173) bị ảnh hưởng khi hoa hồng và doanh thu thuần thay đổi. Theo dõi riêng, ngoài phạm vi.
- **Spec FE** (`specs/DAILY-PAYOUT-TRANSFER-fe-spec.md`) cần cập nhật theo v6 sau khi BE được duyệt, ở `Capstone_FE-fix-daily-payout`.
- **Số tiền thật.** Dù chế độ giả lập, công thức và khóa tự động ảnh hưởng tiền thật khi triển khai. Test công thức và nợ phải đầy đủ trước khi merge.
- **Dữ liệu dev.** 24 đợt `Main` xử lý theo Task 2B sau khi developer duyệt bản sao lưu.
- **Gate Owner.** BR-177, BR-188, BR-189 chỉ merge sau biên bản Owner trong SRS.
