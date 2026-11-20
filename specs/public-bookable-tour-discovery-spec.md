# Public bookable tour discovery

Approved as task 3 of the Web booking/Admin fixes on 20 November 2026:
`../Capstone_FE-user-profile-book-tour/docs/superpowers/specs/2026-11-20-booking-admin-fixes-spec.md`.

V1 search accepts optional `bookableOnly=true|false` (default false for existing
API consumers). Web's public tour proxy forces true. With true, only tours with
an open Scheduled future departure, remaining valid capacity and a canonical
BookedTourItineraryTimeline accepted by booking preflight are counted/paged.
The representative departure must itself be eligible and match requested dates.
One invalid departure cannot hide another valid departure of the same tour.

Direct detail retains the historical tour but sets schedule bookability using
the same canonical timeline. Recommendations apply eligibility before counts.
No data writes, migrations, booking rules or financial rules change. The dormant,
unregistered V2 independent SQL reader has no HTTP route and is not changed.
