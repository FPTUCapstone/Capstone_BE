# TM-225 — Staff provisioning and Service Provider workflow

## Authorized scope and source

The user requested implementation of TM-225 on 14 November 2026. Implement UC-76–80
on Backend and Web using Report 3 sections 3.9.1.4–5 and 3.6.3–5. Administrator creates
Staff, Staff creates Service Provider, and Service Provider handles its own linked
commercial POI requests. Preserve current commercial listings and legacy bookings.

## Acceptance contract

- Accounts have unique normalized email, Active status, server-assigned role and a
  hashed temporary password delivered only by email. First sign-in requires changing
  that password. Account creation and acting identity are audited.
- Provider provisioning requires at least one Active commercial POI in Hotel,
  Vehicle Rental or Restaurant. A POI belongs to at most one provider account.
- Provider lists are SQL-filtered, counted and paginated with deterministic order,
  pending requests first. Detail and mutations enforce linked-POI ownership.
- Pending Confirmation may become Confirmed or Rejected once within 24 hours.
  Rejection requires a reason. Unanswered requests expire and notify the Traveler.
- After the Vietnam service date passes, a Confirmed request may become Completed
  with positive actual amount, or No-show with zero commission. Completion commission
  is 10 percent rounded down to whole VND. At three days after the service date,
  unresolved Confirmed requests default to Completed at the estimated amount.
- Transactions, concurrency checks, idempotent mutation handling and durable audit/
  notification records prevent partial or duplicate decisions and outcomes.
- Confirmed service association is visible through its existing itinerary linkage.
- Preserve legacy commercial booking statuses/data and canonical SQL schema parity.

## Dependencies and delivery

UC-31 owns creation of service requests. Monthly statements belong to TM-226 and
full provider authentication lifecycle coverage to TM-229. This delivery supplies
the account/password-change enforcement needed for UC-76–80 to function. Use existing
Web provider UI where applicable and replace demo transports with real integration.
Document any missing upstream request-creation or itinerary linkage dependency.

The user authorized synchronization with develop, commit, push, PR creation and CI verification on 15 November 2026. Merge remains subject to repository review requirements.

## Confirmed deadline interpretation

The user confirmed that providers have the three complete Vietnam calendar days
following the requested service date to record the outcome. A service on 12 November
may be recorded until the end of 15 November and defaults from 00:00 on 16 November
in Vietnam, corresponding to the start of D+4. This is enforced by server timestamps.
