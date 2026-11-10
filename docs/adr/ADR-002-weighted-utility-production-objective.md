# ADR-002: WeightedUtility là hàm mục tiêu production của UC-10

- **Trạng thái:** Được developer chấp thuận (Accepted). Rollout theo giai đoạn; `Csp.Mode` chưa được chuyển.
- **Ngày:** 2026-11-10
- **Tác giả:** UC-10 scheduling (phuctv)
- **Liên quan:** [ADR-001](ADR-001-objective-kernel-interface.md),
  `specs/UC-10-csp-itinerary-solver-spec.md`, audit UC-10 ngày 2026-11-10 (`origin/develop` `f6cd480e`)

---

## 1. Ngữ cảnh

Audit read-only UC-10 trên `origin/develop` `f6cd480e` có các kết luận sau.

- **Cá nhân hóa ở cấu hình phát hành chỉ một chiều.** `appsettings.json` đặt `SolverMode=Csp` và `Csp.Mode=Lexicographic`.
  - Chỉ `InterestTagsJson` và hành vi like/dislike/skip ảnh hưởng lịch. Chúng ảnh hưởng qua: pool top-60, matrix top-40, và hạng optional trong product rule.
  - `TravelPace` và `RiskTolerance` chỉ được đọc trong `PersonalParamsFactory`, tức nhánh `WeightedUtility`.
  - Scheduling không đọc `FoodPreferencesJson` và `PreferredTransportMode`.
  - Food & Drink không thuộc nhóm auto-schedulable (`PlanningPoiEligibility.IsAutoSchedulable`).
- **Probe đối chứng ở handler** (8 POI, cùng ma trận, cấu hình phát hành):
  - culture lover và nature lover ra lịch khác nhau;
  - Relaxed và Fast giống nhau (cả hai 5 điểm khi cửa sổ 480 phút);
  - risk Low và High giống nhau;
  - food lover giống người dùng chưa có hồ sơ.
- **Cùng probe với `WeightedUtility`** và bảng Pace của `appsettings.json`:
  - ở 480 phút, Relaxed ra 2 điểm, Fast ra 3 điểm;
  - người dùng chưa có hồ sơ nhận lịch đa dạng nhóm hơn.
- **Báo cáo persona 2026-10-21 không khớp cấu hình phát hành.** Báo cáo đo trên `WeightedUtility` với AI ranking `Success`, trong khi cấu hình phát hành là `Lexicographic` với `AiRanking.Enabled=false`.
- **SRS hứa nhiều hơn.** BR-24 và BR-26 mô tả 5 loại preference là soft constraint của CSP Engine: phương tiện, nhịp độ, ẩm thực, sở thích và rủi ro.
- **Shadow benchmark v3b** (máy dev, 210 case):

  | Mode | Số điểm trung bình | Phút di chuyển | p95 |
  |---|---:|---:|---:|
  | `WeightedUtility` | 3,96 | 65 | 147 ms |
  | `Lexicographic` | 8,94 | 117 | 172 ms |

## 2. Quyết định

1. **`WeightedUtility` trở thành hàm mục tiêu production của UC-10**, cho cả tạo mới lẫn regenerate.
2. **Rollout shadow-first.**
   - ADR này bật `SchedulingGeneration:Shadow` với `Enabled=true` và `SampleRate=0.2`. Shadow đã chạy `WeightedUtility` ở nền khi mode chính là `Lexicographic`, nên bước này chỉ là cấu hình.
   - `Csp.Mode` được đổi trong một PR cấu hình riêng, chỉ khi mọi gate ở mục 4 đạt.
3. **Mục tiêu số điểm cho chuyến 8 giờ.** Số điểm phải tăng dần theo pace. Đạt mục tiêu bằng cách hiệu chỉnh `LambdaPerMin`, `RhoStops` và `MaxStopsPerDay`.

   | Pace | Số điểm |
   |---|---|
   | Relaxed | 3 ± 1 |
   | Moderate | 4–6 |
   | Fast | 6–8 |

4. **Ẩm thực và phương tiện.**
   - Đợt này không đưa quán ăn vào lịch tự động.
   - SRS BR-24/26 được sửa: `FoodPreferences` áp dụng khi traveler tự thêm quán ăn.
   - `PreferredTransportMode` là giá trị mặc định được điền sẵn vào request.
5. **Điều kiện tiên quyết trước khi chuyển mode.**
   - Nguồn gốc lịch (solver outcome, đã duyệt hết cây hay chưa, có bị sắp lại theo giờ trong ngày không) phải được lưu và hiển thị trung thực trên Web và Mobile.
   - Lịch dự phòng ở `WeightedUtility` phải được cá nhân hóa, tức dùng `GreedyWeightedIncumbent` thay cho lịch heuristic theo product rule.

## 3. Các lựa chọn đã cân nhắc

- **Giữ `Lexicographic` và chuyển pace thành `MaxStops` theo từng request.** Bị loại, vì:
  - pace trở thành ràng buộc cứng, trái với BR-26 (soft constraint);
  - risk và time-fit vẫn không được dùng;
  - bỏ phí `WeightedUtilityKernel` đã có benchmark và golden test.
- **Chuyển thẳng, không qua shadow.** Bị loại, vì chưa có số liệu production về độ trễ, tỷ lệ chạm time limit và số điểm theo pace.

## 4. Gate chuyển `Csp.Mode` (tất cả phải đạt)

1. **Shadow production** chạy ít nhất 7 ngày và ít nhất 500 lần, với:
   - p95 thời gian shadow ≤ 300 ms;
   - tỷ lệ timeout < 1%;
   - không có lần chạy lỗi.
2. **Số điểm trung bình theo pace** nằm trong mục tiêu ở mục 2.3. Để đo được điều này, dòng log shadow phải ghi nhịp độ (pace).
3. **Bộ test đối chứng ở tầng handler** chạy với `WeightedUtility` và factory đã cấu hình xanh trong CI: sở thích, pace, rủi ro, người dùng chưa có hồ sơ, đổi user.
4. **Benchmark `WeightedUtility`:**
   - tính theo J, CSP không kém incumbent tham lam và không kém heuristic ở 100% kịch bản;
   - added p95 ≤ 300 ms;
   - không lần nào chạm time limit;
   - report có p99 và tỷ lệ chạm node limit.
5. **Nguồn gốc lịch** (điều kiện tiên quyết ở mục 2.5) đã được phát hành trên BE, Web và Mobile.
6. **Product owner** duyệt số điểm.

## 5. Rollback

- Shadow: `SchedulingGeneration__Shadow__Enabled=false`.
- Sau khi chuyển mode: `SchedulingGeneration__Csp__Mode=Lexicographic`.
- Ngưỡng kích hoạt rollback (dùng chung với ADR-001 mục 4): outcome dự phòng vượt 1% trong một ngày, hoặc p95 solver vượt 800 ms.

## 6. Hệ quả

- **Lịch thưa hơn hiện tại**, đúng chủ ý: số điểm phụ thuộc nhịp độ.
- **Time-of-day nằm trong J.** Vì lịch có `ModelObjective`, `TimeOfDayRefiner` không còn sắp lại thứ tự lịch của solver.
- **Thước đo "không kém heuristic" đổi sang J.** Report `solver-comparison` (đo theo product rule) không còn là gate của mode phát hành.
- **Shadow đảo vai sau khi chuyển.** Shadow chỉ chạy khi mode chính là `Lexicographic`. Test `Post_WhenShadowModeEnabled_ProcessesPrimaryRequestAndInvokesShadowRunner` phải đặt `Lexicographic` tường minh.
- **Chưa chứng minh tối ưu ở cỡ lớn.** Với 40 ứng viên và 50.000 nút, `WeightedUtility` có thể không duyệt hết cây, nên cần báo khoảng cách tới tối ưu.
- **Chi phí shadow trên production:**
  - hàng đợi giới hạn 100 việc, đầy thì bỏ việc cũ nhất (`DropOldest`);
  - một luồng đọc, timeout 5 s;
  - dùng lại ma trận khoảng cách đã có, không gọi thêm Mapbox.

## 7. Giai đoạn

| Giai đoạn | Nội dung |
|---|---|
| G0 | ADR này; bật shadow 20%; ghim `Csp.Mode=Lexicographic` trong test cấu hình phát hành cho đến khi đạt gate |
| G1 | Test đối chứng ở handler; gỡ bốn chỗ `?? new PersonalParamsFactory()` (bảng Pace rỗng làm pace bị bỏ qua mà không báo); ghi pace vào log shadow |
| G2 | Lưu và trả nguồn gốc lịch (migration `Itineraries`); trả mã lỗi riêng khi CSP hết giờ mà heuristic không có lịch, thay cho báo vô nghiệm |
| G3 | Web và Mobile: badge CSP có điều kiện, MSG35, hiển thị `detail` của lỗi 422 |
| G4 | Hiệu chỉnh tham số; lịch dự phòng cá nhân hóa; benchmark `WeightedUtility`; PR chuyển mode |
| G5 | Khoảng cách tới tối ưu; lý do chọn từng điểm theo sở thích |
| G6 | Sửa SRS BR-24/26 và MSG35; chạy lại báo cáo persona trên cấu hình phát hành |
