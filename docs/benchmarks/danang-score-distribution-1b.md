# Báo cáo Đo lường Phân phối Điểm Mục 1b (Đà Nẵng × 5 Personas)

## 1. Bảng Phân phối Điểm (BaseScore, AiScore, Base)

| Persona | Chỉ số | Min | Trung vị | Max | Mean | Độ lệch chuẩn (StdDev) |
|---|---|---|---|---|---|---|
| **Culture Enthusiast** | **BaseScore** (nội bộ) | 0.2875 | 0.6375 | 0.7708 | 0.5534 | 0.1905 |
| | **AiScore** (bge-m3) | 0.2213 | 0.7302 | 0.8836 | 0.6561 | 0.1939 |
| | **Base: AI Success** (0.6 Base + 0.4 Ai) | 0.2760 | 0.6991 | 0.7612 | 0.5945 | 0.1817 |
| | **Base: AI Fallback** (0.6 Base + 0.4 AiFallback) | 0.4125 | 0.6225 | 0.7025 | 0.5720 | 0.1143 |
| **Food Lover (Relaxed)** | **BaseScore** (nội bộ) | 0.2750 | 0.3375 | 0.5000 | 0.3534 | 0.0606 |
| | **AiScore** (bge-m3) | 0.3320 | 0.5559 | 0.8904 | 0.5608 | 0.1617 |
| | **Base: AI Success** (0.6 Base + 0.4 Ai) | 0.2978 | 0.4396 | 0.6162 | 0.4363 | 0.0951 |
| | **Base: AI Fallback** (0.6 Base + 0.4 AiFallback) | 0.4050 | 0.4425 | 0.5400 | 0.4520 | 0.0363 |
| **Active Explorer (Fast)** | **BaseScore** (nội bộ) | 0.2750 | 0.3375 | 0.8500 | 0.4138 | 0.1731 |
| | **AiScore** (bge-m3) | 0.3019 | 0.5963 | 0.9759 | 0.6097 | 0.2071 |
| | **Base: AI Success** (0.6 Base + 0.4 Ai) | 0.3083 | 0.4270 | 0.9004 | 0.4922 | 0.1812 |
| | **Base: AI Fallback** (0.6 Base + 0.4 AiFallback) | 0.4050 | 0.4425 | 0.7500 | 0.4883 | 0.1039 |
| **Scenic & Photography** | **BaseScore** (nội bộ) | 0.2750 | 0.3792 | 0.7167 | 0.4076 | 0.1376 |
| | **AiScore** (bge-m3) | 0.2840 | 0.7048 | 0.8751 | 0.6493 | 0.2038 |
| | **Base: AI Success** (0.6 Base + 0.4 Ai) | 0.3011 | 0.5094 | 0.7710 | 0.5042 | 0.1522 |
| | **Base: AI Fallback** (0.6 Base + 0.4 AiFallback) | 0.4050 | 0.4675 | 0.6700 | 0.4845 | 0.0826 |
| **Default Generalist (Cold Start)** | **BaseScore** (nội bộ) | 0.2750 | 0.3313 | 0.3750 | 0.3242 | 0.0287 |
| | **AiScore** (bge-m3) | 0.5000 | 0.5000 | 0.5000 | 0.5000 | 0.0000 |
| | **Base: AI Success** (0.6 Base + 0.4 Ai) | 0.3650 | 0.3988 | 0.4250 | 0.3945 | 0.0172 |
| | **Base: AI Fallback** (0.6 Base + 0.4 AiFallback) | 0.3650 | 0.3988 | 0.4250 | 0.3945 | 0.0172 |

## 2. So sánh Số điểm dừng và Lịch trình Weighted (AI Success vs. AI Fallback 0.60/0.50)

| Persona | Số điểm (Success) | Số điểm (Fallback) | Chênh lệch ($\Delta$) | Visited POIs (Success) | Visited POIs (Fallback) | Model Obj (Success) | Model Obj (Fallback) |
|---|---|---|---|---|---|---|---|
| **Culture Enthusiast** | **6** | **6** | +0 | [5] Cầu Rồng, [7] Cầu Tình Yêu, [6] Cầu Sông Hàn, [3] Bảo tàng Mỹ thuật Đà Nẵng, [2] Bảo tàng Đà Nẵng, [1] Bảo tàng Điêu khắc Chăm | [5] Cầu Rồng, [7] Cầu Tình Yêu, [6] Cầu Sông Hàn, [3] Bảo tàng Mỹ thuật Đà Nẵng, [2] Bảo tàng Đà Nẵng, [1] Bảo tàng Điêu khắc Chăm | 34324 | 30017 |
| **Food Lover (Relaxed)** | **3** | **3** | +0 | [5] Cầu Rồng, [8] Công viên APEC, [1] Bảo tàng Điêu khắc Chăm | [5] Cầu Rồng, [8] Công viên APEC, [1] Bảo tàng Điêu khắc Chăm | 10169 | 10704 |
| **Active Explorer (Fast)** | **5** | **7** | -2 | [5] Cầu Rồng, [13] Động Âm Phủ, [12] Ngũ Hành Sơn - Thủy Sơn, [1] Bảo tàng Điêu khắc Chăm, [8] Công viên APEC | [5] Cầu Rồng, [8] Công viên APEC, [13] Động Âm Phủ, [1] Bảo tàng Điêu khắc Chăm, [10] Chợ Cồn, [6] Cầu Sông Hàn, [7] Cầu Tình Yêu | 27256 | 24338 |
| **Scenic & Photography** | **5** | **5** | +0 | [5] Cầu Rồng, [9] Bãi biển Mỹ Khê, [3] Bảo tàng Mỹ thuật Đà Nẵng, [1] Bảo tàng Điêu khắc Chăm, [8] Công viên APEC | [5] Cầu Rồng, [9] Bãi biển Mỹ Khê, [3] Bảo tàng Mỹ thuật Đà Nẵng, [1] Bảo tàng Điêu khắc Chăm, [8] Công viên APEC | 24937 | 21836 |
| **Default Generalist (Cold Start)** | **5** | **5** | +0 | [5] Cầu Rồng, [7] Cầu Tình Yêu, [8] Công viên APEC, [1] Bảo tàng Điêu khắc Chăm, [2] Bảo tàng Đà Nẵng | [5] Cầu Rồng, [7] Cầu Tình Yêu, [8] Công viên APEC, [1] Bảo tàng Điêu khắc Chăm, [2] Bảo tàng Đà Nẵng | 15086 | 15086 |

## 3. Nhận xét & Đánh giá Tiêu chí

- **Culture Enthusiast**: StdDev AiScore = **0.1939** (✅ >= 0.05: Phân tán tốt).
- **Food Lover (Relaxed)**: StdDev AiScore = **0.1617** (✅ >= 0.05: Phân tán tốt).
- **Active Explorer (Fast)**: StdDev AiScore = **0.2071** (✅ >= 0.05: Phân tán tốt).
- **Scenic & Photography**: StdDev AiScore = **0.2038** (✅ >= 0.05: Phân tán tốt).
- **Default Generalist (Cold Start)**: StdDev AiScore = **0.0000** (⚠️ < 0.05: Cần xem xét chuẩn hóa).