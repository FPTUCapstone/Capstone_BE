# ACTIVE-TRIP-STAFF-ACCESS — Implementation Plan (BE)

Spec: `specs/ACTIVE-TRIP-STAFF-ACCESS-spec.md` (đã duyệt — phương án giữ Administrator + thêm Staff: `Roles = "Staff,Administrator"`)
Branch: `fix/linhnv-view-active-trip` (base `develop`)

## Task duy nhất (TDD: đỏ → xanh → refactor)

**T1 — Cho phép Staff truy cập UC-58/UC-59, giữ nguyên Administrator.**

1. Đỏ:
   - Sửa `GetActiveTripsQueryHandlerTests.cs`: đổi `Handle_NonAdministrator_ReturnsForbidden` thành test role thật sự bị chặn (vd. `"TourOperator"`), thêm test mới `Handle_Staff_ReturnsSuccess` (role `"Staff"` không bị Forbidden).
   - Thêm test tương đương cho `GetActiveTripDetailsQueryTests.cs` nếu đang có test "non-Administrator" tương tự; nếu chưa có test role nào ở đây thì thêm cả hai (Staff thành công, role khác bị Forbidden).
   - Rà soát `tests/TripMate.Api.IntegrationTests/ActiveTrips/ActiveTripsEndpointTests.cs` và `ActiveTripDetailsEndpointTests.cs`: nếu có test dựng client với role khác Administrator để khẳng định 403, đổi role đó sang một role chắc chắn vẫn bị chặn (vd. `TourOperator`/`Traveler`), thêm test Staff → 200.
   - Chạy test, xác nhận các test mới đỏ vì code chưa đổi.
2. Xanh:
   - `AdminActiveTripsController.cs:12`: `[Authorize(Roles = "Administrator")]` → `[Authorize(Roles = "Staff,Administrator")]`.
   - `GetActiveTripsQueryHandler.cs:24`: `currentUserService.Role != "Administrator"` → `currentUserService.Role is not ("Staff" or "Administrator")`.
   - `GetActiveTripDetailsQueryHandler.cs:28`: tương tự.
   - Chạy lại toàn bộ test active-trips, xác nhận xanh.
3. Refactor: không cần — thay đổi nhỏ, không có logic lặp lại cần rút gọn.

## Không làm trong plan này

- Không đổi `ActiveTripErrorCodes`, message, DTO, audit log (đã đúng, xem spec mục 2).
- Không đụng các controller/UC khác.

## Xác minh cuối

- `dotnet build`
- `dotnet test` (unit + integration; integration cần SQL Server local đang chạy nếu có test `[SqlServerFact]` trong phạm vi bị ảnh hưởng — các file trên dùng `CustomWebApplicationFactory` thường không cần SQL Server thật, xác nhận lại khi chạy).
- `dotnet format --verify-no-changes` trên các file đã đổi trước khi commit (rút kinh nghiệm từ lỗi CI của UC-41).

## Cần người duyệt

- Plan này chỉ có 1 task, phạm vi nhỏ — xin duyệt để triển khai thẳng, không cần chia nhỏ thêm.
