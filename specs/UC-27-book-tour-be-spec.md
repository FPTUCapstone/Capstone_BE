# UC-27 Backend Specification — Book Tour

**Feature:** UC-27 Book Tour  
**Platform:** Backend / Spring Boot  
**Status:** Draft — awaiting team approval  
**Contract basis:** SRS 3.5.4 + Mobile/FE client specifications  

---

## 1. Purpose

UC-27 lets an authenticated Traveler create a booking for a selected departure schedule of a tour package. The Backend:
1. Validates availability (slots, schedule, account status)
2. Computes pricing (unit price, discount, total) per BR-63
3. Validates participant and voucher data
4. Creates booking in `PendingPayment` status
5. Records an idempotency key to prevent duplicate bookings

---

## 2. Scope & Exclusions

### Supported (BE Responsibility)

- **Tour Schedule Availability:** Check remaining slots, schedule status (Active/Closed/Cancelled)
- **Pricing Computation:** Fetch base price, apply voucher discount, compute subtotal/total (BR-63)
- **Participant Validation:** Format validation only (DOB, identity doc format, phone format) — not business logic
- **Voucher Eligibility:** Check code exists, not expired, not exhausted, meets minimum purchase
- **Booking Creation:** Insert booking record, generate booking code, set status = PendingPayment, set payment expiry
- **Idempotency Handling:** Detect/reject duplicate requests via Idempotency-Key
- **Concurrency Control:** Enforce "max N unpaid bookings per Traveler" (business rule)
- **Notification:** Emit event for successful booking (SMS/email → separate service)

### Out of Scope

- **Payment Processing** → UC-28 (external gateway)
- **E-ticket Generation** → UC-29
- Tour Operator or Administrator workflows
- Refund calculation (handled by UC-41, UC-42)
- Check-in logistics (UC-43)

---

## 3. API Endpoints

### 3.1 Price Quote / Preview

**Endpoint:**
```http
POST /api/v1/traveler/tour-bookings/quote
Authorization: Bearer <access-token>
Content-Type: application/json
```

**Purpose:** Allow clients to preview pricing before confirming booking. Used on participant count change, voucher apply/remove.

**Request:**
```json
{
  "tourScheduleId": 123,
  "numberOfParticipants": 2,
  "voucherCode": "SUMMER2026" (optional, null if no voucher)
}
```

**Response (HTTP 200):**
```json
{
  "tourScheduleId": 123,
  "numberOfParticipants": 2,
  "pricePerParticipant": 1000000,
  "subtotalAmount": 2000000,
  "discountAmount": 200000,
  "totalAmount": 1800000,
  "currency": "VND",
  "appliedVoucher": {
    "code": "SUMMER2026",
    "discountPercentage": 10,
    "discountAmount": 200000
  },
  "remainingSlots": 5
}
```

**Error Responses:**
| HTTP | Error Code | SRS Msg | Condition |
|------|-----------|---------|-----------|
| 400  | INVALID_SCHEDULE | MSG65 | Schedule not found or inactive |
| 400  | INSUFFICIENT_SLOTS | MSG77 | Participants > remaining slots |
| 400  | INVALID_VOUCHER | MSG101 | Voucher code invalid/expired/exhausted |
| 400  | VOUCHER_MINIMUM_NOT_MET | MSG102 | Subtotal < voucher minimum spend |
| 401  | UNAUTHORIZED | N/A | Token invalid/expired |
| 403  | FORBIDDEN | N/A | Non-Traveler role |
| 5xx  | SERVER_ERROR | MSG127 | Internal server error |

---

### 3.2 Create Booking

**Endpoint:**
```http
POST /api/v1/traveler/tour-bookings
Authorization: Bearer <access-token>
Idempotency-Key: <UUID, e.g., "550e8400-e29b-41d4-a716-446655440000">
Content-Type: application/json
```

**Purpose:** Create a booking for the Traveler with Pending Payment status.

**Request:**
```json
{
  "tourScheduleId": 123,
  "numberOfParticipants": 2,
  "participants": [
    {
      "fullName": "John Doe",
      "dateOfBirth": "1990-01-15",
      "identityDocumentNumber": "123456789",
      "phoneNumber": "+84912345678"
    },
    {
      "fullName": "Jane Doe",
      "dateOfBirth": "1992-06-20",
      "identityDocumentNumber": "987654321",
      "phoneNumber": "+84987654321"
    }
  ],
  "contactInformation": {
    "fullName": "John Doe",
    "email": "john@example.com",
    "phoneNumber": "+84912345678"
  },
  "voucherCode": "SUMMER2026" (optional, null if no voucher)
}
```

**Validations (Backend MUST perform):**

1. **Traveler Status:** Account must be Active, not locked
2. **Schedule Availability:** Schedule exists, status = Active, departure date ≥ today + N hours (business rule)
3. **Capacity Check:** `numberOfParticipants ≤ remainingSlots` (revalidate at booking time)
4. **Participant Data:** All fields non-null, format valid (date format, phone format, ID length)
5. **Contact Data:** Email format valid, phone format valid
6. **Voucher (if provided):**
   - Code exists in database
   - Status = Active (not expired)
   - Remaining uses > 0
   - Subtotal ≥ minimum spend
   - Voucher applicable to this tour (if tour-specific)
7. **Concurrency:** `count(bookings where status IN (PendingPayment, AwaitingConfirmation) AND traveler_id = X) < MAX_UNPAID_BOOKINGS` (e.g., MAX=3, per BR-XX)
8. **Idempotency:** If `Idempotency-Key` was seen before within 24h and booking was created, return cached booking (200 OK); if request differs, reject (422 Conflict)

**Response (HTTP 201 Created):**
```json
{
  "bookingId": 456,
  "bookingCode": "BK-20261015-001",
  "status": "PendingPayment",
  "tourScheduleId": 123,
  "tourName": "Hanoi City Tour",
  "departureDate": "2026-10-20T08:00:00Z",
  "returnDate": "2026-10-22T18:00:00Z",
  "numberOfParticipants": 2,
  "participants": [
    {
      "fullName": "John Doe",
      "dateOfBirth": "1990-01-15",
      "identityDocumentNumber": "123456789",
      "phoneNumber": "+84912345678"
    },
    {
      "fullName": "Jane Doe",
      "dateOfBirth": "1992-06-20",
      "identityDocumentNumber": "987654321",
      "phoneNumber": "+84987654321"
    }
  ],
  "contactInformation": {
    "fullName": "John Doe",
    "email": "john@example.com",
    "phoneNumber": "+84912345678"
  },
  "subtotalAmount": 2000000,
  "discountAmount": 200000,
  "totalAmount": 1800000,
  "currency": "VND",
  "appliedVoucher": {
    "code": "SUMMER2026",
    "discountPercentage": 10,
    "discountAmount": 200000
  },
  "paymentExpiryTime": "2026-10-15T10:30:00Z",
  "createdAt": "2026-10-15T09:30:00Z"
}
```

**Error Responses:**
| HTTP | Error Code | SRS Msg | Condition |
|------|-----------|---------|-----------|
| 400  | INVALID_SCHEDULE | MSG65 | Schedule not found or inactive |
| 400  | INSUFFICIENT_SLOTS | MSG77 | Participants > remaining slots |
| 400  | INVALID_PARTICIPANT | MSG78 | Participant info incomplete/invalid format |
| 400  | INVALID_VOUCHER | MSG101 | Voucher code invalid/expired/exhausted |
| 400  | VOUCHER_MINIMUM_NOT_MET | MSG102 | Subtotal < voucher minimum spend |
| 400  | TOO_MANY_UNPAID_BOOKINGS | MSG126 | Traveler has ≥ MAX_UNPAID_BOOKINGS pending |
| 401  | UNAUTHORIZED | N/A | Token invalid/expired |
| 403  | FORBIDDEN | N/A | Non-Traveler role |
| 409  | CONFLICT | N/A | Idempotency-Key collision with different request body |
| 422  | UNPROCESSABLE_ENTITY | N/A | Business rule violation (e.g., schedule not yet open for booking) |
| 5xx  | SERVER_ERROR | MSG127 | Internal server error |

---

## 4. Business Rules & Validation

### BR-63: Price Computation (Client Must Not Compute)

- Backend ALWAYS computes: subtotal = numberOfParticipants × pricePerParticipant
- Backend ALWAYS applies discount (if voucher valid)
- Total = subtotal - discount
- **Client must never compute or trust client-computed amounts.** Quote endpoint re-validates on each call.

### Booking Code Generation

- Format: `BK-YYYYMMDD-XXXXX` (e.g., `BK-20261015-00001`)
- Uniqueness: Database constraint on (tourScheduleId, bookingCode)
- Increment: Per-schedule counter or distributed ID generator

### Payment Expiry

- Calculated: `createdAt + PAYMENT_EXPIRY_MINUTES` (e.g., 15 minutes per SRS)
- Stored in booking record
- Used by UC-28 (Make Electronic Payment) — if payment attempt > expiry, reject with MSG119

### Idempotency-Key Handling

- Header: `Idempotency-Key: <UUID>`
- Store in database: table `idempotency_keys(key UUID, booking_id INT, created_at TIMESTAMP, request_hash VARCHAR, response_hash VARCHAR)`
- Logic:
  - If key exists AND request hash matches → return cached response (200 OK)
  - If key exists AND request hash differs → return 409 Conflict
  - If key not seen → create booking, store key + response, return 201 Created

### Concurrency & Slot Management

- Slots are soft-reserved at booking creation, hard-reserved at payment confirmation
- If slot capacity changes after booking creation but before payment, subsequent operations fail gracefully
- No overbooking: always check `remainingSlots ≥ numberOfParticipants` at booking time

---

## 5. Database Schema Extensions

```sql
-- New/Modified tables for UC-27

CREATE TABLE tour_bookings (
  booking_id BIGINT PRIMARY KEY AUTO_INCREMENT,
  booking_code VARCHAR(50) UNIQUE,
  traveler_id BIGINT NOT NULL,
  tour_schedule_id BIGINT NOT NULL,
  status ENUM('PendingPayment', 'AwaitingConfirmation', 'Confirmed', 'Cancelled', 'Expired'),
  number_of_participants INT NOT NULL,
  subtotal_amount DECIMAL(15,2),
  discount_amount DECIMAL(15,2) DEFAULT 0,
  total_amount DECIMAL(15,2),
  currency VARCHAR(3) DEFAULT 'VND',
  applied_voucher_id BIGINT,
  payment_expiry_time DATETIME,
  created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  FOREIGN KEY (traveler_id) REFERENCES traveler(id),
  FOREIGN KEY (tour_schedule_id) REFERENCES tour_schedule(id),
  FOREIGN KEY (applied_voucher_id) REFERENCES coupon(id),
  INDEX idx_traveler_status (traveler_id, status),
  INDEX idx_schedule (tour_schedule_id)
);

CREATE TABLE tour_booking_participants (
  participant_id BIGINT PRIMARY KEY AUTO_INCREMENT,
  booking_id BIGINT NOT NULL,
  participant_index INT,
  full_name VARCHAR(255) NOT NULL,
  date_of_birth DATE NOT NULL,
  identity_document_number VARCHAR(50),
  phone_number VARCHAR(20),
  created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (booking_id) REFERENCES tour_bookings(booking_id) ON DELETE CASCADE,
  INDEX idx_booking (booking_id)
);

CREATE TABLE tour_booking_contact_info (
  contact_id BIGINT PRIMARY KEY AUTO_INCREMENT,
  booking_id BIGINT UNIQUE NOT NULL,
  full_name VARCHAR(255) NOT NULL,
  email VARCHAR(255),
  phone_number VARCHAR(20),
  created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (booking_id) REFERENCES tour_bookings(booking_id) ON DELETE CASCADE
);

CREATE TABLE idempotency_keys (
  idempotency_key_id BIGINT PRIMARY KEY AUTO_INCREMENT,
  key_value VARCHAR(255) UNIQUE NOT NULL,
  traveler_id BIGINT,
  booking_id BIGINT,
  request_hash VARCHAR(255),
  response_hash VARCHAR(255),
  created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
  expires_at DATETIME,
  FOREIGN KEY (traveler_id) REFERENCES traveler(id),
  FOREIGN KEY (booking_id) REFERENCES tour_bookings(booking_id),
  INDEX idx_key (key_value),
  INDEX idx_expires (expires_at)
);
```

---

## 6. Event & Integration

### Events to Emit

1. **BookingCreated** → consumed by:
   - Notification service (SMS/Email: booking confirmation, payment reminder)
   - Analytics service
   - Inventory management (reserve slots)

2. **BookingExpired** → (async, if payment not completed within expiry)

### Integration Points

- **Tour Schedule API:** Fetch schedule details, remaining slots, pricing
- **Voucher/Coupon API:** Validate coupon, fetch discount
- **Notification Service:** Send booking confirmation email/SMS
- **Payment Gateway:** UC-28 integration (separate)

---

## 7. Error Codes & Messages Mapping

| Error Code | HTTP | SRS Msg | Frontend Safe Message |
|-----------|------|---------|------------------------|
| INVALID_SCHEDULE | 400 | MSG65 | "This tour departure is no longer available" |
| INSUFFICIENT_SLOTS | 400 | MSG77 | "Not enough slots available for this departure" |
| INVALID_PARTICIPANT | 400 | MSG78 | "Participant information is invalid; check date of birth and ID format" |
| INVALID_VOUCHER | 400 | MSG101 | "Voucher code is invalid, expired or fully used" |
| VOUCHER_MINIMUM_NOT_MET | 400 | MSG102 | "Voucher requires a minimum purchase; remove it or add more participants" |
| TOO_MANY_UNPAID_BOOKINGS | 400 | MSG126 | "You have too many pending bookings; complete payment or cancel one to proceed" |
| SERVER_ERROR | 5xx | MSG127 | "The service is temporarily unavailable; please try again later" |
| UNAUTHORIZED | 401 | N/A | "Please sign in again" |
| FORBIDDEN | 403 | N/A | "This action is only available to Traveler accounts" |

---

## 8. Testing Strategy

### Unit Tests

- Pricing computation (various voucher scenarios)
- Idempotency key logic
- Validation logic (participant data, voucher eligibility)

### Integration Tests

- End-to-end booking creation with quote → booking flow
- Concurrent booking attempts (slot collision)
- Idempotency key duplicate detection

### Load Testing

- High-volume bookings for same schedule (slot exhaustion)
- Payment expiry processing (async job)

---

## 9. Performance Considerations

- Indices on `(traveler_id, status)`, `(tour_schedule_id)`, `(idempotency_key)`
- Slot availability cached with short TTL (30s) to reduce DB queries
- Quote endpoint optimized (no create, just read + compute)

---

## Approval Gates

- [ ] Backend team confirms API endpoint names, field names, error codes
- [ ] Database schema approved
- [ ] Idempotency key strategy confirmed
- [ ] Max unpaid bookings business rule documented
- [ ] Payment expiry duration confirmed (currently 15 min)
- [ ] Integration with tour schedule & coupon services confirmed
