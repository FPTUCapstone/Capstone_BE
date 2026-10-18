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
