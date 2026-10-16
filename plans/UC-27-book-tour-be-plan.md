# UC-27 Backend Implementation Plan — Book Tour

**Feature:** UC-27 Book Tour  
**Platform:** Spring Boot  
**Status:** Draft  
**Specification:** `specs/UC-27-book-tour-be-spec.md`  

---

## 1. Delivery Boundary

This plan delivers:
- `POST /api/v1/traveler/tour-bookings/quote` endpoint (price preview)
- `POST /api/v1/traveler/tour-bookings` endpoint (booking creation)
- Database schema for bookings, participants, contact info, idempotency keys
- Validation logic, BR-63 pricing, idempotency handling

Does NOT include:
- Payment processing (UC-28 → separate feature)
- E-ticket generation (UC-29 → separate feature)
- Booking refund logic (UC-41/42 → separate features)
- Async notification service (separate service)

---

## 2. Critical Blockers — MUST RESOLVE FIRST

### ⚠️ P0: External Dependencies

1. **Tour Schedule Service**
   - API: GET `/schedules/{id}` → returns remaining slots, pricing, status
   - Must provide: `pricePerParticipant`, `remainingSlots`, `status`, `departureDate`
   - **Confirm API contract, error codes**

2. **Coupon/Voucher Service**
   - API: POST `/vouchers/validate` → validates coupon eligibility
   - Must return: `discountPercentage`, `discountAmount`, `minimumSpend`, `status`
   - **Confirm API contract before implementation**

3. **Database Migrations**
   - Team must review & approve schema before running
   - New tables: `tour_bookings`, `tour_booking_participants`, `tour_booking_contact_info`, `idempotency_keys`

### ⚠️ P1: Architecture Decisions

1. **Idempotency Key Storage**
   - Option A: Separate table `idempotency_keys` (recommended for audit trail)
   - Option B: Column on `tour_bookings` (simpler but less flexible)
   - **Decision needed** before Task 1

2. **Payment Expiry Processing**
   - Option A: Async scheduled job (every 5 min, mark expired bookings)
   - Option B: Lazy check (at payment time, return 410 Gone if expired)
   - **Decision needed** for Task 5

3. **Concurrency & Slot Management**
   - Soft vs. hard slot reservation timing?
   - When are slots locked: at booking creation or payment confirmation?
   - **Clarify with product team**

---

## 3. Task Breakdown

### Task 1: Domain Models & Entities

**Files to create:**
- `src/main/java/domain/entities/TourBooking.java`
- `src/main/java/domain/entities/TourBookingParticipant.java`
- `src/main/java/domain/entities/ContactInfo.java`
- `src/main/java/domain/entities/BookingStatus.java` (enum)
- `src/main/java/domain/dto/CreateBookingRequest.java`
- `src/main/java/domain/dto/BookingResponse.java`
- `src/main/java/domain/dto/QuoteResponse.java`

**Key Points:**
- TourBooking: read-only computed fields (subtotal, discount, total, paymentExpiryTime)
- BookingCode generation logic
- Validation annotations (non-null, email format, phone format)

**Timeline:** 1 day  
**Verify:** Compile, no errors

---

### Task 2: Database Migration & Schema

**Files:**
- `src/main/resources/db/migration/V20261017001__create_booking_tables.sql`
- Indices on `(traveler_id, status)`, `(tour_schedule_id)`, `(idempotency_key)`
- Foreign keys to `traveler`, `tour_schedule`, `coupon`

**Timeline:** 1 day  
**Verify:** Migration runs successfully, schema matches spec

---

### Task 3: Repository & Data Access

**Files:**
- `src/main/java/repositories/TourBookingRepository.java` (Spring Data JPA)
- `src/main/java/repositories/IdempotencyKeyRepository.java`
- Custom queries:
  - `countUnpaidBookingsByTraveler(traveler_id)` → for MAX_UNPAID_BOOKINGS validation
  - `findByIdempotencyKey(key)` → for deduplication
  - `findByTravelerAndStatus(...)` → for list operations

**Timeline:** 1 day  
**Verify:** Unit tests for repository methods (no DB integration yet)

---

### Task 4: Validation & Business Logic Service

**Files:**
- `src/main/java/services/BookingValidationService.java`
  - `validateScheduleAvailability(scheduleId)`
  - `validateParticipantData(participants)` (format only, not business rules)
  - `validateTravelerEligibility(travelerId)`
  - `validateVoucherEligibility(voucherCode, subtotal)`
  - `validateConcurrency(travelerId)` → count unpaid bookings

- `src/main/java/services/PricingService.java`
  - `calculateQuote(scheduleId, numberOfParticipants, voucherCode)` → calls Coupon Service + Schedule Service
  - Returns: subtotal, discount, total (never trusts client input)

**Timeline:** 2 days  
**Verify:** Unit tests with mocked external services

---

### Task 5: IdempotencyKey Handling & Deduplication

**Files:**
- `src/main/java/services/IdempotencyService.java`
  - `processIdempotencyKey(key, requestBody, responseBody)` → store/retrieve/validate
  - `detectCollision(key, currentRequestHash)` → return 409 if mismatch

- `src/main/java/interceptors/IdempotencyInterceptor.java`
  - Extract header, generate request hash (MD5/SHA256 of body)
  - Check for cached response, return early if found
  - Store response after endpoint execution

**Timeline:** 1.5 days  
**Verify:** Unit tests for deduplication logic

---

### Task 6: REST Endpoints (Quote & Create Booking)

**Files:**
- `src/main/java/controllers/TourBookingController.java`
  - `POST /api/v1/traveler/tour-bookings/quote` → calls PricingService
  - `POST /api/v1/traveler/tour-bookings` → orchestrates validation, creates booking, returns response
  - Error handling: translate service exceptions → HTTP status + error codes (MSG65, MSG77, etc.)

**Timeline:** 2 days  
**Verify:** Integration tests, postman collection

---

### Task 7: Event Publishing & Integration

**Files:**
- `src/main/java/events/BookingCreatedEvent.java`
- `src/main/java/publishers/BookingEventPublisher.java`
- Integration with external services (Notification, Analytics)

**Timeline:** 1 day  
**Verify:** Event is emitted on booking creation

---

### Task 8: Integration Tests & Documentation

**Files:**
- `src/test/java/integration/TourBookingIntegrationTest.java`
  - End-to-end quote → booking flow
  - Error scenarios (insufficient slots, invalid voucher, etc.)
  - Idempotency key duplicate detection
  - Concurrent booking attempts

- OpenAPI/Swagger documentation update

**Timeline:** 2 days  
**Verify:** All integration tests pass, API docs accurate

---

## 4. Dependencies & Assumptions

| Dependency | Status | Owner |
|-----------|--------|-------|
| Tour Schedule Service API | TBD | Backend Team |
| Coupon/Voucher Service API | TBD | Backend Team |
| Database permissions | TBD | DevOps |
| Message broker (for events) | Existing | — |

---

## 5. Risks & Mitigation

| Risk | Severity | Mitigation |
|------|----------|-----------|
| Slot race condition (concurrent bookings exceed capacity) | High | Pessimistic locking or database-level check constraint |
| Payment expiry job delays (bookings not marked expired) | Medium | Monitor job execution, alert if skipped > 10 min |
| Idempotency key collision (different requests, same UUID) | Low | Request hash comparison, return 409 Conflict |
| External service downtime (Schedule/Coupon APIs unavailable) | Medium | Circuit breaker pattern, fallback to cached prices |

---

## 6. Testing Strategy

```
Task 1: Unit tests for DTOs, entity validation
Task 2: Migration verification
Task 3: Repository unit tests (mocked DB)
Task 4: Service unit tests (mocked external services)
Task 5: Idempotency logic unit tests
Task 6: Integration tests (real DB, mock external services)
Task 7: Event publishing unit tests
Task 8: End-to-end integration tests + API contract tests
```

---

## 7. Effort Estimate

| Task | Days | Notes |
|------|------|-------|
| Task 1 (Models) | 1 | Straightforward |
| Task 2 (DB Migration) | 1 | Requires schema review |
| Task 3 (Repository) | 1 | Spring Data JPA standard |
| Task 4 (Validation & Pricing) | 2 | Depends on external service contracts |
| Task 5 (Idempotency) | 1.5 | Key complexity is deduplication logic |
| Task 6 (Endpoints) | 2 | Error handling, status code mapping |
| Task 7 (Events) | 1 | Standard Spring event publishing |
| Task 8 (Tests & Docs) | 2 | Integration test coverage critical |
| **TOTAL** | **11.5 days** | ~2 weeks (with code review, fixes) |

---

## 8. Execution Order

1. **Approve Spec + Blockers** (Day 0)
2. **Task 1** → Task 2 → Task 3 (parallel) (Days 1-2)
3. **Task 4** → Task 5 (Days 3-4)
4. **Task 6** (Day 5-6)
5. **Task 7** (Day 7)
6. **Task 8** (Days 8-9)
7. **Code review + fixes** (Days 10-11)

---

## 9. Definition of Done

- [ ] Spec approved by team
- [ ] All 8 tasks implemented
- [ ] Integration tests pass (90%+ coverage)
- [ ] Error codes map to SRS messages (MSG65, MSG77, etc.)
- [ ] OpenAPI docs generated & accurate
- [ ] Manual testing with Postman collection
- [ ] No P1/P2 code review findings
- [ ] Ready for FE/Mobile integration testing

---

## Approval Gates

- [ ] Specification approved
- [ ] External service API contracts confirmed (Tour Schedule, Coupon)
- [ ] Idempotency & concurrency strategy approved
- [ ] Database schema approved
- [ ] Team capacity confirmed for 11.5-day effort
