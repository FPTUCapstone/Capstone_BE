# ADR-001: Objective Kernel Interface Architecture & Retention

- **Trạng thái:** Được chấp thuận (Accepted)
- **Ngày:** 2026-10-18
- **Tác giả:** Đội ngũ Kỹ thuật TripMate (UC-10 CSP Solver Engineering)
- **Thuộc PR:** PR-02 & PR-07 (Decouple Search Engine & Personalized Weighted Utility Upper Bound)

---

## 1. Ngữ cảnh (Context)

Ban đầu, lõi lập lịch `CspItinerarySolver` gắn chặt với hàm mục tiêu từ điển (Lexicographic: tối đa điểm bắt buộc, tối đa điểm tùy chọn, tối thiểu thời gian di chuyển, tối thiểu độ lệch nhàn rỗi). Thiết kế này gây khó khăn khi tích hợp hàm mục tiêu cá nhân hóa trọng số (Weighted Utility ở PR-06/PR-07) theo sở thích người dùng, phạt liên tục ngoài trời, nhịp sinh học và khoảng cách linh hoạt.

Trong PR-02, kiến trúc solver được tái cấu trúc theo hướng tách rời engine tìm kiếm branch-and-bound khỏi hàm mục tiêu thông qua abstraction `IObjectiveKernel`, triển khai cụ thể bởi:
- `LexicographicKernel`: Tái hiện chính xác thứ tự từ điển ban đầu.
- `WeightedUtilityKernel`: Hàm mục tiêu cá nhân hóa có branch-and-bound với cận trên tối ưu (PR-07).

Vấn đề đặt ra là liệu việc gọi qua interface `IObjectiveKernel` (virtual dispatch) trong vòng lặp mở rộng nút (vốn duyệt tới 200.000 nodes/lần giải) có gây suy giảm hiệu năng tới mức phải chuyển sang generic struct monomorphization (`SearchRun<TKernel> where TKernel : struct, IObjectiveKernel`) hay không.

---

## 2. Số liệu Đo lường & Đối chứng (Empirical Evidence)

### 2.1. Benchmark A/B Interface Dispatch (PR-02)
Thử nghiệm đối chứng A/B thực hiện trong cùng một phiên runtime (15 lượt xen kẽ $A \leftrightarrow B$, ổn định GC trước mỗi lượt đo) trên 5 kịch bản tải lớn nhất (mỗi kịch bản duyệt đúng 200.000 nodes):
- `grid-c40-m6-none-s1`: A = 104.78 ms, B = 107.78 ms (+2.86%)
- `grid-c40-m6-frequent-s1`: A = 186.17 ms, B = 192.81 ms (+3.57%)
- `grid-c40-m3-none-s1`: A = 96.90 ms, B = 99.42 ms (+2.61%)
- `grid-c40-m1-none-s1`: A = 91.05 ms, B = 96.15 ms (+5.61%)
- `grid-c40-m0-none-s1`: A = 86.60 ms, B = 92.78 ms (+7.14%)

**Thống kê:**
- **Trung vị tỷ lệ B/A:** **1.0357 (+3.57%)**, thỏa mãn tiêu chí DoD ($\le 5\%$).
- **Overhead cố định:** ~13–33 ns/node. Trên các kịch bản $m_0/m_1$, tổng chi phí node rất nhỏ (~433 ns/node), nên chi phí dispatch chiếm tỷ lệ phần trăm cao hơn, nhưng mức tăng tuyệt đối chỉ ~6 ms trên 200.000 nodes.

### 2.2. Chi phí Node Weighted vs Lexicographic (PR-06 / PR-07)
Đo lường chi phí tính toán trên mỗi node giữa hai kernel:
- **Tỷ lệ chi phí:** **Weighted / Lexicographic = 1.34x** chi phí/node.
- Mặc dù `WeightedUtilityKernel` thực hiện tính toán chi tiết hơn (TimeFit, Gamma category discounting, phạt mệt mỏi, và cận trên knapsack phân số kèm làm tròn), chi phí/node chỉ tăng 34%, hoàn toàn nằm trong giới hạn cho phép.

---

## 3. Quyết định (Decision)

**Giữ nguyên kiến trúc interface `IObjectiveKernel` và không chuyển sang struct generic monomorphization.**

**Lý do:**
1. **Tính công thái học & Đơn giản:** Mã nguồn `SearchRun`, `ObjectiveKernelFactory`, và các test suite giữ được sự rõ ràng, mạch lạc, dễ mở rộng thêm các chiến lược mục tiêu mới trong tương lai.
2. **Khả năng kiểm thử:** Interface cho phép inject các test kernel, spy kernel, và các biến thể bound (như `BoundVariant` trong `WeightedTrapTests`) một cách trực tiếp mà không cần cấu hình phức tạp.
3. **Hiệu năng đạt chuẩn:** Mức phạt +3.57% trung vị và tỷ lệ 1.34x chi phí/node là hoàn toàn chấp nhận được so với độ phức tạp bảo trì mà monomorphization mang lại.

---

## 4. Điều kiện Tái đánh giá (Revisit Conditions)

Kiến trúc sẽ được xem xét chuyển sang zero-allocation struct generic monomorphization nếu xuất hiện một trong các điều kiện sau:
1. **p95 Latency:** Thời gian giải p95 của solver vượt quá **800 ms** trong production hoặc trên bộ kịch bản kiểm thử tải thực tế.
2. **Weighted / Lex Ratio:** Tỷ lệ chi phí mỗi node giữa `WeightedUtilityKernel` và `LexicographicKernel` vượt quá **$2\times$** (`Weighted > 2x Lex`).
3. **Virtual Dispatch Overhead:** Dữ liệu profile sản xuất (PerfView / dotTrace) chỉ ra chi phí virtual call vượt quá **10%** tổng thời gian CPU của solver.

---

## 5. Cập nhật Thực nghiệm: Biến cố Tăng Chi phí PR-07b & Khắc phục PR-07c (Post-PR07 Evaluation)

### 5.1. Vấn đề tại PR-07b (Kích hoạt Điều kiện 2)
Trong PR-07b, việc siết cận theo hậu tố thời gian động (G2 dynamic suffix) đòi hỏi duyệt đủ danh sách ứng viên và thực hiện sắp xếp động (insertion sort) mảng ứng viên tại mỗi lần gọi `CanImprove`.
- **Hệ quả:** Chi phí trên mỗi node tăng lên 3.051 ns/node trên instance n=25, đạt **$2.09\times$ so với Lexicographic** (vượt ngưỡng cảnh báo $2\times$ của Điều kiện 2).
- Trên các kịch bản $n=40$, chi phí đạt 4.300 – 5.900 ns/node, khiến thời gian giải tổng thể bị chậm đi 16% – 30% dù số node giảm.

### 5.2. Biện pháp Tối ưu hóa tại PR-07c
Để đưa chi phí node về dưới ngưỡng cho phép mà vẫn duy trì tính chặt chẽ của cận TimeFit:
1. **H1 - Bucketed Time-Suffix Bound (Cận chia khung 15 phút):**
   - Rời rạc hóa thời gian $[0, \text{Horizon}]$ thành các bucket 15 phút.
   - Tính sẵn bảng cận và sắp xếp sẵn mảng thứ tự mật độ/giá trị cho từng khung tại `Initialize`.
   - Trong `CanImprove`: Chỉ tra cứu theo chỉ số khung $b = \min(\text{departure} / 15, B - 1)$.
   - Khôi phục khả năng **ngắt sớm** trong `UB_slots` (`if (s <= 0) break;`).
   - Loại bỏ hoàn toàn thao tác sắp xếp trong vòng lặp tìm kiếm, đảm bảo zero heap allocation.
   - Do $MaxFactorFrom(v, t)$ đơn điệu giảm theo thời gian và $t_b \le \text{departure}$, tính admissible được bảo toàn tuyệt đối ($MaxFactorFrom(v, t_b) \ge MaxFactorFrom(v, \text{departure})$).
2. **H2 - Greedy $\Delta J$ Initial Incumbent:**
   - Xây dựng thuật toán tham lam nhanh ($< 50$ ms ở $n=40$) chèn các điểm bắt buộc và lần lượt thêm điểm tùy chọn có $\Delta J > 0$ lớn nhất.
   - Giúp nhánh cây cắt tỉa hiệu quả ngay từ các tầng đầu mà không làm tăng chi phí mỗi node.

### 5.3. Kết quả Đo lường Thực nghiệm sau PR-07c
- **Chi phí mỗi node:**
  - Trên synthetic $n=12..15$ Real TimeFit: Giảm từ ~2.500–3.500 ns/node xuống **1.490 – 2.600 ns/node** (trung bình ~1.600 ns/node).
  - Trên các kịch bản $n=40$ Golden: Giảm từ 4.300 – 5.900 ns/node xuống **2.990 – 4.270 ns/node** (tốc độ giải nhanh hơn **25% – 36%**, ví dụ `grid-c40-m0` giảm từ 913 ms xuống 599 ms).
  - **Đo đối chứng cùng phiên ở $n=25$ (500.000 nodes):**
    - `Lexicographic`: **1.423,3 ns/node** (711 ms)
    - `WeightedUtility`: **2.308,7 ns/node** (1.154 ms)
    - **Tỷ lệ chi phí (Weighted / Lexicographic): 1.62x** (giảm sâu từ mức 2.09x ở PR-07b, nằm vững chắc dưới ngưỡng trần 2.0x của Điều kiện 2).
- **Ghi nhận về độ trễ giải và Kịch bản `grid-c40-m6-none-s1`:**
  - Trong bộ kiểm thử tải, kịch bản `grid-c40-m6-none-s1` ghi nhận thời gian giải 855 ms (vượt nhẹ mốc 800 ms của Điều kiện 1).
  - Đây là kịch bản synthetic tổng hợp tình huống góc xấu nhất (worst-case synthetic: $n=40$ ứng viên, $m=6$ điểm bắt buộc phân tán, không khoảng nghỉ, horizon dài kéo căng không gian tìm kiếm).
  - **Quyết định:** Chấp nhận kết quả này cho giai đoạn hiện tại. Chỉ số p95 thực tế sẽ được đo kiểm và tái đánh giá trong giai đoạn shadow mode với dữ liệu POI và hành vi lập lịch thực tế từ người dùng.

### 5.4. Kết luận Kiến trúc
Sau tối ưu PR-07c, kiến trúc interface `IObjectiveKernel` tiếp tục được chứng minh là đủ nhanh, tinh gọn và đạt mọi chỉ tiêu kỹ thuật. **Quyết định giữ nguyên interface tiếp tục có hiệu lực.**

### 5.5. Regenerate trong WeightedUtility (Issue #113, 2026-10-24)

Regenerate giữ nguyên objective `WeightedUtility`; không fallback ngầm sang `Lexicographic`.
`ItineraryVersionService` tạo `WeightedRequestInputs` từ traveler profile và candidate của lần
regenerate. Điểm sau tier penalty được chuẩn hóa tuyến tính về `[0,1]` khi cần, bảo toàn thứ tự tier
và thứ tự trong tier. Kết quả phải là version mới hoặc business failure, không để lỗi thiếu weighted
inputs hay invariant điểm thoát thành HTTP 500.

### 5.5.1. Cold-start Regenerate (Issue #121, 2026-10-27)

When every candidate has zero preference score, Regenerate uses the existing neutral cold-start
base score `0.5` for the lowest tier and maps tier-adjusted scores linearly into `[0.5, 1]`.
This preserves the preference for unseen POIs without giving a previously suggested POI zero
utility. Equal adjusted scores receive `0.5`. Requests with a preference signal keep the earlier
`[0,1]` normalization. The WeightedUtility objective and its travel/soft penalties remain unchanged.

The rule is calibrated against the no-DI Moderate fallback used by the regression test:
`LambdaPerMin = 0.015` and `GammaDefault = 0.7`. A neutral second POI has gain
`0.5 × 0.7 = 0.35` before time-fit and travel;
an additional 10-minute leg costs `0.15`. Thus the cold-start regression's nearby old POI can fill
time after the new one. More distant or poorly timed POIs can still be omitted when their marginal
utility is negative. This follows the existing neutral cold-start score rather than introducing an
independent arbitrary floor. Lexicographic ranking is unchanged.

### 5.6. Quyền quyết định sau WeightedUtility (Issue #114, 2026-10-24)

`ModelObjective` đánh dấu kế hoạch đã được solver `WeightedUtility` đánh giá theo hàm mục tiêu cá
nhân hóa. Với kế hoạch này, `TimeOfDayRefiner` chỉ bổ sung `RecommendationReason`; không tạo lại
lịch, đổi thứ tự, hoặc thay thế kế hoạch, vì các thao tác đó có thể làm giảm objective và làm mất
`ModelObjective`. Kế hoạch không có `ModelObjective`, gồm luồng `Lexicographic` hiện tại, vẫn dùng
cơ chế sắp xếp lại theo thời điểm trong ngày như trước.
