# Direct Payment Cutover Matrix

**Date:** 25/11/2026  
**Branch:** `fix/linhnv-payout-and-refund`  
**Status:** Phase 0 Task 1 — SRS Alignment & Legacy-Direct Separation  

---

## 1. Mục Đích

Tài liệu này ánh xạ các UC hiện tại (legacy flow: TripMate giữ tiền) sang UC mới (direct flow: TO kênh TO), để:
- ✅ Chỉ rõ UC nào sử dụng legacy, UC nào dùng direct
- ✅ Xác định sự phụ thuộc + điểm vào (entry points)
- ✅ Phân biệt các worker/service trong code
- ✅ Giải quyết xung đột UC-67

---

## 2. UC Mapping — Legacy vs Direct

### UC-27: Book Tour (unchanged concept, 2 flows)

**Current SRS (UC-27):**
> Allows a Traveler to create a booking for a selected eligible tour package. TripMate validates relevant availability and booking information and records the selected tour, Traveler, quantity or slot information, applicable price, and initial booking status. Payment processing is performed separately when electronic payment is required.

**Split in Direct Model:**

| Aspect | Legacy Flow | Direct Flow |
|--------|-------------|-------------|
| **UC name** | UC-27 Book Tour (Legacy) | UC-27 Book Tour (Direct) |
| **Who does payment** | Traveler → TripMate | Traveler → TO |
| **Link created by** | TripMate gateway | TO gateway (backend selects) |
| **QR beneficiary** | TripMate bank account | TO bank account |
| **Booking state** | `PendingPayment` (same) | `PendingPayment` (same) |
| **Worker** | `IPaymentGateway` (TripMate) | `IPaymentGateway` (TO resolver) |
| **Code route** | Legacy path in `CreateOrReusePaymentLinkCommandHandler` | Direct path in `CreateOrReusePaymentLinkCommandHandler` |
| **Feature flag** | `IF paymentFlow == PlatformCollectedLegacy` | `IF paymentFlow == OperatorDirect` |

**Decision:**
- Keep UC-27 name in SRS
- In code: feature flag routes to legacy OR direct handler
- Both converge on same `Booking` entity, same `PendingPayment` state
- Different `PaymentSnapshot` data stored (channelVersion, purpose, etc.)

---

### UC-28: Make Electronic Payment → Payment Processing Logic Split

**Current SRS (UC-28):**
> Allows a Traveler to complete payment for an eligible TripMate booking. TripMate creates the payment request and sends the required transaction information to the integrated external Payment Gateway. The Traveler is returned to TripMate through a deep link on the mobile application and through a redirect URL on the web application...

**Split in Direct Model:**

| Aspect | Legacy | Direct |
|--------|--------|--------|
| **Flow** | Traveler calls GET payment link → TripMate → Traveler pays | Traveler calls GET payment link → TO → Traveler pays |
| **Webhook endpoint** | POST `/api/v1/payments/payos/webhook` | POST `/api/v1/payments/payos/channels/{opaqueId}/webhook` |
| **Handler** | `PayOsWebhookController` → `ProcessPayOsWebhookCommand` | `PayOsWebhookController` (route per channel) |
| **Signature verify** | TripMate key | TO key (from encrypted storage) |
| **Beneficiary** | TripMate account | TO account (from snapshot) |
| **Booking transition** | `PendingPayment → Paid` | `PendingPayment → Paid` (same) |
| **Payout obligation** | Create `OperatorPayout` (70/90%) | Create `CommissionStatement` (10%) |
| **Worker** | `PaymentCompletionProcessor` | `PaymentCompletionProcessor` (same, different payout logic) |

**Decision:**
- UC-28 flow logic unchanged (payment is payment)
- Route to webhook differs by channel
- Webhook signature per merchant
- Backend payout obligation differs (legacy payout vs. commission)

---

### UC-42: Initiate Booking Refund → Split Legacy & Direct Refund

**Current SRS (UC-42):**
> Allows a Tour Operator to initiate the refund process for an eligible cancelled booking according to the configured refund policy. TripMate determines or retrieves the applicable refund information and sends the refund transaction request to the external Payment Gateway...

**Split in Direct Model:**

| Aspect | Legacy | Direct |
|--------|--------|--------|
| **Flow** | TO clicks refund → TripMate calls payOS → refund to Traveler | TO/Traveler → system creates refund obligation → TO transfers manually |
| **Entry point** | `OperatorRefundsController.cs` `/confirm` endpoint | `DirectRefundsController.cs` (new) |
| **Refund entity** | `OperatorRefund` (legacy table) | `DirectRefundCase` (new table) |
| **Refund flow** | `RefundProcessor` → `RefundProcessingBackgroundService` | `DirectRefundCreate` → `DirectRefundDeadlineProcessor` |
| **TO action** | Approve refund | Report transfer + evidence |
| **Traveler action** | Auto-receive (or no action) | Confirm receipt (72h window) |
| **Admin role** | None | Resolve disputes |
| **Final state** | Refund completed | Refund completed or Disputed |

**Decision:**
- Keep UC-42 name, clarify it applies ONLY to legacy bookings
- Create UC-71 (or renumber): "Traveler Confirm Direct Refund Transfer" (new)
- Create UC-72 (or renumber): "Admin Review Disputed Refund" (new)
- Entry points separate: legacy refund workers do NOT touch direct; direct refund workers do NOT touch legacy

---

### UC-44: View Revenue Report (unchanged concept, 2 filters)

**Current SRS (UC-44):**
> Allows a Tour Operator to review financial information associated only with its own tour activities on TripMate. Available information may include booking revenue, successful payments, discounts, completed bookings, commission deductions, payout amounts, and other financial summaries available to the operator.

**Split in Direct Model:**

| Aspect | Legacy | Direct |
|--------|--------|--------|
| **Data source** | `OperatorPayout` (70/90% payout) | `CommissionStatement` (10% commission) |
| **Filter** | WHERE `booking.paymentFlow = PlatformCollectedLegacy` | WHERE `booking.paymentFlow = OperatorDirect` |
| **Columns** | Payout %, gross revenue, net | Commission rate, base, commission due |
| **Endpoint** | GET `/api/v1/operator/revenue-report` (legacy label) | GET `/api/v1/operator/commission-statements` (new) |
| **Report label** | "Payout Report (Legacy)" | "Commission Report (Direct)" |

**Decision:**
- Keep UC-44 for legacy reporting
- Create UC-73: "View Commission Statement" (new)
- Both show on TO dashboard but tabs separate
- Filter by `paymentFlow` in code

---

### UC-45: Export Revenue Report (unchanged concept, 2 paths)

**Current SRS (UC-45):**
> Allows a Tour Operator to export available revenue information for offline analysis or business reporting. TripMate generates the report using financial information visible to that operator and produces it in one or more supported export formats...

**Split in Direct Model:**

| Aspect | Legacy | Direct |
|--------|--------|--------|
| **Source** | UC-44 legacy data | UC-73 direct data |
| **Export endpoint** | POST `/api/v1/operator/revenue-report/export` | POST `/api/v1/operator/commission-statements/export` |
| **Handler** | `ExportPayoutReportHandler` | `ExportCommissionStatementHandler` |
| **File format** | CSV/PDF (payout columns) | CSV/PDF (commission columns) |

**Decision:**
- Keep UC-45 for legacy export
- Create UC-74: "Export Commission Statement" (new)
- Both go through export worker, different DTO

---

### UC-46: Request Payout Settlement → Rename to "Request Commission Settlement"

**Current SRS (UC-46):**
> Allows a Tour Operator to submit a payout request for an eligible settlement period. TripMate validates the available payout amount and creates the request in PendingReview status for Staff review...

**Split in Direct Model:**

| Aspect | Legacy | Direct |
|--------|--------|--------|
| **UC name** | UC-46 Request Payout Settlement (legacy) | UC-75: Request Commission Payment (new) |
| **Data source** | `OperatorPayout` | `CommissionStatement` |
| **TO action** | Submit payout request | Submit commission payment request |
| **Amount** | Backend-calculated 70/90% | Backend-calculated 10% |
| **Payment method** | From TripMate payout gateway | Via payOS (TripMate channel) |
| **Staff review** | Approve/reject payout | Approve/reject commission payment |

**Decision:**
- Keep UC-46 for legacy
- Create UC-75 for direct commission settlement
- Separate endpoints: `/payout-requests` vs. `/commission-settlement-requests`

---

### UC-67: Export Statistical Reports — ✅ **CONFIRM: NOT REFUND**

**Current SRS (UC-67):**
> Allows an Administrator to generate and export platform-level statistical reports using data available across TripMate. Supported information may include booking statistics, popular POIs, rerouting incidents, user feedback, Tour Operator activity, or other approved system-level metrics...

**Analysis:**
- ✅ UC-67 = Statistics/reporting (confirmed in SRS)
- ❌ UC-67 ≠ Refund (conflict resolved)

**Direct refund flow uses NEW UC numbers:**
- UC-71: Confirm Direct Refund Transfer (Traveler confirms receipt)
- UC-72: Review Disputed Direct Refund (Admin resolves)

**Decision:**
- Do NOT modify UC-67 meaning
- Reference UC-67 for admin reporting only
- Refund UCs are NEW additions to SRS

---

### UC-70 & UC-71: Emergency Cancellation → Handoff to Direct Refund

**Current SRS (UC-70):**
> Triggered automatically by TripMate when incoming weather data indicates that an applicable tour is affected by a qualifying extreme-weather event... Any eligible refund is subsequently processed through UC-71 Process Automatic Refund.

**Current SRS (UC-71):**
> Triggered automatically by TripMate for an eligible cancelled booking according to the configured cancellation and refund policies. The system calculates or obtains the applicable refundable amount... sends the refund request to the external Payment Gateway...

**Split in Direct Model:**

| Aspect | Legacy | Direct |
|--------|--------|--------|
| **UC-70 trigger** | Emergency weather event (same) | Emergency weather event (same) |
| **UC-70 action** | Auto-cancel booking, create payout obligation | Auto-cancel booking, create direct refund obligation |
| **UC-71 processing** | Refund via TripMate payOS | Refund obligation handed to DirectRefundCreate |
| **Handoff** | `EmergencyRefundHandler` → `RefundProcessor` (legacy) | `EmergencyRefundHandler` → `DirectRefundCreate` (new) |
| **Filter logic** | IF `booking.paymentFlow == Legacy` | IF `booking.paymentFlow == Direct` |

**Decision:**
- Keep UC-70 & UC-71 names
- Handoff routing uses `paymentFlow`
- Legacy path: UC-70 → UC-71 → auto-refund
- Direct path: UC-70 → DirectRefundCreate → Traveler confirm 72h

---

## 3. Feature Flag Routing — Code Entry Points

### Location: Everywhere refund/payout is created

```csharp
// Template for all entry points

IF (booking.paymentFlow == PaymentFlow.PlatformCollectedLegacy)
{
  // Use legacy worker
  await RefundProcessor.ProcessAsync(booking, ...);
}
ELSE IF (booking.paymentFlow == PaymentFlow.OperatorDirect)
{
  // Use direct refund worker
  await DirectRefundCreate.ProcessAsync(booking, ...);
}
ELSE
{
  throw InvalidOperationException("Unknown payment flow");
}
```

### Entry Points (Must filter):

| File / Handler | Action | Legacy path | Direct path |
|---|---|---|---|
| `CancelBookingCommandHandler` | Traveler cancels tour | RefundProcessor | DirectRefundCreate |
| `EmergencyRefundHandler` | Weather event → auto-cancel | RefundProcessor | DirectRefundCreate |
| `ComplaintPartialRefundProposals` | Complaint → partial refund proposal | RefundProcessor (share calc) | DirectRefundCreate |
| `PayOsWebhookController` | Payment webhook | ProcessPayOsWebhookCommand (legacy) | ProcessPayOsWebhookCommand (direct route) |
| `ExportRevenueCommandHandler` | TO exports revenue | LegacyPayoutData | CommissionStatementData |
| `OperatorPayoutReport` | TO views revenue | LegacyPayout entity query | CommissionStatement entity query |

---

## 4. Database Separation

### Legacy Tables (do NOT modify, do NOT delete)
- `OperatorPayouts`
- `OperatorPayoutTransactions`
- `PaymentLinks` (legacy, legacy flow only)
- `Bookings` (payment-related columns: `PaidAtUtc`, `PaymentGatewayReference`, etc. — SHARED between legacy & direct)

### Direct Tables (new, Phase 1 migration)
- `OperatorPaymentChannels` (TO kênh)
- `DirectRefundCases` (refund obligation)
- `DirectRefundTransfers` (TO transfer report)
- `DirectRefundResponses` (Traveler confirm)
- `CommissionStatements` (monthly invoice)
- `CommissionStatementLines` (per-booking commission)

### Hybrid Columns in `Bookings` (MUST add)
- `PaymentFlow` (enum: `PlatformCollectedLegacy` = 0, `OperatorDirect` = 1)
  - Decision point for all business logic
  - Set at booking creation time
  - Never changes (immutable per booking)
- `ChannelVersionSnapshot` (if direct: foreign key to `OperatorPaymentChannels`, version number)
- `CommissionPolicySnapshot` (commission rate, rounding rule, effective date)

---

## 5. UC Conflict Resolution — UC-67

**Problem:**
- SRS Line 130: UC-67 = "Export Statistical Reports" (admin reporting)
- Old refund spec said UC-67 = "Confirm Refund" (conflict!)

**Resolution:**
✅ **UC-67 = Export Statistical Reports** (keep SRS meaning)

**Direct refund flow uses NEW UC numbers** (to be added to SRS):
- UC-71 (or UC-7x): Confirm Direct Refund Transfer Received (Traveler)
- UC-72 (or UC-7x): Review Disputed Direct Refund (Admin)

**Action:**
- When SRS is updated: add new UCs at end (UC-75+) or renumber cleanly
- Do NOT edit UC-67 meaning in SRS
- Create cross-reference table in SRS: "Direct Payment UCs (UC-7x to UC-8x)"

---

## 6. Separation Checklist

Before code for each phase, verify:

### Phase 0 (SRS alignment)
- [ ] All UC mappings documented (above)
- [ ] UC-67 conflict resolved (✅)
- [ ] Feature flag routing points listed (above)
- [ ] Legacy vs. direct table separation clear (above)
- [ ] SRS team will add new UCs (UC-71, UC-72, etc.)

### Phase 1 (Baseline & schema)
- [ ] `Bookings.PaymentFlow` column added (migration)
- [ ] Legacy tables: NO changes (verify backward compat)
- [ ] Direct tables: created with indices/unique keys
- [ ] Feature flag in code: IF paymentFlow == ... (all entry points)

### Phase 2-5 (Implementation)
- [ ] Legacy workers: ignore `OperatorDirect` bookings
- [ ] Direct workers: ignore `PlatformCollectedLegacy` bookings
- [ ] Webhook routing: per-channel (TO resolver)
- [ ] Export/reporting: separate by `paymentFlow`

### Phase 5+ (Cutover)
- [ ] Feature flag: OFF (default)
- [ ] No new direct bookings created until ready
- [ ] Legacy flow processes normally
- [ ] Ready to toggle ON when Phase 2-5 done + tested

---

## 7. SRS Update — Action Items

**When SRS is updated** (after this Task 1 approved):

1. **Add new UCs** (at end, UC-71 to UC-7x):
   - UC-71: Confirm Direct Refund Transfer (Traveler)
   - UC-72: Review Disputed Direct Refund (Admin)
   - UC-73: View Commission Statement (TO)
   - UC-74: Export Commission Statement (TO)
   - UC-75: Request Commission Settlement (TO)
   - (others as needed)

2. **Update existing UC descriptions** (clarify legacy/direct split):
   - UC-27: Add "(Legacy flow: Traveler pays TripMate; Direct flow: Traveler pays TO)"
   - UC-28: Add "(Webhook routing: per channel)"
   - UC-42: Add "(Legacy only; see UC-71 for direct refund flow)"
   - UC-46: Add "(Legacy payout settlement; see UC-75 for direct commission settlement)"

3. **Add feature flag note** at beginning of SRS Section 3:
   > "Direct Payment Model: Starting with Phase 0 (25/11/2026), TripMate supports TO direct payment model. By default, the legacy flow (TripMate holds money, payout 70/90% monthly) is active. The direct flow (TO receives payment directly, TripMate collects 10% commission monthly) is feature-flagged and disabled until Phase 2-5 implementation is complete and tested. All UC mappings between legacy and direct flows are documented in `docs/direct-payment-cutover.md`."

4. **Update screen authorization matrix** (add direct-only screens):
   - Admin Dispute Review screens (web only)
   - Traveler Direct Refund Confirm screens (web + mobile)
   - TO Commission Statement screens (web + mobile)

---

## 8. Handoff to SRS Team

**File to send:** This document (`docs/direct-payment-cutover.md`)

**Message to SRS team:**
> "Phase 0 Task 1 complete. This document maps legacy SRS flow to new direct payment flow. Use it to:
> 1. Add new UCs (UC-71+) for direct refund/commission flows
> 2. Clarify existing UCs (UC-27, 28, 42, 46) with feature flag notes
> 3. Update screen authorization matrix
> 4. Confirm UC-67 = Export Stats (not refund)
>
> Once SRS is updated, Phase 1 baseline can proceed."

---

## 9. References

- BE Spec: `../specs/direct-operator-payment-refund-spec.md` (sections 1-13)
- BE Plan: `../plans/direct-operator-payment-refund-plan.md`
- SRS: `Capstone_Docs/srs_latest_until_315.txt`
- Nhánh: `fix/linhnv-payout-and-refund`

---

## ✅ Task 1 Done Criteria

- [x] File created: `docs/direct-payment-cutover.md`
- [x] UC mapping (28, 42, 44, 45, 46, 67, 70, 71): legacy vs. direct
- [x] Feature flag routing points listed
- [x] Database separation (legacy vs. direct tables)
- [x] UC-67 conflict resolved
- [x] Handoff to SRS team (action items listed)
- [x] Commit to branch `fix/linhnv-payout-and-refund`

**Awaiting:** SRS team review + approval → Phase 1 start
