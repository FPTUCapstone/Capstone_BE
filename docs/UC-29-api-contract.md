# UC-29 Backend API contract — View QR E-ticket

Status: **implemented on `feature/uc29-view-qr-eticket` (not merged yet), verified 2026-10-25; migrations applied on Azure UAT.** Authority: `specs/UC-29-view-qr-eticket-be-spec.md`,
Report 3 §3.5.6. For Capstone_FE and Capstone_Mobile.

## What changes for clients

| Current client stub | Real contract |
| --- | --- |
| FE `GET /api/tickets/{id}` | `GET /api/v1/traveler/tickets/{ticketId}` |
| FE expects string ids (`typeof ticketId === 'string'`) | ids are JSON **numbers**, same as `actions.ticketId` in TM-78 |
| FE `TicketStatus` = `Valid/Used/Cancelled/Expired` | `ticketStatus` adds **`NotYetActive`** |
| FE `qrPayload` | `qr.content` (or `qr: null`) |
| Mobile QR built on the device as `TRIPMATE_TICKET:{bookingCode}` | **Removed.** The QR content always comes from the server. Never build, parse or cache-modify it. |
| Every error → `501 PENDING_BE_INTEGRATION` | the errors below |

## Auth

`Authorization: Bearer <traveler token>`. Active Traveler accounts only; only the booking's owner.

## Endpoints

| Method and path | Use |
| --- | --- |
| `GET /api/v1/traveler/tickets/{ticketId}` | Trip History → [View E-ticket] (`actions.ticketId`). |
| `GET /api/v1/traveler/bookings/{bookingId}/ticket` | Booking Confirmation screen after payment (knows only the booking id). |
| `POST /api/v1/traveler/tickets/{ticketId}/qr-code/refresh` | [Refresh QR Code]. No body. |

Never swap `ticketId` and `bookingId`. All three return the same body on `200`.

```json
{
  "ticketId": 77,
  "ticketCode": "TK-0000000456",
  "bookingId": 456,
  "bookingCode": "BK-20261017-00001",
  "ticketStatus": "Valid",
  "tour": { "tourId": 45, "title": "Hoi An Lantern Tour" },
  "operatorName": "Hoi An Travel Co.",
  "schedule": {
    "startAtUtc": "2026-10-30T01:00:00+00:00",
    "endAtUtc": "2026-10-30T10:00:00+00:00",
    "meetingPoint": "Old Town gate"
  },
  "participantCount": 3,
  "participants": { "adult": 2, "child": 1, "infant": 0 },
  "validity": {
    "departureDate": "2026-10-30",
    "timeZone": "Asia/Ho_Chi_Minh",
    "validFromUtc": "2026-10-29T17:00:00+00:00",
    "validUntilUtc": "2026-10-30T16:59:59.9999999+00:00"
  },
  "checkedInAtUtc": null,
  "qr": {
    "content": "TM1.q8V0...Xk.3fJ9...aQ",
    "issuedAtUtc": "2026-10-24T08:15:00+00:00"
  }
}
```

- `qr` is present only when `ticketStatus` is `NotYetActive` or `Valid`; otherwise it is `null`.
- `qr.content` is opaque. Render it as a QR code exactly as received.
- `checkedInAtUtc` is set only when `Used` (fill `{Checkin_Time}` in MSG95).
- `meetingPoint` may be `null`.
- Dates are UTC instants. Show them in `Asia/Ho_Chi_Minh` as `dd/MM/yyyy` (CR-07). Use
  `validity.departureDate` for `{Departure_Date}`.
- Opening a ticket never consumes it. Only the Operator check-in (UC-43) marks it `Used`.

## `ticketStatus` → screen

| `ticketStatus` | Label | QR | Message |
| --- | --- | --- | --- |
| `Valid` | Valid | shown | MSG93 |
| `NotYetActive` | Not Yet Active | shown, with MSG98 next to it | MSG98 |
| `Used` | Used | hidden | MSG95 |
| `Cancelled` | Cancelled | hidden | MSG139 |
| `Expired` | Expired | hidden | MSG140 |

Show [Refresh QR Code] only for `Valid` and `NotYetActive`. After a refresh, replace the displayed QR with
the new one; the previous QR no longer works at check-in.

Refresh revokes the old QR as soon as the server commits, even if the response never reaches the device.
To keep a Traveler at the meeting point from ending up with a dead QR:

- Ask for confirmation first, e.g. "The current QR code will stop working. Continue?".
- Disable the button while the device is offline.
- If the refresh request fails or times out, do not keep showing the old QR as if it were valid:
  re-`GET` the ticket. If that also fails, show MSG127 and state that the displayed QR may no longer be
  valid until the ticket is reloaded.

## Errors (`errorCode` present on every row that names one)

| HTTP | `errorCode` | When | Show |
| ---: | --- | --- | --- |
| 401 | `ticket.unauthorized` | Not signed in | MSG125 → sign in |
| 403 | `ticket.forbidden` | Not a Traveler, inactive, or not your booking | MSG126, no QR |
| 404 | `ticket.not_found` | Unknown ticket / booking | MSG127 |
| 409 | `ticket.booking_not_confirmed` | Booking not confirmed by a verified payment | MSG138, no QR |
| 409 | `ticket.not_issued` | Paid booking without a ticket yet | MSG127 + retry |
| 409 | `ticket.not_refreshable` | Refresh on Used/Cancelled/Expired; `ticketStatus` in the problem body | re-`GET` and show that status |
| 400 | — | Id not a positive whole number (`0`, `-1`, `abc`); plain problem without `errorCode` | — |
| 500 | — | System failure | MSG127 + retry (SRS 4.a1) |

## Client notes

- Demo fixtures stay behind the existing explicit demo gate only; a real call must never fall back to a
  fixture silently.
- Mobile: the ticket screen is the device shown to the Operator at the meeting point. Keep the last `200`
  body in memory so the QR stays on screen if connectivity drops; do not persist it to disk.
