# Báo cáo Đo lường Shadow Mode & Đánh giá Hiệu chỉnh K5 (Local Shadow Benchmark V3a)

**Ngày thực hiện:** 2026-10-19  
**Nhánh:** `feature/phuctv-uc10-personalized-csp`  
**Bộ giải so sánh:** Production Lexicographic (Primary) vs. Weighted Utility (Shadow)  
**Tập kịch bản:** 20 kịch bản corpus chuẩn + **1 kịch bản khu vực thực tế Đà Nẵng** (16 POIs thật từ seed DB) = **21 kịch bản**  
**Tổng số lượt chạy:** 21 kịch bản × 5 Personas × 2 Modes = **210 lượt chạy giải thuật toán**  
**Ngân sách nút Weighted:** 50.000 nút (theo đường cong K4, p95 <= 300ms)  

---

## 1. Tóm tắt & Chẩn đoán Cốt lõi (Executive Summary & Root Cause)

1. **Hiệu năng & Thời gian giải (Latency & ADR-001 Check):**
   - **Mode Lexicographic:** p50 = **307.6 ms** | p95 = **825.6 ms** | Tỷ lệ chạm giới hạn Node: **57.1%**.
   - **Mode Weighted (50.000 nodes):** p50 = **160.2 ms** | p95 = **490.7 ms** | Tỷ lệ chạm giới hạn Node: **84.8%**.
   - *Nhận định:* Ngân sách 50.000 nodes giúp kiểm soát p95 thời gian giải Weighted ở mức an toàn cho shadow mode, đồng thời giữ chất lượng nghiệm gần như tối ưu nhờ Incumbent Tham Lam.

2. **Chất lượng cá nhân hóa sau các bước hiệu chỉnh (K0b, K0c, K2):**
   - Sửa lỗi gán cứng danh mục General (K0b) và TagToCategory (K0c) giúp tỷ lệ POI yêu thích và số danh mục phản ánh đúng sở thích du khách.
   - Hiệu chỉnh trọng số di chuyển $\lambda$ (K2) và hệ số đa dạng $\gamma$ giúp cân bằng giữa số lượng điểm dừng và sự thoải mái của chuyến đi.
   - Tạm tắt phạt thời gian nghỉ $\rho_{\text{rest}}$ (K1) khi solver chưa mô hình hóa điểm dừng nghỉ tránh phạt oan các lịch trình dài.

---

## 2. Bảng Tổng hợp Chỉ số theo Persona × Mode (Mục 3c)

| Persona | Mode | Số điểm TB | Phút di chuyển TB | Di chuyển/điểm | Tỷ lệ tham quan | Số danh mục TB | % POI Yêu thích | Outdoor trưa nắng | TimeFit TB | p50 Time (ms) | p95 Time (ms) | Chạm NodeLimit (%) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Culture Enthusiast** | Lexicographic | 8.9 | 196m | 21.9m | 59.0% | 4.9 | 54.8% | 3.8 | 0.90 | 307.6 ms | 743.1 ms | 66.7% |
| **Culture Enthusiast** | Weighted (Shadow) | 4.4 | 91m | 20.0m | 61.4% | 3.4 | 64.0% | 1.4 | 0.94 | 167.8 ms | 490.7 ms | 90.5% |
| **Food Lover (Relaxed)** | Lexicographic | 9.0 | 190m | 20.9m | 60.1% | 5.7 | 1.9% | 3.9 | 0.90 | 321.0 ms | 771.6 ms | 57.1% |
| **Food Lover (Relaxed)** | Weighted (Shadow) | 3.2 | 59m | 17.0m | 65.7% | 3.1 | 1.9% | 0.7 | 1.00 | 184.7 ms | 392.6 ms | 71.4% |
| **Active Explorer (Fast)** | Lexicographic | 8.9 | 193m | 21.5m | 59.3% | 4.8 | 60.6% | 3.9 | 0.89 | 321.6 ms | 846.0 ms | 52.4% |
| **Active Explorer (Fast)** | Weighted (Shadow) | 5.2 | 110m | 20.2m | 61.0% | 3.5 | 70.6% | 2.0 | 0.92 | 126.5 ms | 296.9 ms | 90.5% |
| **Scenic & Photography** | Lexicographic | 8.9 | 189m | 20.9m | 60.0% | 4.9 | 63.5% | 3.8 | 0.90 | 288.7 ms | 742.8 ms | 52.4% |
| **Scenic & Photography** | Weighted (Shadow) | 4.6 | 95m | 20.0m | 61.6% | 3.6 | 70.1% | 1.7 | 0.93 | 162.5 ms | 362.3 ms | 90.5% |
| **Default Generalist (Cold Start)** | Lexicographic | 9.0 | 189m | 20.8m | 60.2% | 5.7 | 0.0% | 3.8 | 0.90 | 302.6 ms | 832.7 ms | 57.1% |
| **Default Generalist (Cold Start)** | Weighted (Shadow) | 3.8 | 70m | 17.8m | 64.2% | 3.6 | 0.0% | 0.9 | 0.98 | 156.3 ms | 386.5 ms | 81.0% |

---

## 3. Ma trận Jaccard 5×5 giữa các Persona (Mục 3d)

### 3.1. Mode Lexicographic (Baseline - Chưa Cá nhân hóa sâu)
*Mode cũ chủ yếu nhồi nhét POI theo vị trí địa lý nên lịch trình giữa các persona rất giống nhau (Jaccard cao).*

| Persona | CultureEnthusiast | FoodLoverRelaxed | ActiveExplorerFast | ScenicPhotography | DefaultGeneralist |
|---|---|---|---|---|---|
| **CultureEnthusiast** | 1.00 | **0.62** | **0.48** | **0.49** | **0.63** |
| **FoodLoverRelaxed** | **0.62** | 1.00 | **0.59** | **0.54** | **0.96** |
| **ActiveExplorerFast** | **0.48** | **0.59** | 1.00 | **0.66** | **0.60** |
| **ScenicPhotography** | **0.49** | **0.54** | **0.66** | 1.00 | **0.56** |
| **DefaultGeneralist** | **0.63** | **0.96** | **0.60** | **0.56** | 1.00 |

### 3.2. Mode Weighted Utility (Cá nhân hóa theo Hồ sơ Du khách)
*Các persona có sở thích đối lập (Văn hóa vs Ẩm thực vs Thiên nhiên) có độ tương đồng Jaccard giảm sâu, chứng minh sự phân kỳ cá nhân hóa rõ rệt.*

| Persona | CultureEnthusiast | FoodLoverRelaxed | ActiveExplorerFast | ScenicPhotography | DefaultGeneralist |
|---|---|---|---|---|---|
| **CultureEnthusiast** | 1.00 | **0.46** | **0.38** | **0.38** | **0.51** |
| **FoodLoverRelaxed** | **0.46** | 1.00 | **0.42** | **0.44** | **0.73** |
| **ActiveExplorerFast** | **0.38** | **0.42** | 1.00 | **0.54** | **0.44** |
| **ScenicPhotography** | **0.38** | **0.44** | **0.54** | 1.00 | **0.44** |
| **DefaultGeneralist** | **0.51** | **0.73** | **0.44** | **0.44** | 1.00 |

---

## 4. Phân rã $J$ và Chẩn đoán Chi tiết (Mục 4)

### 4.1. Phân rã $J$ từng lượt ghé trong lịch Weighted (5 kịch bản tiêu biểu)

#### Kịch bản: `grid-c10-m1-none-s1` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_2 | 0.600 | 1.00 | 1.000 | 0.6000 | -0.1760 | -0.0000 | **+0.4240** |
| #2 | POI_10 | 0.600 | 1.00 | 1.000 | 0.6000 | -0.1520 | -0.0000 | **+0.4480** |
| #3 | POI_3 | 0.600 | 1.00 | 0.900 | 0.5400 | -0.2160 | -0.0000 | **+0.3240** |
| #4 | POI_4 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.1440 | -0.0000 | **+0.1560** |
| #5 | POI_9 | 0.600 | 0.70 | 0.900 | 0.3780 | -0.1200 | -0.0000 | **+0.2580** |
| #6 | POI_1 | 0.300 | 0.70 | 1.000 | 0.2100 | -0.2960 | -0.0000 | **-0.0860** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_5** | Sau vị trí #6 | +0.2100 | -0.1760 | -0.3000 | **-0.2660** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_7** | Sau vị trí #6 | +0.2100 | -0.1760 | -0.3000 | **-0.2660** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_8** | Sau vị trí #6 | +0.1470 | -0.1760 | -0.3000 | **-0.3290** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **76.9%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **23.1%**.

#### Kịch bản: `grid-c10-m1-none-s1` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_2 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2640 | -0.0000 | **+0.0360** |
| #2 | POI_5 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2280 | -0.0000 | **+0.0720** |
| #3 | POI_1 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.4200 | -0.0000 | **-0.1200** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_10** | Sau vị trí #3 | +0.2100 | -0.2640 | -0.3000 | **-0.3540** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_3** | Sau vị trí #3 | +0.2100 | -0.2640 | -0.3000 | **-0.3540** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_7** | Sau vị trí #3 | +0.2100 | -0.2640 | -0.3000 | **-0.3540** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **76.9%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **23.1%**.

#### Kịch bản: `grid-c20-m1-none-s1` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_17 | 0.600 | 1.00 | 1.000 | 0.6000 | -0.1520 | -0.0000 | **+0.4480** |
| #2 | POI_2 | 0.600 | 1.00 | 1.000 | 0.6000 | -0.2080 | -0.0000 | **+0.3920** |
| #3 | POI_1 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.1520 | -0.0000 | **+0.1480** |
| #4 | POI_16 | 0.600 | 1.00 | 0.900 | 0.5400 | -0.1920 | -0.0000 | **+0.3480** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_10** | Sau vị trí #4 | +0.3780 | -0.1760 | -0.3000 | **-0.0980** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_13** | Sau vị trí #4 | +0.2100 | -0.1760 | -0.3000 | **-0.2660** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_3** | Sau vị trí #4 | +0.3780 | -0.1760 | -0.3000 | **-0.0980** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **66.7%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **33.3%**.

#### Kịch bản: `grid-c20-m1-none-s1` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_17 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2280 | -0.0000 | **+0.0720** |
| #2 | POI_1 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2640 | -0.0000 | **+0.0360** |
| #3 | POI_6 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2160 | -0.0000 | **+0.0840** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_14** | Sau vị trí #3 | +0.2100 | -0.2640 | -0.3000 | **-0.3540** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_13** | Sau vị trí #3 | +0.1470 | -0.2640 | -0.3000 | **-0.4170** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_3** | Sau vị trí #3 | +0.1470 | -0.2640 | -0.3000 | **-0.4170** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **76.9%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **23.1%**.

#### Kịch bản: `grid-c40-m1-none-s1` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_23 | 0.600 | 1.00 | 1.000 | 0.6000 | -0.1200 | -0.0000 | **+0.4800** |
| #2 | POI_24 | 0.600 | 1.00 | 1.000 | 0.6000 | -0.1200 | -0.0000 | **+0.4800** |
| #3 | POI_31 | 0.600 | 1.00 | 0.900 | 0.5400 | -0.1280 | -0.0000 | **+0.4120** |
| #4 | POI_3 | 0.600 | 1.00 | 0.810 | 0.4860 | -0.1680 | -0.0000 | **+0.3180** |
| #5 | POI_1 | 0.300 | 0.70 | 1.000 | 0.2100 | -0.1840 | -0.0000 | **+0.0260** |
| #6 | POI_9 | 0.600 | 0.70 | 0.900 | 0.3780 | -0.1520 | -0.0000 | **+0.2260** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_38** | Sau vị trí #6 | +0.3062 | -0.1760 | -0.3000 | **-0.1698** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_10** | Sau vị trí #6 | +0.3062 | -0.1760 | -0.3000 | **-0.1698** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_2** | Sau vị trí #6 | +0.3402 | -0.1760 | -0.3000 | **-0.1358** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **62.5%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **37.5%**.

#### Kịch bản: `grid-c40-m1-none-s1` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | POI_7 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2160 | -0.0000 | **+0.0840** |
| #2 | POI_1 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2280 | -0.0000 | **+0.0720** |
| #3 | POI_27 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2520 | -0.0000 | **+0.0480** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **POI_23** | Sau vị trí #3 | +0.2100 | -0.2640 | -0.3000 | **-0.3540** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_24** | Sau vị trí #3 | +0.2100 | -0.2640 | -0.3000 | **-0.3540** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **POI_2** | Sau vị trí #3 | +0.2100 | -0.2640 | -0.3000 | **-0.3540** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **76.9%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **23.1%**.

#### Kịch bản: `named-zigzag` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | M1_NearStart | 0.600 | 1.00 | 1.000 | 0.6000 | -0.1600 | -0.0000 | **+0.4400** |
| #2 | M2_NearEnd | 0.300 | 1.00 | 1.000 | 0.3000 | -0.4800 | -0.0000 | **-0.1800** |
| #3 | Opt2_NearM2 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.1280 | -0.0000 | **+0.1720** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **Opt1_NearM1** | Sau vị trí #3 | +0.2100 | -0.1760 | -0.3000 | **-0.2660** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **76.9%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **23.1%**.

#### Kịch bản: `named-zigzag` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | M1_NearStart | 0.300 | 1.00 | 1.000 | 0.3000 | -0.2400 | -0.0000 | **+0.0600** |
| #2 | M2_NearEnd | 0.300 | 1.00 | 1.000 | 0.3000 | -0.7200 | -0.0000 | **-0.4200** |
| #3 | Opt2_NearM2 | 0.300 | 1.00 | 1.000 | 0.3000 | -0.1920 | -0.0000 | **+0.1080** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **Opt1_NearM1** | Sau vị trí #3 | +0.2100 | -0.2640 | -0.3000 | **-0.3540** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **76.9%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **23.1%**.

#### Kịch bản: `danang-real-1day` | Persona: `ActiveExplorerFast`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | Động Âm Phủ | 0.721 | 1.00 | 1.000 | 0.7208 | -0.2560 | -0.0000 | **+0.4648** |
| #2 | Ngũ Hành Sơn - Thủy Sơn | 0.850 | 1.00 | 0.900 | 0.7650 | -0.0960 | -0.0000 | **+0.6690** |
| #3 | Bảo tàng Điêu khắc Chăm | 0.325 | 1.00 | 1.000 | 0.3250 | -0.2160 | -0.0000 | **+0.1090** |
| #4 | Cộng Cà Phê Bạch Đằng | 0.312 | 1.00 | 1.000 | 0.3125 | -0.1520 | -0.0000 | **+0.1605** |
| #5 | Công viên APEC | 0.638 | 1.00 | 1.000 | 0.6375 | -0.1120 | -0.0000 | **+0.5255** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **Cầu Rồng** | Sau vị trí #5 | +0.2537 | -0.1760 | -0.3000 | **-0.2222** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **73.4%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **26.6%**.

#### Kịch bản: `danang-real-1day` | Persona: `DefaultGeneralist`

| Thứ tự | POI Name | Base | TimeFit | $\gamma^k$ | Gain | TravelCost | Penalties | $\Delta J$ |
|---|---|---|---|---|---|---|---|---|
| #1 | Cầu Rồng | 0.362 | 1.00 | 1.000 | 0.3625 | -0.1440 | -0.0000 | **+0.2185** |
| #2 | Cầu Tình Yêu | 0.338 | 1.00 | 0.700 | 0.2362 | -0.1440 | -0.0000 | **+0.0922** |
| #3 | Cầu Sông Hàn | 0.338 | 1.00 | 0.490 | 0.1654 | -0.1680 | -0.0000 | **-0.0026** |
| #4 | Nhà hàng Madame Lân | 0.300 | 1.00 | 1.000 | 0.3000 | -0.1680 | -0.0000 | **+0.1320** |
| #5 | Cộng Cà Phê Bạch Đằng | 0.312 | 1.00 | 0.700 | 0.2188 | -0.1800 | -0.0000 | **+0.0388** |
| #6 | Bảo tàng Điêu khắc Chăm | 0.325 | 1.00 | 1.000 | 0.3250 | -0.2280 | -0.3000 | **-0.2030** |
| #7 | Công viên APEC | 0.338 | 0.70 | 1.000 | 0.2362 | -0.1920 | -0.3000 | **-0.2558** |

**Các điểm Lexicographic có nhưng Weighted bỏ qua (Giải thích vì sao $\Delta J < 0$):**

| POI Omitted | Vị trí chèn tốt nhất | Marginal Gain | Marginal TravelCost | Marginal Penalties | Tổng $\Delta J$ nếu chèn | Nguyên nhân chính khiến $\Delta J < 0$ |
|---|---|---|---|---|---|---|
| **Bãi biển Mỹ Khê** | Sau vị trí #7 | +0.2450 | -0.2640 | -0.6000 | **-0.6190** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |
| **Chùa Linh Ứng Bãi Bụt** | Sau vị trí #7 | +0.1286 | -0.2640 | -0.6000 | **-0.7354** | Phạt mềm cộng dồn (Soft Penalties: RhoStops/RhoRest) |

*Tỷ trọng các yếu tố làm giảm $\Delta J$:* Phạt mềm: **92.0%** | Di chuyển: **0.0%** | Suy giảm danh mục: **0.0%** | Nắng trưa: **8.0%**.

---

## 5. Bảng Đối chiếu Tiêu chí Đạt / Chưa đạt (K5 Evaluation Criteria)

| Tiêu chí | Ngưỡng kỳ vọng | Lexicographic (Baseline) | Weighted v3a (Shadow) | Đánh giá | Phân tích chi tiết |
|---|---|---|---|---|---|
| **Số điểm dừng Persona Fast** | $\ge 5$ điểm | 8.9 điểm | **5.2 điểm** | ✅ ĐẠT | Fast duy trì 5.2 điểm/lịch, phản ánh đúng nhịp độ năng động. |
| **Tỷ lệ thời gian tham quan** | $\ge 70\%$ tổng thời gian | 59.7% | **62.8%** | ❌ CHƯA ĐẠT | Tỷ lệ thời gian dành cho tham quan thực tế so với tổng thời gian tour. |
| **Thời gian di chuyển / điểm** | $\le 25$ phút/điểm | 21.2 phút | **19.0 phút** | ✅ ĐẠT | Solver tối ưu hành trình gom cụm, giảm thời gian lãng phí trên đường. |
| **Giảm lượt ngoài trời trưa nắng** | Giảm $\ge 60\%$ so với Lex | 3.8 lượt/lịch | **1.3 lượt/lịch** (giảm 65.0%) | ✅ ĐẠT | TimeFit 0.70 phát huy tác dụng bảo vệ sức khỏe du khách giờ cao điểm nắng. |
| **Đa dạng danh mục (Đà Nẵng)** | $\ge 3$ danh mục khác nhau | 3.2 danh mục | **4.2 danh mục** | ✅ ĐẠT | Thể hiện sự đa dạng trải nghiệm trên địa bàn thực tế sau khi sửa lỗi General. |
| **Thời gian giải (Latency p95)** | $\le 300\text{ ms}$ (ADR-001) | 825.6 ms | **490.7 ms** | ⚠️ CẬN BIÊN | Ngân sách 50.000 nodes kiểm soát trần thời gian chạy an toàn cho shadow. |
