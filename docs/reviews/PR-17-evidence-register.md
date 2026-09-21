# PR #17 — Evidence Register (mẫu §29 TEAM_ENGINEERING_RULES v2.0)

> Cách dùng: đây là bằng chứng đính kèm PR description. Các ô `__[…]__` là bắt buộc phải điền trước khi request re-review — theo §2, ô trống không được tính là pass. Trạng thái dùng Đ (đạt, có bằng chứng) / C (chưa kiểm tra) / NA (không áp dụng, kèm lý do).
>
> **Cảnh báo quan trọng (§20):** các Run dưới đây thực hiện trên working tree ngày 19/09/2026 **trước khi commit** (base branch `develop` @ `3796e29`, head commit lúc đó `b55b7b5` + thay đổi chưa commit). Sau khi commit, head SHA sẽ đổi — phải điền SHA mới và để CI chạy lại trên đúng head (hoặc chạy lại local đúng head) trước khi bằng chứng được tính là hợp lệ.

## 0. Hồ sơ (mẫu §27)

| Mục | Giá trị |
|---|---|
| Task/UC | UC-68 View Audit Logs + UC-69 View Audit Log Details (kèm Result/Reason amendment duyệt 2026-09-18) |
| Repo/PR | `Capstone_BE` / PR #17 (branch `feature/linhnv-view-audit-log-details`) |
| Author / Reviewer | __[tên bạn]__ / Antigravity Production Reviewer (leader) |
| Base branch / SHA | `develop` @ `3796e29b678e78eb0322d4adce20c83e588784cb` |
| Head SHA (sau commit) / CI merge SHA | __[điền sau khi commit]__ / __[nếu CI chạy merge ref, ghi cả hai]__ |
| Worktree | `D:\study\Project-Capstone\Capstone_BE` (và `Capstone_FE` cho phần Frontend) |
| Ngày / môi trường / schema | 19/09/2026 (UTC+7), local Windows, .NET __[dotnet --info]__ · **SQL Server local (SQLEXPRESS, Windows auth) đã dùng cho test migration/rollback** qua `TRIPMATE_SQLSERVER_TEST_CONNECTION` (harness tự tạo/xoá DB test — `SqlServerTestDatabase.cs`) |

## 1. Run — bằng chứng lệnh chạy (ĐẠT, cần refresh SHA)

| # | Command | Kết quả | Ghi chú |
|---|---|---|---|
| R1 | `dotnet build TripMate.slnx -c Release` | Build succeeded, **0 warning**, exit 0 | Ngày 19/09, giờ __[…]__ (UTC+7) |
| R2a | `dotnet test` (toàn solution, **không** set SQL env) | **495 pass / 0 fail / 19 skip** = Infra 4/4, Application 334/334, Api.Integration 157 pass + 19 skip | 19 skip = 16 test SQL legacy + 3 test migration mới, tất cả đều gate theo `TRIPMATE_SQLSERVER_TEST_CONNECTION` (quy tắc L08: skip không tính là pass) |
| R2b | `TRIPMATE_SQLSERVER_TEST_CONNECTION="Server=localhost\SQLEXPRESS;…;Trusted_Connection=True" dotnet test` (toàn solution) | **514 pass / 0 fail / 0 skip** = Infra 4/4, Application 334/334, **Api.Integration 176/176** | **Lần chạy zero-skip đầu tiên** (1m21s). Bao gồm 3 test SQL mới: migration trên DB mới / DB cũ có dữ liệu / chạy lại lần 2; truy vấn biên ngày + phân trang trên SQL Server; rollback nghiệp vụ nhưng audit Failure vẫn được lưu |
| R2-fail (lịch sử) | Lần chạy toàn bộ đầu tiên sau khi thêm nhóm test HTTP mới | **Failed: test-host initialization error** — nhóm test mới chạy song song ngoài `[Collection(TripMateApiFactory)]` | Đã sửa: đưa `SafeLoggingTests`/`AuditLogDetailEndpointTests`/`AuditOutcomeEndpointTests` vào collection chung; các run R2a/R2b sau đó xanh. **Lỗi này được giữ trong lịch sử bằng chứng theo §20, không xoá** |
| R3 | `dotnet format TripMate.slnx --verify-no-changes --no-restore` | **0 lỗi** sau khi đã fix 36 vi phạm format (34 final-newline + 2 whitespace) | Fix bằng `dotnet format`, verify lại = 0 |
| R4 | `git diff --check` | exit 0, không whitespace error | C37 |
| R5 (FE) | `npm run lint` && `npm run typecheck` | 0 error / 0 warning; typecheck pass | Repo `Capstone_FE`, node __[node -v]__ |
| R6 (FE) | `npm test` | **73/73 pass** (11 files) | Bao gồm AuditLogDetailDrawer + AuditLogResultReason (Result/Reason null-states) tests |
| R7 (FE) | `npm run build` | Success — 21 static pages, có `/admin/audit-logs` | Production build, C29 — chạy lại 19/09 sau thay đổi Result/Reason |
| R8 | CI trên head cuối | __[C — chờ sau push; bot/CodeRabbit status không thay thế job build/test]__ | §20 |

## 2. Finding — 13 finding của leader review (mục §29 Finding)

| ID | Severity | Tình huống / vị trí (head hiện tại) | Actual → Expected | Nguồn rule |
|---|---|---|---|---|
| SEC-01 | P0 | Static JWT fallback key tại `Program.cs:104-113`, `JwtTokenService.cs:31-34` | Key cứng "us+B0GY0…" âm thầm dùng khi thiếu config → fail-fast throw `InvalidOperationException` | AGENTS.md §5.4 |
| SEC-02 | P0 | Connection string trong `appsettings.json:10`, `appsettings.Development.json:9` | SQLEXPRESS conn string commit trong git → rỗng, chuyển User Secrets (`UserSecretsId` trong csproj) + startup guard | AGENTS.md §5.4, README §setup |
| ARCH-01 | P1 | `AuditLog.cs` public ctor + public setters | Entity mutable → private ctor, `private set`, factory `CreateRecordedOutcome` validate invariant | AGENTS.md §2.1, BR-130 |
| CONTRACT-01 | P2 | `GetAuditLogsQueryValidator.cs` (rule date-range) chặn trước handler → 400, nhánh 422 trong handler thành dead code | 400 ≠ spec 422 → bỏ rule khỏi validator, handler là đường duy nhất trả `Result.Failure` 422 | UC-68-spec |
| SOURCE-01 | P2 | MSG129 (locked = success toast) bị gán cho 404; MSG126 tự chế tại `GetAuditLogDetailQueryHandler.cs:27` | Sai locked catalog → locked MSG126 nguyên văn; 404 đổi sang **MSG132 (proposed)**, ghi rõ lý do trong spec/plan | SRS §5.3, L17 |
| SEC-03 | P2 | `GetAuditLogDetailQueryHandler.cs:54-55` trả raw `beforeData`/`afterData` | Vi phạm BR-03 → `AuditLogPayloadRedactor` che credential ở read boundary (không sửa data lưu) | BR-03, §3.9.12.2 |
| SOURCE-02 | P2 | SRS yêu cầu cột Result/Reason/Client Platform/Affected Module, schema không có | Tự bỏ qua không duyệt → **Decision Record 2026-09-18** (developer-approved): thêm `result`+`reason` qua migration; module/platform out-of-scope chờ Lead/PO | §4 nguồn quyết định |
| TEST-01 | P2 | Không integration test cho 2 endpoint mới | Thêm `AuditLogDetailEndpointTests`, `AuditOutcomeEndpointTests`, `SafeLoggingTests` dùng JWT bearer pipeline thật (401/403/200/404) | Checklist C13 |
| SCOPE-01 | P3 | `plans/UC-51-plan.md`, `specs/UC-51-…-spec.md` trong PR diff | Out-of-scope → đã xoá khỏi tree. **Ghi chú:** mapping UC-49/50 error codes trong `ApiControllerBase` được giữ vì shared với UC-49/50 (đã ghi trong remediation plan mục 3) | SCOPE rule |
| SCOPE-02 | P3 | 1 dòng trắng thừa cuối `20260912_add_travel_group_creation_requests.sql` | Đã gỡ (diff hiện chỉ là -1 blank line) | C37 |
| PERF-01 | P3 | `GetAuditLogsQueryHandler.cs:84` `EF.Functions.Like(AffectedEntityId.ToString(),…)` non-sargable | → `long.TryParse` + exact match; **hành vi đổi**: keyword số khớp chính xác ID, không còn substring; đã đồng bộ spec BE+FE | C23, plan UC-68 |
| DB-01 | P3 | Sort chỉ theo `CreatedAtUtc`, thiếu tie-breaker | → `.ThenByDescending(a => a.Id)`, phân trang deterministic | C23 |
| HYGIENE-01 | P3 | PR description trống | **C — chính file này + skeleton mục 5 để điền** | Mẫu §30 |

## 3. Resolution — từng finding đóng như thế nào (mục §29 Resolution)

| ID | Cách sửa | Regression test | Docs đồng bộ | Trạng thái |
|---|---|---|---|---|
| SEC-01 | Throw khi thiếu SigningKey ở cả 2 nơi; grep toàn repo = 0 occurrence key cũ | Startup guard được exercised gián tiếp qua factory test (factory luôn set key riêng) | — | Đ locally — **chờ re-review** |
| SEC-02 | appsettings rỗng; `UserSecretsId` + user-secrets local; guard conn string; factory test set dummy khi InMemory | Integration suite xanh qua guard | `README.md`, `.env.example` (đã sửa trong tree) | Đ locally — chờ re-review. **Secret đã lộ trong git history: cần rotate (owner: author)** |
| ARCH-01 | Encapsulation restore + factory guard độ dài/enum/id | `AuditLogOutcomeTests` (invalid outcome, reason >1000, length guard) | UC-69-spec remediation section | Đ — chờ re-review |
| CONTRACT-01 | Xoá rule validator (kèm comment lý do), handler giữ 422 | `Handle_WhenInvalidDateRange_ShouldReturnInvalidDateRangeFailure` (unit, giờ là đường đi thật) | UC-68-spec (bảng MSG + note MSG131) | Đ — chờ re-review |
| SOURCE-01 | Locked text cả 2 handler; MSG129→MSG132 | `GetAuditLogDetailQueryHandlerTests` (assert locked text) | UC-68/69 spec+plan, FE spec | Đ — **MSG132/133 chờ duyệt catalog riêng** |
| SEC-03 | Redactor (nested object/array, serialized string, change-set field naming, malformed JSON → REDACTED) | `AuditLogSecurityTests` + integration masked-response + persisted-payload-unchanged | UC-69-spec (BR-03/PC-01, MSG133) | Đ — chờ re-review |
| SOURCE-02 | Migration idempotent + enum + factory + DTO; không backfill | `AuditOutcomePersistenceTests` (InMemory — có giới hạn, xem mục 4) | **Decision Record trong UC-69-spec**, FE spec amendment sections | Đ — module/platform chờ Lead/PO |
| TEST-01 | 3 file integration test, JWT thật, TestServer | Chính các test đó | — | Đ — chờ re-review |
| SCOPE-01/02 | Xoá file UC-51; gỡ newline | `git diff --name-only` kiểm tra inventory | — | Đ — chờ re-review |
| PERF-01 | `long.TryParse` exact match | `Handle_WhenKeywordIsNumericId_…` | UC-68-spec BE+FE (exact numeric match) | Đ — chờ re-review |
| DB-01 | Tie-breaker Id | Unit test ordering hiện có + biên ngày/phân trang trên SQL Server thật (R2b) | — | Đ |
| HYGIENE-01 | Điền PR description từ mục 5 | — | — | **C — author** |

## 4. Nghiệm thu & giới hạn trung thực (mục §29 Nghiệm thu)

**Đã chạy:** Unit (Application/Infra) + Integration qua `WebApplicationFactory`/TestServer với **JWT bearer pipeline thật** (401 thiếu/sai token, 403 sai role, 200, 404, System actor, redaction, payload lưu không đổi). FE: component tests + production build. Fixture hoàn toàn test-local, không dùng dữ liệu thật người dùng.

**Đã chạy bổ sung (19/09, nâng lên Đ):**
- **Migration trên SQL Server thật (D05/C19)** — Đ: 3 test `AuditMigrationSqlServerTests` pass trong R2b — apply trên DB mới, DB cũ có dữ liệu phiên bản trước, và chạy lại lần 2 (idempotent); dữ liệu lịch sử còn nguyên, không backfill.
- **16 test SQL bị skip từ trước** — Đ: toàn bộ pass trong R2b (176/176 Integration, zero skip).
- **Che `reason` + middleware chưa chạy** — Đ: 2 regression test mới tái hiện được lỗi (reason mask thiếu, middleware chưa đăng ký), sửa xong pass tập trung; middleware đã đăng ký `Program.cs:162`.

**Chưa kiểm tra (C) — không được tính là pass:**

| Mục | Lý do | Điều kiện đóng |
|---|---|---|
| Manual endpoint check sau migration (checkbox template "Migration applied locally") | Endpoint đã được cover bởi integration test trên TestServer (R2b), nhưng chưa có thao tác Swagger/HTTP thủ công trên DB đã migrate | Gọi 2 endpoint qua Swagger/HTTP client với dữ liệu có `result`/`reason` trên DB đã apply |
| U03 (response cũ về sau response mới) / V01 (overflow 320px) | Chưa mô phỏng trong lần này (V01: author tự kiểm tra UI 320px theo kế hoạch) | Hoặc bổ sung simulation, hoặc ghi NA kèm lý do được reviewer chấp thuận |
| CI trên head cuối | Chưa push | Job build/test xanh trên đúng head/merge SHA |

**Quyết định còn mở:** MSG131 (UC-68 date-range), MSG132 (UC-69 404), MSG133 ([REDACTED]) — đều ghi "proposed" trong spec, runtime dùng neutral wording/errorCode, chờ Lead/PO duyệt thêm vào SRS §5.3. `client_platform`/`affected_module` — chờ quyết định riêng.

## 5. PR description skeleton (mẫu §30 — dán vào PR #17, thay `__[…]__`)

```markdown
## Summary
Adds UC-68 (audit log list) + UC-69 (audit log detail) admin endpoints with full remediation
of the 13 cross-review findings: removed hardcoded JWT fallback key and committed connection
strings (fail-fast startup guards + User Secrets), restored AuditLog immutability (private
setters + validated factory), aligned error contract (422 date-range via handler, locked
MSG126 wording, proposed MSG132 for 404), response-only BR-03 redaction, approved Result/Reason
schema amendment (migration 20260918, decision record in specs/UC-69-spec.md), new JWT
integration tests, and deterministic pagination + sargable keyword search.
Behavior change: numeric keyword now matches AffectedEntityId exactly (not substring) — specs updated.

## Affected endpoints and features
- GET /api/v1/admin/audit-logs (list + filters + pagination, result column)
- GET /api/v1/admin/audit-logs/{id} (detail + result/reason + redaction)
- Failure audit recording for CreatePoi / ApproveOperatorApplication (pipeline behavior)
- Request rejection logging middleware (401/403/400/422 → structured log, not AuditLogs)

## Related task or issue
Jira: __[TM-114/TM-115]__ · Evidence register: docs/reviews/PR-17-evidence-register.md

## Validation performed
- [x] dotnet build (Release, 0 warnings) — R1
- [x] dotnet test — **514 pass / 0 skip** with local SQL Server env (R2b); 495 pass / 19 SQL-gated skips without (R2a); one earlier failed run kept in evidence history (R2-fail)
- [x] Migration verified on real SQL Server: new DB, upgraded DB, re-run (AuditMigrationSqlServerTests) — D05
- [x] dotnet format --verify-no-changes = 0 — R3 · git diff --check — R4
- [x] FE: lint + typecheck + tests + production build — R5–R7
- [ ] Manual endpoint checks via Swagger post-migration — pending
- [ ] V01 320px UI check — pending (author)

## Database changes
- dbo.AuditLogs: + result VARCHAR(20) NULL (CHECK Success/Failure), + reason NVARCHAR(1000) NULL
- Migration: database/migrations/20260918_add_audit_result_reason.sql (idempotent; no backfill)
- Full schema updated: database/tripmate_schema_v7.sql
- Merge order: apply migration BEFORE deploying this code (see Known limitations)

## Known limitations
- SQL-gated tests (19) skip unless TRIPMATE_SQLSERVER_TEST_CONNECTION is configured (verified locally: 514/0 with it).
- MSG131/132/133 are proposals pending SRS 5.3 catalog approval; runtime uses errorCode + neutral text.
- client_platform / affected_module intentionally out of scope (pending Lead/PO decision).
- Legacy rows keep result/reason = NULL (no backfill) — UI shows "No recorded result/reason".
- FE admin credential previously committed must be rotated by owner (git history).

## Reviewer notes
Focus areas: AuditFailureBehaviour allowlist semantics (only 2 commands; validation/auth
excluded), AuditLogPayloadRedactor coverage, startup guards breaking local runs without
User Secrets (README updated), and the numeric-keyword exact-match behavior change.

## Definition of Done
- [x] matches assigned scope (UC-51 files removed from this PR)
- [x] Clean Architecture respected (Domain pure; Result<T>; no business logic in controllers)
- [x] no secrets/credentials/connection strings committed
- [x] README.md and .env.example updated for the new configuration
- [ ] migration verified on real DB + re-review approval (remaining gate)
```
