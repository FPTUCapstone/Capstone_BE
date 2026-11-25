# Phase 1 Baseline — Build & Test

**Task 4 — Phase 1**  
Ngày: 25/11/2026. Đây là snapshot trạng thái CI/baseline TRƯỚC khi bắt đầu viết code direct payment.

---

## 1. Build baseline

**Command:** `dotnet build TripMate.slnx --configuration Release`  
**Result:** ✅ **Build succeeded. 0 Warning(s). 0 Error(s).**  
**Duration:** 1 min 56 s

**9 projects built:**
- TripMate.Domain
- TripMate.Application
- TripMate.Infrastructure
- TripMate.Api
- TripMate.Infrastructure.CandidatePoolReplay
- TripMate.Domain.UnitTests
- TripMate.Application.UnitTests
- TripMate.Infrastructure.UnitTests
- TripMate.Api.IntegrationTests

---

## 2. Test baseline

**Command:** `dotnet test TripMate.slnx --configuration Release --no-build`  
**Result:** ✅ **Exit code 0 (passed)**  
**Duration:** 5 m 17 s

| Metric | Count |
|--------|-------|
| Passed | **1028** |
| Failed | **0** |
| Skipped | **829** |
| **Total** | **1857** |

### Skipped tests — expected

Tất cả 829 test bị skip là **SqlServer integration tests** (`[SqlServerFact]` / `[Trait("Category", "SqlServer")]`). Các test này cần SQL Server live instance và được thiết kế để skip khi không có connection string — đây là behavior đúng, không phải blocker.

Ví dụ các test class bị skip:
- `RefundUc42SchemaMigrationSqlServerTests` (8 tests)
- `JoinTravelGroupSqlServerTests` (nhiều tests)
- `GroupInvitationSqlServerTests` (nhiều tests)
- `CreateTravelGroupSqlServerTests` (nhiều tests)

### Passed tests breakdown

Tất cả 1028 test pass đều là unit tests và non-SQL integration tests. Không có failed test nào.

---

## 3. Blockers hạ tầng

| Hạng mục | Trạng thái | Ghi chú |
|---|---|---|
| Docker / SQL Server live | **N/A — không cần** | SqlServer tests chạy in-process với `SqlServerTestDatabase.CreateEmptyAsync()` — cần connection string; để lại skip |
| Redis | Không kiểm tra | Không có test Redis nào trong baseline |
| .NET SDK | ✅ OK | .NET 10, build thành công |

---

## 4. Done criteria — Task 4

- ✅ `dotnet build TripMate.slnx` → 0 warning, 0 error
- ✅ `dotnet test TripMate.slnx` → exit code 0, 1028 passed, 0 failed
- ✅ Skipped 829 tests = SQL Server integration tests (expected, không coi là pass)
- ✅ Không reset Docker/DB
- ✅ Baseline có kết quả thật

---

## 5. Trạng thái clean slate cho Phase 1

Build và unit test suite hoàn toàn green. Không có blocker nào cản trở bắt đầu Task 5 (domain entities + SQL migration).
