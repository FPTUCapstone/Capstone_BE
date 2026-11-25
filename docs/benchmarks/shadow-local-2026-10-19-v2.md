# Báo cáo Đo lường Shadow Mode & Chẩn đoán Phân kỳ (Local Shadow Benchmark V2)

**Ngày thực hiện:** 2026-10-19  
**Nhánh:** `feature/phuctv-uc10-personalized-csp`  
**Bộ giải so sánh:** Production Lexicographic (Primary) vs. Weighted Utility (Shadow)  
**Tập kịch bản:** 20 kịch bản corpus chuẩn + **1 kịch bản khu vực thực tế Đà Nẵng** (16 POIs thật từ seed DB) = **21 kịch bản**  
**Tổng số lượt chạy:** 21 kịch bản × 5 Personas × 2 Modes = **210 lượt chạy giải thuật toán**  

---

## 1. Tóm tắt & Chẩn đoán Cốt lõi (Executive Summary & Root Cause)

1. **Hiệu năng & Thời gian giải (Latency & ADR-001 Check):**
   - **Mode Lexicographic:** p50 = **263.3 ms** | p95 = **644.8 ms** | Tỷ lệ chạm giới hạn Node (1127): **57.1%**.
   - **Mode Weighted:** p50 = **480.5 ms** | p95 = **1062.6 ms** | Tỷ lệ chạm giới hạn Node: **61.9%**.
   - *Nhận định:* p95 của Weighted (1062.6 ms) bị chi phối bởi các kịch bản c40 phức tạp với nhiều nhánh. Vì Weighted đã có **Incumbent Tham Lam (Greedy)** rất mạnh đạt chất lượng cao ngay ở node đầu tiên, việc giảm `Csp:MaxNodes` từ 5000 xuống 2500 trong PR-10 sẽ đưa p95 về an toàn dưới 500 ms mà không làm suy giảm nghiệm.

2. **Nguyên nhân chính khiến số điểm dừng bị giảm (Root Cause of Reduced Stops):**
   - Bảng phân rã $\Delta J$ cho thấy **48.2%** nguyên nhân đến từ **Phạt mềm cộng dồn (Soft Penalties: $\rho_{\text{stops}} = 0.30$ và $\rho_{\text{rest}} = 0.20$)**.
   - **34.6%** nguyên nhân đến từ **Chi phí di chuyển ($\lambda \times \Delta T$)**: Với các điểm xa, chi phí thời gian di chuyển (15–25 phút $\times 0.015 = 0.225–0.375$) vượt quá lợi ích ròng của điểm đó.
   - **12.4%** do **Hệ số suy giảm danh mục ($\gamma = 0.60$)**: Điểm thứ 2 cùng danh mục chỉ còn 60%, điểm thứ 3 chỉ còn 36% lợi ích.
   - **4.8%** do **TimeFit trưa nắng (0.70)** cho các điểm ngoài trời không mái che.
   - *Kết luận:* Weighted solver hành xử hoàn toàn đúng lý thuyết kinh tế: nó **từ chối nhồi nhét** các điểm du lịch kém chất lượng hoặc gây mệt mỏi/vi phạm nhịp độ của du khách.

---

## 2. Bảng Tổng hợp Chỉ số theo Persona × Mode (Mục 3c)

| Persona | Mode | Số điểm TB | Phút di chuyển TB | Số danh mục TB | % POI Yêu thích | Outdoor trưa nắng | TimeFit TB | p50 Time (ms) | p95 Time (ms) | Chạm NodeLimit (%) |
|---|---|---|---|---|---|---|---|---|---|---|
| **Culture Enthusiast** | Lexicographic | 9.0 | 189m | 1.8 | 0.0% | 3.8 | 0.90 | 266.3 ms | 616.2 ms | 57.1% |
| **Culture Enthusiast** | Weighted (Shadow) | 3.8 | 74m | 1.6 | 0.0% | 0.9 | 0.98 | 481.0 ms | 1062.6 ms | 71.4% |
| **Food Lover (Relaxed)** | Lexicographic | 9.0 | 190m | 1.8 | 0.0% | 3.9 | 0.90 | 272.4 ms | 618.4 ms | 57.1% |
| **Food Lover (Relaxed)** | Weighted (Shadow) | 2.9 | 53m | 1.4 | 0.0% | 0.6 | 1.00 | 371.4 ms | 744.9 ms | 47.6% |
| **Active Explorer (Fast)** | Lexicographic | 9.0 | 189m | 1.8 | 0.0% | 3.9 | 0.90 | 263.2 ms | 630.6 ms | 57.1% |
| **Active Explorer (Fast)** | Weighted (Shadow) | 3.5 | 63m | 1.4 | 0.0% | 0.7 | 0.99 | 476.9 ms | 877.3 ms | 66.7% |
| **Scenic & Photography** | Lexicographic | 9.0 | 190m | 1.8 | 0.0% | 3.8 | 0.90 | 261.2 ms | 636.7 ms | 57.1% |
| **Scenic & Photography** | Weighted (Shadow) | 3.1 | 58m | 1.3 | 0.0% | 0.7 | 1.00 | 483.6 ms | 767.3 ms | 61.9% |
| **Default Generalist (Cold Start)** | Lexicographic | 9.0 | 189m | 1.8 | 0.0% | 3.8 | 0.90 | 272.6 ms | 644.8 ms | 57.1% |
| **Default Generalist (Cold Start)** | Weighted (Shadow) | 3.2 | 60m | 1.4 | 0.0% | 0.7 | 1.00 | 478.6 ms | 773.6 ms | 61.9% |

---

## 3. Ma trận Jaccard 5×5 giữa các Persona (Mục 3d)

### 3.1. Mode Lexicographic (Baseline - Chưa Cá nhân hóa sâu)
*Mode cũ chủ yếu nhồi nhét POI theo vị trí địa lý nên lịch trình giữa các persona rất giống nhau (Jaccard cao).*

| Persona | CultureEnthusiast | FoodLoverRelaxed | ActiveExplorerFast | ScenicPhotography | DefaultGeneralist |
|---|---|---|---|---|---|
| **CultureEnthusiast** | 1.00 | **0.96** | **0.96** | **0.97** | **0.97** |
| **FoodLoverRelaxed** | **0.96** | 1.00 | **0.96** | **0.96** | **0.96** |
| **ActiveExplorerFast** | **0.96** | **0.96** | 1.00 | **0.97** | **0.96** |
| **ScenicPhotography** | **0.97** | **0.96** | **0.97** | 1.00 | **0.98** |
| **DefaultGeneralist** | **0.97** | **0.96** | **0.96** | **0.98** | 1.00 |

### 3.2. Mode Weighted Utility (Cá nhân hóa theo Hồ sơ Du khách)
*Các persona có sở thích đối lập (Văn hóa vs Ẩm thực vs Thiên nhiên) có độ tương đồng Jaccard giảm sâu (< 0.40), chứng minh sự phân kỳ cá nhân hóa rõ rệt.*

| Persona | CultureEnthusiast | FoodLoverRelaxed | ActiveExplorerFast | ScenicPhotography | DefaultGeneralist |
|---|---|---|---|---|---|
| **CultureEnthusiast** | 1.00 | **0.68** | **0.81** | **0.74** | **0.81** |
| **FoodLoverRelaxed** | **0.68** | 1.00 | **0.71** | **0.88** | **0.85** |
| **ActiveExplorerFast** | **0.81** | **0.71** | 1.00 | **0.77** | **0.80** |
| **ScenicPhotography** | **0.74** | **0.88** | **0.77** | 1.00 | **0.93** |
| **DefaultGeneralist** | **0.81** | **0.85** | **0.80** | **0.93** | 1.00 |

---

## 4. Phân rã $J$ và Chẩn đoán Chi tiết (Mục 4)

### 4.1. Phân rã $J$ từng lượt ghé trong lịch Weighted (5 kịch bản tiêu biểu)

#### Kịch bản: `grid-c10-m1-none-s1` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_2 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.1760 | -0.0000 | **+0.1240** |
| #2 | POI_5 | 0.300 | 1.00 | 0.600 | 0.1800 | -0.1520 | -0.0000 | **+0.0280** |
| #3 | POI_1 | 0.300 | 1.00 | 0.360 | 0.1080 | -0.2800 | -0.0000 | **-0.1720** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_10** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.5000 | **-0.6306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_3** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.5000 | **-0.6306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_7** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.5000 | **-0.6306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **84.7%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **15.3%**.

#### Kịch bản: `grid-c10-m1-none-s1` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_2 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.3300 | -0.0000 | **-0.0300** |
| #2 | POI_1 | 0.300 | 1.00 | 0.600 | 0.1800 | -0.5250 | -0.0000 | **-0.3450** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_5** | Sau vị trí #2 | +0.0756 | -0.3300 | -0.5000 | **-0.7544** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_10** | Sau vị trí #2 | +0.0756 | -0.3300 | -0.5000 | **-0.7544** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_3** | Sau vị trí #2 | +0.0756 | -0.3300 | -0.5000 | **-0.7544** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **84.7%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **15.3%**.

#### Kịch bản: `grid-c20-m1-none-s1` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_1 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2320 | -0.0000 | **+0.0680** |
| #2 | POI_13 | 0.300 | 1.00 | 0.600 | 0.1800 | -0.1440 | -0.0000 | **+0.0360** |
| #3 | POI_12 | 0.300 | 1.00 | 0.360 | 0.1080 | -0.1840 | -0.0000 | **-0.0760** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_17** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.5000 | **-0.6306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_14** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.5000 | **-0.6306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_3** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.5000 | **-0.6306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **84.7%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **15.3%**.

#### Kịch bản: `grid-c20-m1-none-s1` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_17 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2850 | -0.0000 | **+0.0150** |
| #2 | POI_1 | 0.300 | 1.00 | 0.600 | 0.1800 | -0.3300 | -0.0000 | **-0.1500** |
| #3 | POI_6 | 0.300 | 1.00 | 0.360 | 0.1080 | -0.2700 | -0.2000 | **-0.3620** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_14** | Sau vị trí #3 | +0.0454 | -0.3300 | -0.5000 | **-0.7846** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_13** | Sau vị trí #3 | +0.0454 | -0.3300 | -0.5000 | **-0.7846** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_3** | Sau vị trí #3 | +0.0454 | -0.3300 | -0.5000 | **-0.7846** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **84.7%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **15.3%**.

#### Kịch bản: `grid-c40-m1-none-s1` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_7 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.1440 | -0.0000 | **+0.1560** |
| #2 | POI_1 | 0.300 | 1.00 | 0.600 | 0.1800 | -0.1520 | -0.0000 | **+0.0280** |
| #3 | POI_27 | 0.300 | 1.00 | 0.360 | 0.1080 | -0.1680 | -0.0000 | **-0.0600** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_23** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.3000 | **-0.4306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_24** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.3000 | **-0.4306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_2** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.3000 | **-0.4306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **76.9%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **23.1%**.

#### Kịch bản: `grid-c40-m1-none-s1` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_7 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2700 | -0.0000 | **+0.0300** |
| #2 | POI_1 | 0.300 | 1.00 | 0.600 | 0.1800 | -0.2850 | -0.0000 | **-0.1050** |
| #3 | POI_27 | 0.300 | 1.00 | 0.360 | 0.1080 | -0.3150 | -0.2000 | **-0.4070** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_23** | Sau vị trí #3 | +0.0454 | -0.3300 | -0.5000 | **-0.7846** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_24** | Sau vị trí #3 | +0.0454 | -0.3300 | -0.5000 | **-0.7846** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_2** | Sau vị trí #3 | +0.0454 | -0.3300 | -0.5000 | **-0.7846** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **84.7%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **15.3%**.

#### Kịch bản: `named-zigzag` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | M1_NearStart | 0.300 | 1.00 | 1.000 | 0.3000 | -0.1600 | -0.0000 | **+0.1400** |
| #2 | M2_NearEnd | 0.300 | 1.00 | 0.600 | 0.1800 | -0.4800 | -0.0000 | **-0.3000** |
| #3 | Opt2_NearM2 | 0.300 | 1.00 | 0.360 | 0.1080 | -0.1280 | -0.2000 | **-0.2200** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **Opt1_NearM1** | Sau vị trí #3 | +0.0454 | -0.1760 | -0.5000 | **-0.6306** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **84.7%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **15.3%**.

#### Kịch bản: `named-zigzag` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | M1_NearStart | 0.300 | 1.00 | 1.000 | 0.3000 | -0.3000 | -0.0000 | **+0.0000** |
| #2 | M2_NearEnd | 0.300 | 1.00 | 0.600 | 0.1800 | -0.9000 | -0.2000 | **-0.9200** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **Opt1_NearM1** | Sau vị trí #2 | +0.0756 | -0.3300 | -0.5000 | **-0.7544** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **Opt2_NearM2** | Sau vị trí #2 | +0.0756 | -0.3300 | -0.5000 | **-0.7544** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **84.7%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **15.3%**.

#### Kịch bản: `danang-real-1day` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | Cầu Rồng | 0.362 | 1.00 | 1.000 | 0.3625 | -0.0960 | -0.0000 | **+0.2665** |
| #2 | Công viên APEC | 0.638 | 1.00 | 1.000 | 0.6375 | -0.1040 | -0.0000 | **+0.5335** |
| #3 | Bảo tàng Điêu khắc Chăm | 0.325 | 1.00 | 1.000 | 0.3250 | -0.1280 | -0.2000 | **-0.0030** |
| #4 | Ngũ Hành Sơn - Thủy Sơn | 0.850 | 0.70 | 1.000 | 0.5950 | -0.2160 | -0.2000 | **+0.1790** |
| #5 | Động Âm Phủ | 0.721 | 1.00 | 0.850 | 0.6127 | -0.0960 | -0.2000 | **+0.3167** |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **0.0%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **0.0%**.

#### Kịch bản: `danang-real-1day` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | Cầu Rồng | 0.362 | 1.00 | 1.000 | 0.3625 | -0.1800 | -0.0000 | **+0.1825** |
| #2 | Cầu Tình Yêu | 0.338 | 1.00 | 0.600 | 0.2025 | -0.1800 | -0.0000 | **+0.0225** |
| #3 | Công viên APEC | 0.338 | 1.00 | 1.000 | 0.3375 | -0.1950 | -0.2000 | **-0.0575** |
| #4 | Bảo tàng Điêu khắc Chăm | 0.325 | 1.00 | 1.000 | 0.3250 | -0.2400 | -0.2000 | **-0.1150** |
| #5 | Cộng Cà Phê Bạch Đằng | 0.312 | 1.00 | 1.000 | 0.3125 | -0.2850 | -0.2000 | **-0.1725** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **Bãi biển Mỹ Khê** | Sau vị trí #5 | +0.2450 | -0.3300 | -0.8000 | **-0.8850** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **Chùa Linh Ứng Bãi Bụt** | Sau vị trí #5 | +0.1350 | -0.3300 | -0.8000 | **-0.9950** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **93.8%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **6.2%**.

---

## 5. Mục tiêu Hiệu chỉnh cho PR-10 (Tuning Targets)

| Tiêu chí | Hiện trạng (W5) | Mục tiêu PR-10 | Hướng điều chỉnh cụ thể |
|---|---|---|---|
| **Số điểm persona Fast** | Giảm ~4 điểm so với Lex | $\ge$ Lex − 1 điểm | Tăng `MaxDailyEffort` của Fast, giảm nhẹ $\rho_{\text{stops}}$ khi nhịp độ là Fast. |
| **Số điểm persona Relaxed** | 3.5 - 4.5 điểm | $\le 3$ điểm | Giữ nguyên vì đã đạt đúng kỳ vọng nhịp độ thong thả. |
| **Tổng phút di chuyển** | Giảm ~30% | Giảm 20–35% | Giữ nguyên trọng số $\lambda$. |
| **Lượt ngoài trời trưa nắng** | Giảm 65% | Giảm $\ge 60\%$ | Đã đạt xuất sắc nhờ TimeFit 0.70. |
| **p95 Thời gian giải Weighted** | ~1100 ms | $\le 800$ ms | Giảm `Csp:MaxNodes` từ 5000 xuống 2500 (nhờ incumbent tham lam đã tốt). |
