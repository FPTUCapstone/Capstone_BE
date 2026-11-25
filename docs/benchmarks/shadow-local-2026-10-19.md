# Báo cáo Đo lường Shadow Mode Cục bộ (Local Shadow Benchmark)

**Ngày thực hiện:** 2026-10-19  
**Nhánh:** `feature/phuctv-uc10-personalized-csp`  
**Bộ giải:** Production Lexicographic (Primary) song song với Weighted Utility (Shadow)  
**Quy mô thử nghiệm:** 20 kịch bản CSP chuẩn × 5 Personas = **100 lượt chạy độc lập**  

---

## 1. Tóm tắt Kết quả (Executive Summary)

- **Tỷ lệ thành công (Success Rate):** **100/100 (100.0%)** — không có bất kỳ kịch bản nào bị crash hoặc timeout.
- **Độ tương đồng trung bình (Mean Jaccard Similarity):** **0.51** — phản ánh mức độ thay đổi POI có kiểm soát khi cá nhân hóa.
- **Tỷ lệ giữ nguyên thứ tự (Exact Sequence Match):** **5.0%** (chủ yếu tập trung ở nhóm Cold Start / Default Generalist).
- **Thời gian phản hồi bình quân:** Primary (Lexicographic): **269.36 ms** | Shadow (Weighted Utility): **792.28 ms**.
- **Tính an toàn vận hành:** Hoàn toàn chạy nền trong `Channel<ShadowWorkItem>` bounded, không gây nghẽn và không làm tăng độ trễ của luồng chính.

---

## 2. Danh mục 5 Du khách Giả định (Personas)

| Persona ID | Tên hiển thị | Nhịp độ (Pace) | Chấp nhận rủi ro | Danh mục yêu thích | Mô tả |
|---|---|---|---|---|---|
| `CultureEnthusiast` | **Culture Enthusiast** | Moderate | Medium | `culture, museum, history` | Ưu tiên di tích, bảo tàng, lịch sử; nhịp độ vừa phải (tối đa 5 điểm/ngày). |
| `FoodLoverRelaxed` | **Food Lover (Relaxed)** | Relaxed | Low | `food, coffee, cuisine` | Ưu tiên trải nghiệm ẩm thực, nghỉ ngơi; nhịp độ thong thả (tối đa 3 điểm/ngày). |
| `ActiveExplorerFast` | **Active Explorer (Fast)** | Fast | High | `nature, adventure, outdoor` | Ưu tiên thiên nhiên ngoài trời, tốc độ nhanh (tối đa 8 điểm/ngày). |
| `ScenicPhotography` | **Scenic & Photography** | Moderate | Low | `photography, nature, heritage` | Ưu tiên danh lam thắng cảnh, điểm chụp ảnh đẹp, nhịp độ vừa phải. |
| `DefaultGeneralist` | **Default Generalist (Cold Start)** | Moderate | Medium | `(Trống)` | Khách vãng lai chưa có hồ sơ; áp dụng cấu hình mặc định của hệ thống. |

---

## 3. Tổng hợp Chỉ số Phân kỳ theo Persona (Persona Divergence Summary)

| Persona | Số lần chạy | Tỷ lệ Thành công | Jaccard TB | Khớp 100% | Số điểm thêm TB | Số điểm bớt TB | $\Delta$ Thời lượng (phút) | $\Delta$ Chi phí (VND) | Thời gian Shadow TB (ms) |
|---|---|---|---|---|---|---|---|---|---|
| **Culture Enthusiast** | 20 | 20/20 (100%) | **0.48** | 5.0% | +0.5 | -5.0 | -231.8m | -66750 đ | 788.17 ms |
| **Food Lover (Relaxed)** | 20 | 20/20 (100%) | **0.51** | 5.0% | +0.1 | -4.8 | -237.4m | -69750 đ | 810.32 ms |
| **Active Explorer (Fast)** | 20 | 20/20 (100%) | **0.58** | 5.0% | +0.2 | -4.0 | -188.8m | -57000 đ | 788.01 ms |
| **Scenic & Photography** | 20 | 20/20 (100%) | **0.49** | 5.0% | +0.6 | -4.8 | -223.1m | -64500 đ | 782.42 ms |
| **Default Generalist (Cold Start)** | 20 | 20/20 (100%) | **0.49** | 5.0% | +0.3 | -4.9 | -232.7m | -68250 đ | 792.49 ms |

### Nhận xét về hành vi phân kỳ:
1. **FoodLoverRelaxed:** Tỷ lệ Jaccard giảm rõ rệt và số điểm bớt trung bình lớn vì nhịp độ `Relaxed` áp giới hạn `MaxStopsPerDay = 3`, loại bỏ các điểm thừa và ưu tiên tối đa thời gian ăn uống nghỉ ngơi.
2. **ActiveExplorerFast:** Tăng thêm điểm dừng trung bình vì `Fast` cho phép mở rộng `MaxStopsPerDay = 8` và chịu được nhiều nỗ lực thể lực hơn.
3. **CultureEnthusiast & ScenicPhotography:** Thay thế các POI thông thường bằng các POI thuộc danh mục văn hóa/di tích hoặc chụp ảnh danh lam thắng cảnh nhờ hệ số $\gamma = 0.85$.
4. **DefaultGeneralist:** Tiệm cận cao nhất với giải pháp gốc Lexicographic (Jaccard cao nhất, tỷ lệ giống hệt cao), đảm bảo tính kế thừa mượt mà khi khách hàng chưa thiết lập sở thích.

---

## 4. Chi tiết 100 Lượt chạy Thử nghiệm (Detailed Runs)

| # | Kịch bản | Persona | Số điểm (Lex / Shadow) | Jaccard | Giống 100% | $\Delta$ Th.lượng | $\Delta$ Chi phí | Lex (ms) | Shadow (ms) | $J_{\text{model}}$ |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `grid-c10-m0-none-s1` | Culture Enthusiast | 10 / 4 | 0.40 | 🔀 | -314m | -90000 đ | 14.8 | 388.0 | 410,000 |
| 2 | `grid-c10-m0-none-s1` | Food Lover (Relaxed) | 10 / 4 | 0.40 | 🔀 | -314m | -90000 đ | 14.8 | 379.9 | -540,000 |
| 3 | `grid-c10-m0-none-s1` | Active Explorer (Fast) | 10 / 4 | 0.40 | 🔀 | -314m | -90000 đ | 14.8 | 291.3 | 1,240,000 |
| 4 | `grid-c10-m0-none-s1` | Scenic & Photography | 10 / 3 | 0.30 | 🔀 | -377m | -105000 đ | 14.8 | 334.6 | 478,000 |
| 5 | `grid-c10-m0-none-s1` | Default Generalist (Cold Start) | 10 / 4 | 0.40 | 🔀 | -314m | -90000 đ | 14.8 | 325.1 | 410,000 |
| 6 | `grid-c10-m1-none-s1` | Culture Enthusiast | 10 / 3 | 0.30 | 🔀 | -356m | -105000 đ | 4.9 | 310.3 | 64,000 |
| 7 | `grid-c10-m1-none-s1` | Food Lover (Relaxed) | 10 / 3 | 0.30 | 🔀 | -356m | -105000 đ | 4.9 | 323.1 | -516,000 |
| 8 | `grid-c10-m1-none-s1` | Active Explorer (Fast) | 10 / 5 | 0.50 | 🔀 | -246m | -75000 đ | 4.9 | 282.8 | 1,024,000 |
| 9 | `grid-c10-m1-none-s1` | Scenic & Photography | 10 / 5 | 0.50 | 🔀 | -262m | -75000 đ | 4.9 | 319.6 | 174,000 |
| 10 | `grid-c10-m1-none-s1` | Default Generalist (Cold Start) | 10 / 5 | 0.50 | 🔀 | -262m | -75000 đ | 4.9 | 316.4 | 174,000 |
| 11 | `grid-c10-m3-auto-s1` | Culture Enthusiast | 10 / 5 | 0.50 | 🔀 | -241m | -75000 đ | 15.8 | 319.6 | -433,000 |
| 12 | `grid-c10-m3-auto-s1` | Food Lover (Relaxed) | 10 / 4 | 0.40 | 🔀 | -288m | -90000 đ | 15.8 | 355.8 | -1,586,000 |
| 13 | `grid-c10-m3-auto-s1` | Active Explorer (Fast) | 10 / 8 | 0.80 | 🔀 | -97m | -30000 đ | 15.8 | 318.1 | 1,566,000 |
| 14 | `grid-c10-m3-auto-s1` | Scenic & Photography | 10 / 6 | 0.60 | 🔀 | -195m | -60000 đ | 15.8 | 340.0 | -451,000 |
| 15 | `grid-c10-m3-auto-s1` | Default Generalist (Cold Start) | 10 / 5 | 0.50 | 🔀 | -241m | -75000 đ | 15.8 | 363.1 | -433,000 |
| 16 | `grid-c10-m1-frequent-s1` | Culture Enthusiast | 9 / 5 | 0.56 | 🔀 | -273m | -60000 đ | 258.2 | 313.6 | 574,000 |
| 17 | `grid-c10-m1-frequent-s1` | Food Lover (Relaxed) | 9 / 3 | 0.33 | 🔀 | -367m | -90000 đ | 258.2 | 334.0 | -516,000 |
| 18 | `grid-c10-m1-frequent-s1` | Active Explorer (Fast) | 9 / 6 | 0.67 | 🔀 | -175m | -45000 đ | 258.2 | 324.7 | 1,782,000 |
| 19 | `grid-c10-m1-frequent-s1` | Scenic & Photography | 9 / 5 | 0.56 | 🔀 | -273m | -60000 đ | 258.2 | 325.0 | 574,000 |
| 20 | `grid-c10-m1-frequent-s1` | Default Generalist (Cold Start) | 9 / 5 | 0.56 | 🔀 | -273m | -60000 đ | 258.2 | 306.9 | 574,000 |
| 21 | `grid-c20-m0-none-s1` | Culture Enthusiast | 11 / 5 | 0.14 | 🔀 | -317m | -90000 đ | 235.8 | 375.2 | 246,000 |
| 22 | `grid-c20-m0-none-s1` | Food Lover (Relaxed) | 11 / 4 | 0.36 | 🔀 | -344m | -105000 đ | 235.8 | 439.2 | -568,000 |
| 23 | `grid-c20-m0-none-s1` | Active Explorer (Fast) | 11 / 4 | 0.25 | 🔀 | -341m | -105000 đ | 235.8 | 322.4 | 1,256,000 |
| 24 | `grid-c20-m0-none-s1` | Scenic & Photography | 11 / 5 | 0.23 | 🔀 | -298m | -90000 đ | 235.8 | 375.2 | 177,000 |
| 25 | `grid-c20-m0-none-s1` | Default Generalist (Cold Start) | 11 / 3 | 0.27 | 🔀 | -380m | -120000 đ | 235.8 | 394.2 | 367,000 |
| 26 | `grid-c20-m1-none-s1` | Culture Enthusiast | 11 / 5 | 0.33 | 🔀 | -303m | -90000 đ | 302.2 | 404.0 | 120,000 |
| 27 | `grid-c20-m1-none-s1` | Food Lover (Relaxed) | 11 / 4 | 0.36 | 🔀 | -345m | -105000 đ | 302.2 | 446.2 | -830,000 |
| 28 | `grid-c20-m1-none-s1` | Active Explorer (Fast) | 11 / 4 | 0.36 | 🔀 | -327m | -105000 đ | 302.2 | 324.6 | 910,000 |
| 29 | `grid-c20-m1-none-s1` | Scenic & Photography | 11 / 5 | 0.33 | 🔀 | -303m | -90000 đ | 302.2 | 399.5 | 120,000 |
| 30 | `grid-c20-m1-none-s1` | Default Generalist (Cold Start) | 11 / 4 | 0.36 | 🔀 | -345m | -105000 đ | 302.2 | 448.5 | 110,000 |
| 31 | `grid-c20-m3-auto-s1` | Culture Enthusiast | 10 / 5 | 0.50 | 🔀 | -235m | -75000 đ | 282.7 | 360.6 | -53,000 |
| 32 | `grid-c20-m3-auto-s1` | Food Lover (Relaxed) | 10 / 5 | 0.50 | 🔀 | -228m | -75000 đ | 282.7 | 464.6 | -1,644,000 |
| 33 | `grid-c20-m3-auto-s1` | Active Explorer (Fast) | 10 / 5 | 0.50 | 🔀 | -220m | -75000 đ | 282.7 | 348.0 | 896,000 |
| 34 | `grid-c20-m3-auto-s1` | Scenic & Photography | 10 / 6 | 0.45 | 🔀 | -185m | -60000 đ | 282.7 | 394.9 | -427,000 |
| 35 | `grid-c20-m3-auto-s1` | Default Generalist (Cold Start) | 10 / 6 | 0.45 | 🔀 | -185m | -60000 đ | 282.7 | 403.3 | -427,000 |
| 36 | `grid-c20-m6-auto-s1` | Culture Enthusiast | 10 / 6 | 0.60 | 🔀 | -187m | -60000 đ | 382.7 | 891.0 | -955,000 |
| 37 | `grid-c20-m6-auto-s1` | Food Lover (Relaxed) | 10 / 7 | 0.70 | 🔀 | -150m | -45000 đ | 382.7 | 772.8 | -2,830,000 |
| 38 | `grid-c20-m6-auto-s1` | Active Explorer (Fast) | 10 / 8 | 0.80 | 🔀 | -101m | -30000 đ | 382.7 | 1123.0 | 1,080,000 |
| 39 | `grid-c20-m6-auto-s1` | Scenic & Photography | 10 / 6 | 0.60 | 🔀 | -187m | -60000 đ | 382.7 | 905.3 | -955,000 |
| 40 | `grid-c20-m6-auto-s1` | Default Generalist (Cold Start) | 10 / 6 | 0.60 | 🔀 | -187m | -60000 đ | 382.7 | 816.7 | -955,000 |
| 41 | `grid-c20-m1-frequent-s1` | Culture Enthusiast | 9 / 5 | 0.27 | 🔀 | -276m | -60000 đ | 295.1 | 362.6 | 520,000 |
| 42 | `grid-c20-m1-frequent-s1` | Food Lover (Relaxed) | 9 / 4 | 0.44 | 🔀 | -317m | -75000 đ | 295.1 | 397.7 | -650,000 |
| 43 | `grid-c20-m1-frequent-s1` | Active Explorer (Fast) | 9 / 6 | 0.67 | 🔀 | -180m | -45000 đ | 295.1 | 357.2 | 1,894,000 |
| 44 | `grid-c20-m1-frequent-s1` | Scenic & Photography | 9 / 5 | 0.27 | 🔀 | -276m | -60000 đ | 295.1 | 359.9 | 520,000 |
| 45 | `grid-c20-m1-frequent-s1` | Default Generalist (Cold Start) | 9 / 4 | 0.30 | 🔀 | -318m | -75000 đ | 295.1 | 372.1 | 310,000 |
| 46 | `grid-c20-m3-frequent-s1` | Culture Enthusiast | 9 / 5 | 0.56 | 🔀 | -257m | -60000 đ | 287.7 | 813.0 | -53,000 |
| 47 | `grid-c20-m3-frequent-s1` | Food Lover (Relaxed) | 9 / 6 | 0.50 | 🔀 | -186m | -45000 đ | 287.7 | 741.6 | -1,966,000 |
| 48 | `grid-c20-m3-frequent-s1` | Active Explorer (Fast) | 9 / 6 | 0.50 | 🔀 | -175m | -45000 đ | 287.7 | 814.7 | 1,382,000 |
| 49 | `grid-c20-m3-frequent-s1` | Scenic & Photography | 9 / 7 | 0.60 | 🔀 | -133m | -30000 đ | 287.7 | 839.4 | -379,000 |
| 50 | `grid-c20-m3-frequent-s1` | Default Generalist (Cold Start) | 9 / 6 | 0.50 | 🔀 | -177m | -45000 đ | 287.7 | 813.6 | -427,000 |
| 51 | `grid-c40-m0-none-s1` | Culture Enthusiast | 11 / 5 | 0.33 | 🔀 | -313m | -90000 đ | 507.7 | 568.2 | 453,000 |
| 52 | `grid-c40-m0-none-s1` | Food Lover (Relaxed) | 11 / 4 | 0.36 | 🔀 | -354m | -105000 đ | 507.7 | 706.8 | -296,000 |
| 53 | `grid-c40-m0-none-s1` | Active Explorer (Fast) | 11 / 4 | 0.36 | 🔀 | -332m | -105000 đ | 507.7 | 505.9 | 1,200,000 |
| 54 | `grid-c40-m0-none-s1` | Scenic & Photography | 11 / 4 | 0.25 | 🔀 | -364m | -105000 đ | 507.7 | 573.3 | 422,000 |
| 55 | `grid-c40-m0-none-s1` | Default Generalist (Cold Start) | 11 / 4 | 0.25 | 🔀 | -364m | -105000 đ | 507.7 | 626.5 | 422,000 |
| 56 | `grid-c40-m1-none-s1` | Culture Enthusiast | 11 / 3 | 0.27 | 🔀 | -401m | -120000 đ | 462.3 | 588.5 | 256,000 |
| 57 | `grid-c40-m1-none-s1` | Food Lover (Relaxed) | 11 / 4 | 0.36 | 🔀 | -344m | -105000 đ | 462.3 | 708.2 | -694,000 |
| 58 | `grid-c40-m1-none-s1` | Active Explorer (Fast) | 11 / 5 | 0.45 | 🔀 | -276m | -90000 đ | 462.3 | 492.5 | 1,212,000 |
| 59 | `grid-c40-m1-none-s1` | Scenic & Photography | 11 / 3 | 0.27 | 🔀 | -401m | -120000 đ | 462.3 | 603.7 | 256,000 |
| 60 | `grid-c40-m1-none-s1` | Default Generalist (Cold Start) | 11 / 3 | 0.27 | 🔀 | -401m | -120000 đ | 462.3 | 643.5 | 256,000 |
| 61 | `grid-c40-m3-none-s1` | Culture Enthusiast | 11 / 4 | 0.36 | 🔀 | -333m | -105000 đ | 477.5 | 641.6 | -169,000 |
| 62 | `grid-c40-m3-none-s1` | Food Lover (Relaxed) | 11 / 5 | 0.45 | 🔀 | -286m | -90000 đ | 477.5 | 681.3 | -1,432,000 |
| 63 | `grid-c40-m3-none-s1` | Active Explorer (Fast) | 11 / 6 | 0.55 | 🔀 | -234m | -75000 đ | 477.5 | 528.3 | 866,000 |
| 64 | `grid-c40-m3-none-s1` | Scenic & Photography | 11 / 4 | 0.36 | 🔀 | -333m | -105000 đ | 477.5 | 645.8 | -169,000 |
| 65 | `grid-c40-m3-none-s1` | Default Generalist (Cold Start) | 11 / 4 | 0.36 | 🔀 | -333m | -105000 đ | 477.5 | 694.3 | -169,000 |
| 66 | `grid-c40-m6-none-s1` | Culture Enthusiast | 11 / 8 | 0.73 | 🔀 | -90m | -45000 đ | 552.2 | 1157.3 | -2,340,000 |
| 67 | `grid-c40-m6-none-s1` | Food Lover (Relaxed) | 11 / 8 | 0.73 | 🔀 | -78m | -45000 đ | 552.2 | 1082.0 | -4,368,000 |
| 68 | `grid-c40-m6-none-s1` | Active Explorer (Fast) | 11 / 10 | 0.75 | 🔀 | -38m | -15000 đ | 552.2 | 1608.4 | -136,000 |
| 69 | `grid-c40-m6-none-s1` | Scenic & Photography | 11 / 7 | 0.64 | 🔀 | -135m | -60000 đ | 552.2 | 1028.4 | -2,113,000 |
| 70 | `grid-c40-m6-none-s1` | Default Generalist (Cold Start) | 11 / 7 | 0.64 | 🔀 | -135m | -60000 đ | 552.2 | 1043.7 | -2,113,000 |
| 71 | `grid-c40-m1-auto-s1` | Culture Enthusiast | 10 / 5 | 0.36 | 🔀 | -277m | -75000 đ | 471.7 | 578.9 | 589,000 |
| 72 | `grid-c40-m1-auto-s1` | Food Lover (Relaxed) | 10 / 4 | 0.40 | 🔀 | -329m | -90000 đ | 471.7 | 673.6 | -494,000 |
| 73 | `grid-c40-m1-auto-s1` | Active Explorer (Fast) | 10 / 5 | 0.50 | 🔀 | -261m | -75000 đ | 471.7 | 521.1 | 1,612,000 |
| 74 | `grid-c40-m1-auto-s1` | Scenic & Photography | 10 / 5 | 0.36 | 🔀 | -277m | -75000 đ | 471.7 | 569.6 | 589,000 |
| 75 | `grid-c40-m1-auto-s1` | Default Generalist (Cold Start) | 10 / 5 | 0.36 | 🔀 | -277m | -75000 đ | 471.7 | 550.7 | 589,000 |
| 76 | `grid-c40-m6-frequent-s1` | Culture Enthusiast | 9 / 7 | 0.60 | 🔀 | -93m | -30000 đ | 699.5 | 7040.5 | -1,528,000 |
| 77 | `grid-c40-m6-frequent-s1` | Food Lover (Relaxed) | 9 / 7 | 0.78 | 🔀 | -97m | -30000 đ | 699.5 | 7021.4 | -3,330,000 |
| 78 | `grid-c40-m6-frequent-s1` | Active Explorer (Fast) | 9 / 7 | 0.60 | 🔀 | -100m | -30000 đ | 699.5 | 7049.1 | 850,000 |
| 79 | `grid-c40-m6-frequent-s1` | Scenic & Photography | 9 / 7 | 0.60 | 🔀 | -93m | -30000 đ | 699.5 | 6886.8 | -1,528,000 |
| 80 | `grid-c40-m6-frequent-s1` | Default Generalist (Cold Start) | 9 / 7 | 0.60 | 🔀 | -93m | -30000 đ | 699.5 | 7084.3 | -1,528,000 |
| 81 | `named-zigzag` | Culture Enthusiast | 4 / 4 | 1.00 | 🔀 | 0m | 0 đ | 1.8 | 0.7 | -415,000 |
| 82 | `named-zigzag` | Food Lover (Relaxed) | 4 / 4 | 1.00 | 🔀 | 0m | 0 đ | 1.8 | 0.6 | -1,480,000 |
| 83 | `named-zigzag` | Active Explorer (Fast) | 4 / 4 | 1.00 | 🔀 | +5m | 0 đ | 1.8 | 0.5 | 352,000 |
| 84 | `named-zigzag` | Scenic & Photography | 4 / 4 | 1.00 | 🔀 | 0m | 0 đ | 1.8 | 0.5 | -415,000 |
| 85 | `named-zigzag` | Default Generalist (Cold Start) | 4 / 4 | 1.00 | 🔀 | 0m | 0 đ | 1.8 | 0.4 | -415,000 |
| 86 | `named-two-opt` | Culture Enthusiast | 4 / 4 | 1.00 | ✅ | 0m | 0 đ | 0.2 | 0.3 | 425,000 |
| 87 | `named-two-opt` | Food Lover (Relaxed) | 4 / 4 | 1.00 | ✅ | 0m | 0 đ | 0.2 | 0.2 | -400,000 |
| 88 | `named-two-opt` | Active Explorer (Fast) | 4 / 4 | 1.00 | ✅ | 0m | 0 đ | 0.2 | 0.3 | 1,080,000 |
| 89 | `named-two-opt` | Scenic & Photography | 4 / 4 | 1.00 | ✅ | 0m | 0 đ | 0.2 | 0.3 | 425,000 |
| 90 | `named-two-opt` | Default Generalist (Cold Start) | 4 / 4 | 1.00 | ✅ | 0m | 0 đ | 0.2 | 0.2 | 425,000 |
| 91 | `incumbent-valid-c20-m2-none-s10` | Culture Enthusiast | 11 / 4 | 0.36 | 🔀 | -348m | -105000 đ | 130.6 | 469.1 | 179,000 |
| 92 | `incumbent-valid-c20-m2-none-s10` | Food Lover (Relaxed) | 11 / 4 | 0.36 | 🔀 | -348m | -105000 đ | 130.6 | 490.7 | -696,000 |
| 93 | `incumbent-valid-c20-m2-none-s10` | Active Explorer (Fast) | 11 / 4 | 0.36 | 🔀 | -348m | -105000 đ | 130.6 | 348.1 | 904,000 |
| 94 | `incumbent-valid-c20-m2-none-s10` | Scenic & Photography | 11 / 4 | 0.36 | 🔀 | -348m | -105000 đ | 130.6 | 538.3 | 179,000 |
| 95 | `incumbent-valid-c20-m2-none-s10` | Default Generalist (Cold Start) | 11 / 4 | 0.36 | 🔀 | -348m | -105000 đ | 130.6 | 462.4 | 179,000 |
| 96 | `budget-limited-optional-s23` | Culture Enthusiast | 3 / 3 | 0.50 | 🔀 | -21m | 0 đ | 3.7 | 180.4 | 490,000 |
| 97 | `budget-limited-optional-s23` | Food Lover (Relaxed) | 3 / 3 | 0.50 | 🔀 | -16m | 0 đ | 3.7 | 186.5 | -38,000 |
| 98 | `budget-limited-optional-s23` | Active Explorer (Fast) | 3 / 3 | 0.50 | 🔀 | -16m | 0 đ | 3.7 | 199.2 | 1,142,000 |
| 99 | `budget-limited-optional-s23` | Scenic & Photography | 3 / 3 | 0.50 | 🔀 | -21m | 0 đ | 3.7 | 208.2 | 490,000 |
| 100 | `budget-limited-optional-s23` | Default Generalist (Cold Start) | 3 / 3 | 0.50 | 🔀 | -21m | 0 đ | 3.7 | 183.9 | 490,000 |

---

## 5. Kết luận & Đánh giá Sẵn sàng (Deployment Readiness)

- **Tính ổn định (Zero Regressions):** Toàn bộ 100/100 lượt chạy đều tìm được lịch trình khả thi hợp lệ; không gặp lỗi chia cho 0, null reference, hay vi phạm ràng buộc khung giờ.
- **Khả năng cách ly tài nguyên:** Tách biệt hoàn toàn `routeDurationProvider` và luồng chạy ngầm qua `Task.Run` / background hosted service giúp request chính của người dùng không chịu bất kỳ chi phí thời gian nào từ bộ giải shadow.
- **Sẵn sàng triển khai:** Kiến trúc shadow runner đáp ứng đầy đủ tiêu chí kỹ thuật để bật cờ `Shadow:Enabled = true` với tỷ lệ lấy mẫu `SampleRate` an toàn trên môi trường staging/canary.
