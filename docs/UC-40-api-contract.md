# UC-40 Backend API contract — View Customer Bookings

Status: **implemented on `feature/uc40-view-customer-bookings` (not merged yet).** Authority:
`specs/UC-40-view-customer-bookings-spec.md` (Approved 2026-10-27), Report 3 §3.8.4.1. For Capstone_FE.
Web only: the SRS defines this screen on the Next.js application, not on mobile.

## What changes for the FE

The operator bookings feature (`src/features/operator/bookings`) currently runs on demo fixtures and
answers `PENDING_BE_INTEGRATION` outside demo mode. Point it at these endpoints and adapt the models
to the BE contract (the BE enums are the source of truth, no migration was needed):

| FE mock today | Real contract |
| --- | --- |
| `BookingStatus` = `Confirmed / PendingPayment / Cancelled / Completed` | adds **`Expired`** |
| `contactName`, `contactEmail`, `contactPhone` | `booker.fullName`, `booker.email`; `booker.phoneNumber` on detail only |
| `departureDate` | `schedule.startAtUtc` (UTC; display in `Asia/Ho_Chi_Minh`, `dd/MM/yyyy`, CR-07) |
| `participantsCount` | `quantity` |
| `createdAt` | `bookedAtUtc` |
| `checkInStatus: NotCheckedIn / CheckedIn` | same values, plus `checkedInAtUtc` (nullable) |
| `paymentTransaction.paymentChannel = VNPay / PayOS` | `payment.gateway` = `Mock / PayOS / VNPAY / MoMo / Stripe` |
| `participants[].age`, `participants[].specialNotes` | **do not exist** on BE: remove or hide. Participants carry `fullName` and `participantType` (`Adult` / `Child` / `Infant`) only |
| `cancellationWindowExpired` | **do not exist.** Use `canCancel` and `cancelBlockedReason` |
| `paidAmount` | `paidAmount` (also returned by BE) |
| `qrTicketValid` | not returned; use `ticket.status` |
| ids are strings | ids are JSON **strings** (same as the admin platform bookings list) |

`MSG82` in the FE mock ("cancellation deadline passed") is not an SRS message: in the SRS it is the
cancel-success toast. UC-41 as built applies no time window to a Tour Operator.

## Auth

`Authorization: Bearer <operator token>`. Role `TourOperator`, account `Active`, application
`Approved`. A different role gets `403`. The operator only ever sees bookings on tours it owns (BR-105).

## Endpoints

| Method and path | Use |
| --- | --- |
| `GET /api/v1/operator/bookings` | Customer Bookings list with filters, summary and pagination. |
| `GET /api/v1/operator/bookings/{bookingId}` | Booking detail panel. |

### `GET /api/v1/operator/bookings`

Query parameters, all optional:

| Name | Notes |
| --- | --- |
| `tourId`, `scheduleId` | positive integers. A tour or schedule the operator does not own (or that does not exist) gives `403`. |
| `status` | `PendingPayment`, `Confirmed`, `Cancelled`, `Completed`, `Expired` |
| `paymentStatus` | `Unpaid`, `Paid`, `Refunded`, `PartiallyRefunded` |
| `departureFrom`, `departureTo` | `YYYY-MM-DD`, Vietnam-time calendar days, both inclusive, based on the schedule start. `departureFrom` after `departureTo` gives `400`. |
| `keyword` | up to 200 characters; matches booking code, booker full name or booker e-mail (case-insensitive, wildcards are literal). |
| `pageNumber` | default `1` |
| `pageSize` | default `20`, maximum `100` |

Ordering is fixed: newest `bookedAtUtc` first, then highest booking id.

```json
{
  "pageNumber": 1,
  "pageSize": 20,
  "totalCount": 37,
  "totalPages": 2,
  "summary": { "totalBookings": 37, "totalParticipants": 112, "totalConfirmedAmount": 98400000.00 },
  "items": [
    {
      "bookingId": "1201",
      "bookingCode": "BK-20261020-0007",
      "tour": { "tourId": "101", "title": "Ba Na Hills full-day tour" },
      "schedule": { "scheduleId": "311", "startAtUtc": "2026-11-02T00:00:00+00:00", "endAtUtc": "2026-11-02T10:00:00+00:00" },
      "booker": { "userId": "55", "fullName": "Nguyen Van A", "email": "a@example.com" },
      "quantity": 3,
      "totalAmount": 2400000.00,
      "discountAmount": 100000.00,
      "status": "Confirmed",
      "paymentStatus": "Paid",
      "checkInStatus": "NotCheckedIn",
      "checkedInAtUtc": null,
      "bookedAtUtc": "2026-10-20T03:15:00+00:00"
    }
  ]
}
```

`summary` covers the whole filtered set, not the page: `totalBookings` counts every status;
`totalParticipants` and `totalConfirmedAmount` count only `Confirmed` and `Completed` bookings.
No match is `200` with `items: []` and a zero summary (show MSG128).

### `GET /api/v1/operator/bookings/{bookingId}`

```json
{
  "bookingId": "1201",
  "bookingCode": "BK-20261020-0007",
  "status": "Confirmed",
  "paymentStatus": "Paid",
  "bookedAtUtc": "2026-10-20T03:15:00+00:00",
  "cancelledAtUtc": null,
  "cancelReason": null,
  "tour": { "tourId": "101", "title": "Ba Na Hills full-day tour" },
  "schedule": { "scheduleId": "311", "startAtUtc": "2026-11-02T00:00:00+00:00", "endAtUtc": "2026-11-02T10:00:00+00:00", "meetingPoint": "Da Nang Cruise Port" },
  "booker": { "userId": "55", "fullName": "Nguyen Van A", "email": "a@example.com", "phoneNumber": "+84901234567" },
  "quantity": 3,
  "participants": [
    { "fullName": "Nguyen Van A", "participantType": "Adult" },
    { "fullName": "Nguyen Thi B", "participantType": "Child" }
  ],
  "ticket": { "ticketCode": "TK-0000001201", "status": "Valid", "issuedAtUtc": "2026-10-20T03:16:00+00:00", "expiresAtUtc": null },
  "checkInStatus": "NotCheckedIn",
  "checkedInAtUtc": null,
  "payment": { "gateway": "PayOS", "gatewayTransactionRef": "FT26102012345", "status": "Success", "amount": 2400000.00, "succeededAtUtc": "2026-10-20T03:15:40+00:00" },
  "paidAmount": 2400000.00,
  "subtotalAmount": 2500000.00,
  "discountAmount": 100000.00,
  "totalAmount": 2400000.00,
  "coupon": { "code": "SUMMER10", "name": "Summer 10%", "discountAmount": 100000.00 },
  "canCancel": true,
  "cancelBlockedReason": null
}
```

- `ticket` is `null` when no ticket was issued (for example an unpaid booking). Do not render it then.
- `payment` is `null` when there is no verified payment; then `paidAmount` is `0`. It is read-only
  (BR-77). Refund progress is not part of this payload: use the existing
  `GET /api/v1/bookings/{bookingId}/refund`.
- `coupon` is `null` when no voucher was applied.
- `canCancel` is exactly the UC-41 rule for a Tour Operator: status `PendingPayment` or `Confirmed`
  and no used ticket. When it is `false`, `cancelBlockedReason` is `InvalidStatus` (the booking is
  already `Cancelled`, `Completed` or `Expired`) or `CheckedIn` (a ticket was used). Cancelling is
  `POST /api/v1/bookings/{bookingId}/cancellation` (UC-41, unchanged).
- Never shown by design: participant phone, e-mail, nationality and ID document (type and number),
  and any gateway payload, checkout link, QR data or order code.

## Check-in

`checkInStatus` is `CheckedIn` when any ticket is `Used` or has a check-in timestamp, otherwise
`NotCheckedIn`; `checkedInAtUtc` is the earliest check-in timestamp. The check-in itself is recorded
by UC-43 (mobile), which is not built yet, so until it ships every booking reports `NotCheckedIn`.

## Errors

| HTTP | `errorCode` | Meaning | FE message |
| --- | --- | --- | --- |
| 401 | none | missing or invalid token | sign-in flow |
| 403 | `operator_tour.access_required` | not an active, approved Tour Operator | MSG126 |
| 403 | `operator_booking.forbidden` | the booking, tour or schedule is not owned by this operator (or does not exist, for a filter) | MSG126 |
| 404 | `operator_booking.not_found` | detail: no such booking | MSG126 |
| 400 | `operator_booking.invalid_filter` | bad `pageNumber`, `pageSize`, ids or an over-long `keyword` | validation message |
| 400 | `operator_booking.invalid_date_range` | `departureFrom` is after `departureTo` | MSG29 |
| 500 | none | unexpected failure | MSG127 |

An empty list is not an error (`200`, show MSG128).
