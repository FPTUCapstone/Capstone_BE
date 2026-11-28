# Phase 1 Task 6 — Migration Verification (Dry-Run)

**Task 6 — Phase 1**  
Ngày: 25/11/2026. DB: `TripMateDb` trên Docker localhost:14330.  
Migration: `20261125_direct_operator_payment_refund_foundation.sql`

---

## 1. Backup trước migration

```
Path (trong container): /var/opt/mssql/data/TripMateDb_before_task5.bak
Lệnh: BACKUP DATABASE [TripMateDb] TO DISK = N'/var/opt/mssql/data/TripMateDb_before_task5.bak'
      WITH FORMAT, INIT, COMPRESSION, STATS = 10;
Kết quả: Backup completed successfully.
```

---

## 2. Snapshot TRƯỚC migration

| Metric | Giá trị |
|---|---|
| `commerce.Bookings` — tổng rows | **309** |
| `commerce.Bookings` — tổng `total_amount` (VND) | **1,175,866,500** |
| `commerce.Bookings` — `payment_status = 'Paid'` | **262** |
| `commerce.Bookings` — `status = 'Confirmed'` | **120** |
| `payment.Refunds` — tổng rows (legacy) | **16** |
| `payment.Refunds` — `status = 'Processed'` | **6** |
| `payment.Refunds` — tổng `amount` (Processed, VND) | **26,970,000** |
| Column `payment_flow` tồn tại | **0 (chưa có)** |
| Table `payment.OperatorPaymentChannels` tồn tại | **không** |
| Table `payment.DirectRefundCases` tồn tại | **không** |
| Table `payment.DirectRefundTransfers` tồn tại | **không** |
| Table `commercial.TourDirectCommissionStatements` tồn tại | **không** |

---

## 3. Áp migration

```
Migration: 20261125_direct_operator_payment_refund_foundation.sql
Kết quả run 1: Migration applied OK.
Kết quả run 2: Migration applied OK (idempotency confirmed — chạy lại không lỗi).
```

**Hai lần chạy liên tiếp đều thành công** — đúng với done criteria.

---

## 4. Snapshot SAU migration

| Metric | Giá trị | So sánh |
|---|---|---|
| `commerce.Bookings` — tổng rows | **309** | ✅ Không đổi |
| `commerce.Bookings` — tổng `total_amount` (VND) | **1,175,866,500** | ✅ Không đổi |
| `commerce.Bookings` — `payment_status = 'Paid'` | **262** | ✅ Không đổi |
| `commerce.Bookings` — `status = 'Confirmed'` | **120** | ✅ Không đổi |
| `payment.Refunds` — tổng rows (legacy) | **16** | ✅ Không đổi |
| `payment.Refunds` — `status = 'Processed'` | **6** | ✅ Không đổi |
| `payment.Refunds` — tổng `amount` (Processed, VND) | **26,970,000** | ✅ Không đổi |
| Column `payment_flow` tồn tại | **1 (có)** | ✅ Thêm thành công |
| Bookings có `payment_flow = 'PlatformCollectedLegacy'` | **309** | ✅ 100% legacy default |
| Bookings có `payment_flow = 'OperatorDirect'` | **0** | ✅ Chưa có direct booking |
| Rows trong `payment.OperatorPaymentChannels` | **0** | ✅ Bảng mới, trống đúng |
| Rows trong `payment.DirectRefundCases` | **0** | ✅ Bảng mới, trống đúng |
| Rows trong `payment.DirectRefundTransfers` | **0** | ✅ Bảng mới, trống đúng |
| Rows trong `commercial.TourDirectCommissionStatements` | **0** | ✅ Bảng mới, trống đúng |

---

## 5. Bugs tìm được trong quá trình áp — đã sửa

| Bug | Mô tả | Fix |
|---|---|---|
| **Filtered index với `SYSUTCDATETIME()`** | `CREATE INDEX ... WHERE confirmation_deadline_at > SYSUTCDATETIME()` bị lỗi — SQL Server không cho phép function trong predicate của filtered index | Bỏ WHERE clause, dùng plain index |
| **Filtered index với N'' prefix và VARCHAR column** | `WHERE status IN (N'AwaitingTransfer')` lỗi collation mismatch (`status` là VARCHAR; `N''` là NVARCHAR) | Bỏ filtered index thừa (covered bởi composite index IX_DirectRefundCases_Status) |
| **CHECK constraint trong block riêng biệt** | SQL Server validate `CHECK (payment_flow IN (...))` ở compile-time; khi column chưa tồn tại (run sau rollback) thì fail | Gộp CHECK constraint vào cùng lệnh `ADD COLUMN` |

Sau khi sửa, cả 3 lỗi đều được test lại và migration chạy thành công.

---

## 6. Done criteria — Task 6

- ✅ Backup thực hiện trước khi áp migration
- ✅ Số record và tổng tài chính TRƯỚC và SAU được ghi
- ✅ Legacy data nguyên vẹn: 309 bookings, 1,175,866,500 VND, 16 refunds, 26,970,000 VND processed
- ✅ Migration chạy 2 lần không thay đổi dữ liệu (idempotent)
- ✅ Tất cả 309 bookings cũ được gán `payment_flow = 'PlatformCollectedLegacy'` tự động
- ✅ 4 bảng mới tạo thành công, bắt đầu từ 0 row
- ✅ Không áp production (chỉ áp trên Docker dev localhost:14330)
