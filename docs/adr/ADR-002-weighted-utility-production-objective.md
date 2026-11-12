# ADR-002: WeightedUtility là hàm mục tiêu production của UC-10

- **Trạng thái:** Được developer chấp thuận (Accepted), sửa đổi ngày 2026-11-11 (mục 8). `Csp.Mode=WeightedUtility` được phát hành từ ngày 2026-11-11, shadow tắt.
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

1. **Shadow production** chạy ít nhất 7 ngày và ít nhất 500 lần (thay bằng kiểm chứng local, xem mục 8), với:
   - p95 thời gian shadow ≤ 300 ms;
   - tỷ lệ timeout < 1%;
   - không có lần chạy lỗi.
2. **Số điểm trung bình theo pace** nằm trong mục tiêu ở mục 2.3. Để đo được điều này, dòng log shadow phải ghi nhịp độ (pace).
3. **Bộ test đối chứng ở tầng handler** chạy với `WeightedUtility` và factory đã cấu hình xanh trong CI: sở thích, pace, rủi ro, người dùng chưa có hồ sơ, đổi user.
4. **Benchmark `WeightedUtility`:**
   - tính theo J, CSP không kém incumbent tham lam và không kém heuristic ở 100% kịch bản;
   - added p95 ≤ 300 ms (ở 40 ứng viên: ≤ 800 ms, xem mục 8);
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
| G1 | Test đối chứng end-to-end qua DI thật. Giữ fallback `?? new PersonalParamsFactory()`, vì test của ADR-001 §5.5.1 dùng nó; production được chặn bằng chính bộ test đối chứng. Ghi pace vào log shadow. Bỏ giá trị sở thích khỏi log khi parse lỗi. |
| G2 | Lưu và trả nguồn gốc lịch (migration `Itineraries`); trả mã lỗi riêng khi CSP hết giờ mà heuristic không có lịch, thay cho báo vô nghiệm; sửa comment `CspOptions` và tạo lại report benchmark |
| G3 | Web và Mobile: badge CSP có điều kiện, MSG35, hiển thị `detail` của lỗi 422 |
| G4 | Hiệu chỉnh tham số; lịch dự phòng cá nhân hóa; benchmark `WeightedUtility`; PR chuyển mode |
| G5 | Khoảng cách tới tối ưu; lý do chọn từng điểm theo sở thích |
| G6 | Sửa SRS BR-24/26 và MSG35; chạy lại báo cáo persona trên cấu hình phát hành |

## 8. Sửa đổi 2026-11-11

Report: `docs/benchmarks/UC-10-weighted-gate-2026-11-11-g4c.md` (tool `tools/benchmarks/weighted-gate`, cấu hình phát hành chỉ đổi `Csp:Mode`).

1. **Kiểm chứng local thay cho shadow production (gate 1).**
   - Production chưa có người dùng, nên 7 ngày shadow không thu được mẫu nào.
   - Thay bằng:
     - benchmark gate trên corpus c10–c40 × 5 persona;
     - bộ test đối chứng end-to-end (gate 3);
     - load test local với giới hạn CPU như Railway.
   - Ngưỡng rollback ở mục 5 vẫn áp dụng sau khi chuyển mode.
2. **Ngưỡng latency theo cỡ ứng viên (gate 4).**
   - p95 ≤ 300 ms dưới 40 ứng viên.
   - p95 ≤ 800 ms ở 40 ứng viên, bằng ngưỡng rollback.
   - Lý do:
     - Toàn bộ đuôi chậm ở c40 đến từ một dạng input: 40 ứng viên, 6 điểm bắt buộc, nghỉ thường xuyên (`grid-c40-m6-frequent`). Ở đó CSP mất khoảng 400 ms cho 50.000 nút.
     - Các kịch bản c40 khác đều ≤ 140 ms.
     - Giảm còn 25.000 nút đưa p95 c40 về 295 ms, nhưng J giảm ở 17/192 lượt (trung bình −7,5%, tệ nhất −40%), tức hạ chất lượng cho mọi người dùng.
   - Gate đo theo từng cỡ ứng viên, nên lượt nhanh ở cỡ nhỏ không che được đuôi chậm của c40.
3. **Hiệu chỉnh pace (mục 2.3).** `Personalization.Version` đổi thành `2026.3`.
   - `RhoStops` đổi từ 0,30 thành 0,60:
     - mọi persona Relaxed ra 3 điểm (trước đó `CultureEnthusiast` ra 5);
     - Moderate và Fast không đổi.
   - `Pace.Relaxed.LambdaPerMin` đổi từ 0,018 thành 0,013 (vẫn lớn hơn Moderate 0,012).
     - Trước đó, 3/4 persona Relaxed nhận cùng hai điểm gần nhất bất kể sở thích: Jaccard điểm tùy chọn trung bình 0,67.
     - Sau khi đổi, Jaccard là 0,11, mỗi persona nhận điểm theo sở thích, và vẫn 3 điểm.
   - Ghim bằng `ShippedPaceCalibrationTests`:
     - Relaxed nằm trong 2–4 điểm;
     - persona khác sở thích có lịch khác nhau (Jaccard trung bình < 0,4 ở mọi pace);
     - người chưa có hồ sơ được ít nhất 2 nhóm điểm;
     - Fast không ít điểm hơn Moderate.
4. **Luật Fast (quyết định sản phẩm 2026-11-11).**
   - Chọn Fast phải được nhiều điểm hơn Moderate, trừ khi ngày đã kín (lịch dùng ≥ 90% cửa sổ).
   - Đo trên chuyến 8 giờ Đà Nẵng: `ActiveExplorerFast` (461/480 phút) và `FoodLoverRelaxed` (449/480 phút) ra 5 điểm ở cả Moderate lẫn Fast vì không còn chỗ.
   - Ép thêm điểm (chi phí di chuyển gần 0) chỉ đổi các điểm ưa thích tốn thời gian lấy điểm ngắn ít ưa thích, nên không áp dụng.
5. **Kết quả gate khi sửa đổi:**
   - p95 c10/c20/c40 là 43/154/515 ms;
   - không lần nào chạm time limit;
   - theo J, lịch trả về không kém heuristic và greedy ở 192/192 lượt (đo bằng cùng `ObjectiveCalculator.Evaluate`);
   - số điểm trung bình Relaxed/Moderate/Fast là 3,0/5,0/6,0;
   - Jaccard điểm tùy chọn trung bình Relaxed/Moderate/Fast là 0,11/0,23/0,37.
   - Gate 6 được product owner của dự án duyệt ngày 2026-11-11, gồm số điểm theo pace và luật Fast.
6. **Load test local** (`docs/benchmarks/UC-10-weighted-load-2026-11-11-cpu8.md` và `-cpu2.md`).
   - Cách đo: sinh lịch đồng thời trên corpus c10–c40, server GC, giới hạn CPU bằng processor affinity.
   - **8 CPU (như Railway):**
     - `WeightedUtility` có throughput 64 req/s, so với 43 của `Lexicographic`;
     - ở 8 request đồng thời, p95 là 356 ms (Lexicographic 402 ms);
     - ở 16 request đồng thời, p95 là 461 ms (Lexicographic 706 ms);
     - không lần nào chạm time limit;
     - đuôi p99 cao hơn (1.218 ms ở 8 request đồng thời, Lexicographic 575 ms) và đến từ input c40 có nhiều điểm bắt buộc.
   - **2 CPU (quá tải có chủ đích):**
     - cả hai mode đều vượt ngưỡng p95;
     - `WeightedUtility` chạm time limit 5–6/360 lượt ở 8–16 request đồng thời, so với 28/360 của `Lexicographic` ở 16;
     - khi chạm time limit, người dùng vẫn nhận lịch tham lam cá nhân hóa (G4a), không lỗi.
   - Kết luận: chuyển mode không làm giảm sức chịu tải. Ngưỡng rollback ở mục 5 vẫn là chốt chặn.

## 9. G5 (2026-11-11): lý do từng điểm và mức mất do giới hạn nút

1. **Lý do từng điểm** (`PersonalizedVisitReasons`, chỉ ở `WeightedUtility`).
   - Lý do viết bằng tiếng Việt, lấy từ các thành phần của J:
     - điểm bắt buộc: "Điểm bạn đã chọn";
     - điểm khớp sở thích: "Hợp sở thích của bạn (category)";
     - hệ số giờ trong ngày ≥ 1,05: "Đi vào khung giờ đẹp";
     - chặng đi ≤ 15 phút: "Gần lộ trình";
     - còn lại: "Vừa thời gian chuyến đi".
   - Lý do được gán sau bước kiểm tra bất biến; `Lexicographic` giữ nguyên mã cũ.
   - Web và Mobile hiển thị nguyên chuỗi, nên không phải sửa client.
   - Giải thích lịch trình (AI và fallback) nhận ra "sở thích" và "gần" để chọn mẫu câu.
   - Bước giờ trong ngày (`TimeOfDayRefiner`) thay phần "Đi vào khung giờ đẹp" bằng câu cụ thể hơn, ví dụ "Xếp vào buổi sáng khi trời mát, ít đông". Các phần "Điểm bạn đã chọn", "Hợp sở thích", "Gần lộ trình" vẫn được giữ ở trước (`PersonalizedVisitReasons.WithTimeOfDay`).
2. **Không công bố khoảng cách tới tối ưu theo cận trên ở gốc.**
   - Trên corpus, cận này cho khoảng cách trung bình 80–108%, và 88% ngay cả khi không có điểm bắt buộc.
   - Khi có điểm bắt buộc, J thường âm. Vì vậy phần trăm so với cận này không có nghĩa với người dùng.
3. **Mức mất do giới hạn 50.000 nút** (`docs/benchmarks/UC-10-weighted-gate-2026-11-11-g5.md`, `--reference-nodes 1000000`).
   - Giải lại mọi request với 1.000.000 nút: J tăng ở 10/180 lượt.
   - Mức mất trung bình là 0% ở c10, 2,0% ở c20 và 0,8% ở c40.
   - Mức mất lớn nhất là 40% (một lượt c20) và 32% (một lượt c40).
   - Tăng node budget không đáng: c40 đã ở 515/800 ms.
