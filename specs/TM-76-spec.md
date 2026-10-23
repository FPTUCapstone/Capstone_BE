# TM-76 — UC-30 View Commercial Service Specification

Status: **APPROVED — 2026-10-03**

Jira: TM-76
Use case: UC-30 — View Commercial Service
Delivery: Backend API first, then Flutter Mobile integration. Web is explicitly out of scope.

## 1. Purpose and approved decisions

UC-30 lets a prospective traveler discover commercial travel services before booking: vehicle rental, hotel rooms, and restaurant offers.

Approved decisions:

1. The catalog is real database-backed data, not a Mobile fixture or an external-provider proxy.
2. Guest and authenticated Traveler callers may browse the catalog and service detail.
3. UC-30 is read-only. Booking, payment, coupon application, cancellation, refund, provider API calls, and provider administration remain separate use cases.
4. A future booking call-to-action belongs to UC-31. It must not claim a booking was made in UC-30.
5. Only an `Active` provider with an `Available` service is discoverable.

## 2. Existing persistence source of truth

SQL schema v7 already owns the commercial catalog:

- `commercial.ServiceProviders` — provider identity, category, contact data, status;
- `commercial.Services` — bookable catalog item, price, capacity, attributes, availability; and
- `commercial.ServiceClickLogs` — optional later click/affiliate telemetry.

The existing schema needs a small, explicit idempotent SQL upgrade so a displayed offer is truthful rather than a generic placeholder. The upgrade adds service-level currency, price disclosure, cover image, fulfilment location, pickup instructions, cancellation summary, and freshness fields. It does not add a booking table or external-provider credential.

The public contract must never expose `ServiceProviders.api_endpoint`, `commission_rate`, booking records, click logs, or other internal/provider-only data.

### 2.1 Commercial service disclosure fields

`commercial.Services` gains the following nullable/required fields through an idempotent SQL migration and matching canonical schema update:

| Field | Rule and user value |
| --- | --- |
| `currency_code` | Required ISO-4217 code; this release seeds and permits `VND` only. Mobile never infers a currency. |
| `price_includes_tax` | Required boolean. UI says either “Đã gồm thuế/phí” or “Chưa gồm thuế/phí”. |
| `refundable_deposit_amount` | Optional non-negative VND amount. Null means no deposit is disclosed, not that a deposit is guaranteed absent. |
| `cover_image_url` | Optional vetted HTTPS image URL. Missing media uses a category-specific neutral placeholder, never an invented provider image. |
| `fulfilment_location_label` | Optional human-readable pickup, check-in, or venue label. It complements—not replaces—`poi_id`. |
| `pickup_or_arrival_instructions` | Optional concise instructions, maximum 1,000 characters. |
| `cancellation_policy_summary` | Optional concise policy summary, maximum 500 characters. |
| `last_updated_at` | Required UTC `datetime2`; shown to the user as “Last updated” and maintained whenever catalog availability/disclosure data changes. |

All new monetary fields require SQL non-negative `CHECK` constraints. All UTC timestamps use the repository `AsUtcDateTime2()` mapping.

## 3. API contract

### 3.1 List services

`GET /api/v1/commercial-services`

Authorization: `[AllowAnonymous]`; authenticated callers receive the same catalog response.

| Query parameter | Type | Default | Rule |
| --- | --- | --- | --- |
| `category` | string? | null | `Vehicle`, `Hotel`, or `Restaurant`; invalid values return 400. |
| `search` | string? | null | Trimmed case-insensitive contains search on service and provider name; maximum 200 characters. |
| `page` | int | 1 | Must be at least 1. |
| `pageSize` | int | 20 | Must be from 1 through 100. |

The response is `200 OK`, including for an empty result:

```json
{
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1,
  "items": [
    {
      "id": 10,
      "category": "Vehicle",
      "name": "Honda Wave 110cc",
      "providerName": "Da Nang Ride",
      "description": "Automatic city scooter rental.",
      "priceAmount": 180000.00,
      "currencyCode": "VND",
      "priceUnit": "PerDay",
      "priceIncludesTax": true,
      "refundableDepositAmount": 500000.00,
      "capacity": 2,
      "attributes": { "transmission": "Automatic", "seats": 2, "licenseRequired": true },
      "coverImageUrl": null,
      "poiId": null,
      "fulfilmentLocationLabel": "Da Nang Ride — 25 Tran Phu, Hai Chau",
      "lastUpdatedAtUtc": "2026-10-03T02:00:00Z"
    }
  ]
}
```

Ordering is deterministic: category, service name, then service ID ascending. Filtering, count, projection, ordering, and paging occur in SQL; no complete catalog may be materialized in memory.

### 3.2 Service detail

`GET /api/v1/commercial-services/{id}`

Authorization: `[AllowAnonymous]`.

`id` must be a positive integer. A successful `200 OK` response contains every list-item field plus provider ID, provider name, contact email and phone when present, service description, capacity, attributes, related POI ID, fulfilment location, pickup/arrival instructions, and cancellation-policy summary.

The response states that displayed availability was last updated at `lastUpdatedAtUtc` and must be confirmed at booking. It never promises a reservation or a final total; UC-31 rechecks availability, price, tax/fees, deposit, and cancellation rules before booking.

Inactive providers and unavailable/missing services are intentionally indistinguishable and return `404 Not Found` with error code `CommercialService.NotFound`.

## 4. Failure behavior

| Condition | HTTP | Behavior |
| --- | --- | --- |
| Invalid category, search length, page, page size, or route ID | 400 | Standard RFC-7807 validation response. |
| No matching active/available service | 200 | Empty paginated page. |
| Missing, unavailable, or inactive-provider service | 404 | `CommercialService.NotFound`; no internal status leak. |
| Unexpected system failure | 500 | Existing generic ProblemDetails, without internal details. |

## 5. Backend architecture

The Backend follows the existing Controller → MediatR query/validator/handler → `IApplicationDbContext` pattern:

- map minimal read-only `ServiceProvider` and `CommercialService` entities/configurations;
- add `DbSet`s and schema mappings using explicit table/schema names;
- use `AsNoTracking` and response projections;
- add a versioned API controller with `[AllowAnonymous]` and typed success results; and
- keep business filtering and visibility rules in Application handlers, never in the controller.

The feature uses constants for categories, availability and provider status. It validates a category-specific attribute allow-list before emitting attributes:

- Vehicle: `transmission`, `seats`, `licenseRequired`, `luggageCapacity`;
- Hotel: `roomType`, `beds`, `checkInTime`, `checkOutTime`; and
- Restaurant: `cuisine`, `servingSize`, `reservationType`.

Unknown or malformed attributes are omitted from the public response rather than rendered as raw JSON. The feature must not introduce a generic repository, a generic catalog framework, an external-provider integration, or write telemetry.

## 6. Mobile behavior

UC-30 is Mobile-only. Flutter provides a public list screen with category filters, debounced name search, paged loading, loading/empty/error states, and retry; and a public detail screen with provider, cover image or neutral category placeholder, VND price/unit, tax/fee disclosure, deposit, structured attributes, related venue context, fulfilment location, policy, freshness, and contact information when present.

A non-booking continuation is allowed: signed-in Traveler may see a clearly deferred UC-31 entry point; Guest sees a sign-in prompt only when choosing that future booking action. The app must never create or simulate a booking in UC-30.

The Mobile client follows Page → Cubit → use case → repository → data source. It maps HTTP 404 to a neutral unavailable-service state and never exposes raw HTTP errors.

## 7. Seed catalog

Development seed data contains representative active providers and available services for all three categories. Names, prices, units, tax/deposit disclosures, capacity, vetted image URLs, location labels, cancellation summaries, and typed attributes must be plausible and clearly local development catalog data. The seed is idempotent and does not overwrite a user-managed catalog.

For production, Platform Operations owns catalog accuracy through a controlled manual/import process until a provider integration is separately approved. Seed data is not represented as live partner inventory.

## 8. Acceptance criteria

1. Guest can list and view active, available commercial services without a token.
2. Authenticated Traveler receives the same public catalog response.
3. Vehicle, Hotel, and Restaurant category filters return only their category.
4. Search, paging, total metadata, and order are deterministic.
5. Inactive provider and unavailable service never appear in a list and are 404 in detail.
6. Public DTOs disclose VND price, tax/fee status, any disclosed deposit, fulfilment location, terms, and freshness without leaking API endpoints, commission rates, booking data, or click logs.
7. The Mobile list/detail uses real Backend data and presents loading, empty, unavailable, and retry states.
8. UC-30 creates no booking, payment, coupon, cancellation, refund, or click-log record.
9. Existing schema is extended only by the approved idempotent SQL migration and matching canonical schema update; no EF migration is added.

## 9. Verification requirements

Backend tests cover query validation, anonymous access, authenticated compatibility, active/available visibility, category/search filtering, stable SQL pagination, empty list, 404 concealment, disclosure fields, raw-attribute omission, DTO field exclusion, migration upgrade/re-run behavior, and SQL Server mapping/query translation.

Mobile tests cover list/detail rendering, filters/search, pagination, guest access, unavailable-service 404 state, retry, and no booking mutation. Final verification requires Backend build/test, Mobile format/analyze/test, SQL Server integration tests, and a manual device/emulator smoke test.

## 10. Explicit exclusions

- UC-31 booking creation and provider confirmation;
- UC-38 coupon application;
- UC-41 cancellation and UC-42 refund;
- payment, payout, provider API credentials, external synchronization, affiliate click logging, and automated provider inventory synchronization;
- Next.js/Web screens; and
- administrator CRUD for providers/services.

## 11. Approval gate

This specification must be reviewed and approved before the implementation plan, feature worktrees, or implementation tests are created. Any contract change above requires spec revision and approval.
