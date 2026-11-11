# ACTIVE-TRIP-STAFF-ACCESS — Spec (draft, chờ duyệt)

Nhánh: `fix/linhnv-view-active-trip` (BE, FE từ `develop`)
Nguồn: SRS mới §3.9.6.1 (UC-58 View Active Trips), §3.9.6.2 (UC-59 View Active Trip Details), BR-115, BR-124, BR-130, BR-51.

## 0. Bối cảnh / lỗi quan sát được

Tài khoản đăng nhập role Staff mở "Active Trips" (đúng theo menu Staff Workspace mà FE đã có) và bị chặn:
`Access denied — You do not have permission to access this function.`

Đối chiếu SRS mới: §3.9.6.1 và §3.9.6.2 ghi rõ Actor/Roles = **Staff**, Credentials "Active account holding the Staff role", Abnormal Case 1.a1 "signed-in account does not hold the Staff role → deny MSG126". Code BE hiện tại lại chỉ cho `Administrator`. Đây là lỗi lệch spec, không phải hành vi đúng.

## 1. Phạm vi

Trong phạm vi:
- Sửa authorization của 2 endpoint đã có để Staff truy cập được, theo đúng SRS mới:
  - `GET /api/v1/admin/trips/active` (UC-58)
  - `GET /api/v1/admin/trips/active/{tripId}` (UC-59)
- Cập nhật 2 nơi đang hard-code kiểm tra role trong Application layer cho khớp.
- Cập nhật/bổ sung unit test + integration test phản ánh role mới được phép.

**[Điểm cần Owner/bạn xác nhận khi duyệt spec]** SRS không nhắc đến Administrator ở UC-58/59 (trừ một chỗ PC-01 của UC-59 ghi "The Administrator sees..." — nhiều khả năng là sót lại khi copy từ UC khác, vì toàn bộ phần còn lại của 3.9.6.2 đều ghi Staff). Spec này đề xuất phương án **giữ nguyên Administrator + thêm Staff** (`Roles = "Staff,Administrator"`), vì:
  - Không gây regression cho Administrator đang dùng được.
  - Các UC admin-console khác trong cùng SRS theo mẫu "Administrator làm được mọi thứ Staff làm được, cộng thêm việc riêng của Administrator" (xem UC Lock/Unlock Account, Table 5).
  - Nếu bạn muốn đúng y văn bản SRS (chỉ Staff, bỏ Administrator), nói rõ khi duyệt — Task 1 bên dưới sẽ đổi thành `Roles = "Staff"`.

Ngoài phạm vi:
- Không đổi hành vi dữ liệu, không đổi DTO, không đổi audit log (đã dùng `currentUserService.UserId` thật, không hard-code "Administrator" — không cần sửa).
- Không đổi text tiêu đề trang FE "Administrator monitoring" (thuộc spec FE riêng, xem `ACTIVE-TRIP-STAFF-ACCESS-fe-spec.md`).
- Không đổi message MSG126 hay mã lỗi `ActiveTrips.Forbidden`.
- Không đổi các UC admin-console khác (chỉ UC-58/UC-59).

## 2. Hiện trạng code (đã kiểm tra)

| Hạng mục | Vị trí | Trạng thái |
|---|---|---|
| Controller Authorize | `Controllers/V1/AdminActiveTripsController.cs:12` | `[Authorize(Roles = "Administrator")]` cho cả GET list và GET details — chỉ Admin |
| Check role trong handler (list) | `Features/Admin/ActiveTrips/GetList/GetActiveTripsQueryHandler.cs:24` | `currentUserService.Role != "Administrator"` → Forbidden |
| Check role trong handler (details) | `Features/Admin/ActiveTrips/GetDetails/GetActiveTripDetailsQueryHandler.cs:28` | `currentUserService.Role != "Administrator"` → Forbidden |
| Audit log actor | `GetActiveTripDetailsQueryHandler.cs:65-71` | Dùng `currentUserService.UserId` thật (không hard-code "Administrator") — không cần sửa |
| Error code / message | `ActiveTripErrorCodes.cs` | `Forbidden = "ActiveTrips.Forbidden"`, message cố định "You do not have permission to access this function." — giữ nguyên |
| Unit test (list) đang khóa hành vi cũ | `tests/TripMate.Application.UnitTests/Features/Admin/ActiveTrips/GetActiveTripsQueryHandlerTests.cs:12` | `Handle_NonAdministrator_ReturnsForbidden` — cần đổi tên/nội dung vì Staff không còn là "non-Administrator bị chặn" |
| Unit test (details) | `tests/TripMate.Application.UnitTests/Features/Admin/ActiveTrips/GetActiveTripDetailsQueryTests.cs` | cần kiểm tra có test tương tự không, cập nhật nếu có |
| Integration test (endpoint auth) | `tests/TripMate.Api.IntegrationTests/ActiveTrips/ActiveTripsEndpointTests.cs`, `ActiveTripDetailsEndpointTests.cs` | cần rà soát test khẳng định 403 cho role khác Administrator |

## 3. Thay đổi cụ thể (Task 1 trong plan)

- `AdminActiveTripsController.cs`: `[Authorize(Roles = "Administrator")]` → `[Authorize(Roles = "Staff,Administrator")]` (hoặc `"Staff"` nếu bạn chọn phương án chỉ Staff khi duyệt).
- `GetActiveTripsQueryHandler.cs:24` và `GetActiveTripDetailsQueryHandler.cs:28`: đổi điều kiện kiểm tra role tương ứng (ví dụ `currentUserService.Role is not ("Staff" or "Administrator")`).
- Cập nhật `GetActiveTripsQueryHandlerTests.cs` (và test tương đương cho details nếu có): test role bị chặn đổi sang một role khác (vd. `"TourOperator"` hoặc `"Traveler"`) thay vì coi Staff là bị chặn; thêm test xác nhận Staff gọi thành công.
- Rà soát integration test 403 trong `ActiveTripsEndpointTests.cs` / `ActiveTripDetailsEndpointTests.cs`: cập nhật vai trò dùng để test "bị từ chối" nếu đang dùng Staff.

## 4. Business Rules liên quan

- BR-115: "Only an account holding the Staff role may open the web administration application." — đây là điều kiện SRS đưa ra cho UC-58/59, không nói về Administrator.
- BR-124: chức năng chỉ đọc (read-only) — không đổi.
- BR-51: không lộ vị trí chính xác của Traveler trừ khi họ bật location-sharing — không đổi, code hiện tại đã đúng.
- BR-130: ghi audit khi xem chi tiết — không đổi, code hiện tại đã đúng (dùng `currentUserService.UserId` thật).

## 5. Rủi ro / điều cần xác minh thêm khi viết plan

- Cần xác nhận `UserRole` enum / JWT role claim của hệ thống có giá trị "Staff" độc lập (đã có, dùng ở các UC Staff khác như UC-57/UC-58... trong các worktree khác) — không phải việc mới tạo role.
- Cần kiểm tra `CustomWebApplicationFactory.CreateAuthenticatedClient` (helper test) đã hỗ trợ tạo client với `UserRole.Staff` chưa, để viết integration test Staff-truy-cập-thành-công.

## 6. Ngoài phạm vi / known limitation

- Không giải quyết các mục C-01…C-24 khác trong Conflict Register SRS (RBAC tổng thể OD-01) — đây là một sửa cục bộ, đúng với UC-58/59 only, không đại diện cho toàn bộ quyết định RBAC chưa chốt ở OD-01.
