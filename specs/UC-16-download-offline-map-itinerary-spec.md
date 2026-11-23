# UC-16 — Download Offline Map & Itinerary: Backend contract
Status: APPROVED DIRECTION — user confirmed 2026-11-22. Documentation only in this task.
Branch: feature/linhnv-download-offline-map-and-itinerary

## Source and scope
Authority: local SRS_rev10 (5).docx, section 3.3.7, paragraphs 3055–3127; BR36–BR40; MSG104–MSG107; offline synchronization section 3.10.3. Source SHA256: 5E7110B8130ACDFAE7E69FE6D50BC47DB573BC0E2CBBFFCBAA1CAC2A4DCC1FCC.
Paragraph numbers refer to the extracted Word body paragraphs, not printed page numbers.
The user assigns UC16 to Traveler on Mobile only. BE supplies authorized itinerary/POI reads. Package download, storage, capacity, progress, removal and offline rendering belong to Mobile.
No server offline-package entity, table, migration, download job, payment behavior or synchronization queue is introduced.

## SRS requirements
- Verify itinerary belongs to or is accessible by the Traveler.
- Fetch current itinerary version, ordered items, related POIs, route information and maps for its area (BR36).
- Persist version/timestamp locally. Only a complete package is available (BR38–39).
- Maximum configured package size is 150 MB (BR37); MSG107 additionally requires at least 150 MB free device storage.
- Offline access does not authorize booking, payment or check-in (BR40).
- Refresh requires confirmation and publishes the replacement only after complete persistence; failure preserves the old package.
- Cancel and remove are required Mobile actions. No server package record is created.

## Existing contracts to reuse
| Endpoint | Role / access | Responsibility |
| --- | --- | --- |
| GET /api/v1/itineraries/{itineraryId} | Traveler; owner or active member of active associated group | Current itinerary detail, resolved ID and version, ordered items, coordinates, route metrics and timing |
| GET /api/v1/pois/{id} | Existing public read policy | Related POI description, address, opening hours, coordinates and update timestamp |

Implementation references:
- src/TripMate.Api/Controllers/V1/ItinerariesController.cs
- src/TripMate.Application/Features/Itineraries/Common/ItineraryAccessService.cs
- src/TripMate.Application/Features/Itineraries/GetDetail/GetItineraryDetailQueryHandler.cs
- src/TripMate.Application/Features/Itineraries/Common/ItineraryDetailResponse.cs
- src/TripMate.Api/Controllers/V1/PublicPointsOfInterestController.cs

The itinerary handler already uses a serializable read. Historical itinerary IDs can resolve to the current version with a different ID. Mobile must persist returned ID/version, not assume the requested ID is current. CanManage is not a download permission: readable group itineraries remain eligible.
No download action starts a navigation session. Staff route catalog permissions must not be widened.
Geometry and guidance are supplied by the integrated map service, not invented from route distance/duration.

Contract details verified from current code:
- Rest items legitimately have null POI ID/coordinates. Require coordinates only for mapped visit/route points; a rest entry is not an incomplete POI.
- Latitude/longitude zero is valid data and must not be treated as a missing coordinate.
- Accepting a draft changes status and UpdatedAtUtc without increasing Version. ID/version comparison alone therefore detects replacement versions, not every field mutation. Mobile should compare a normalized essential snapshot fingerprint as well when checking whether its A/B reads match; omit volatile/navigation and booked-service fields from that fingerprint.
- The detail DTO can include BookedServices. Do not persist the raw response wholesale: whitelist essential itinerary fields and exclude payment/private booking data.
- itinerary.access_denied is authoritative 403 for loss of itinerary access; 401 is authentication failure and must not be interpreted as proof of revoked itinerary permission.

## Proposed consistency protocol
Reuse existing DTOs without inventing a new API contract:
1. Read authorized current itinerary A and pin its returned ID/version.
2. Read only referenced POIs; fetch map-service route data for that pinned snapshot.
3. Re-read itinerary B before publishing. Compare resolved ID/version AND a deterministic fingerprint of essential itinerary fields (status, dates, ordered items, coordinates, timing and transport). Some existing commands change fields without incrementing Version. If any differ, discard staging and offer retry; do not assemble mixed itinerary versions. Rest items legitimately have no coordinates; require coordinates only for actual POI/route points.
4. Save a local immutable snapshot, POI capture timestamps and route fingerprint. POI reads are individually captured, not an atomic cross-endpoint database snapshot.
5. On reconnection compare current itinerary ID/version and the same essential-field fingerprint. Offer refresh, never silently replace. Exclude private BookedServices, credentials and volatile navigation-session flags from that fingerprint.

This protocol detects itinerary version changes but cannot prove atomic POI consistency or detect POI-only edits through itinerary version. B1 accepts individually captured POIs for the first release; retain their capture/update timestamps and disclose this snapshot boundary.
If strict atomic expanded snapshot is required, stop and revise this spec for one bounded read-only query reusing existing access policy; do not silently add an endpoint or DB revision column.

## Failures and security
Reuse existing HTTP/error contracts, including unauthenticated, forbidden, missing itinerary/POI and service failure; do not change global authorization/error mapping for UC16. Mobile maps SRS MSG32/MSG126 where appropriate without displaying raw codes.
Download failure must not mutate itinerary, bookings or navigation state. Do not export credentials, payment details, unrelated POIs or other members' private information.
Expired/denied access cannot be bypassed with a client-supplied owner ID. Retained offline data accessibility is a separate Mobile policy, not server authorization.

## Acceptance criteria
- BE reads preserve owner and active-member access; outsider/inactive-member requests remain denied.
- Historical ID returns authoritative current ID/version and usable itinerary data.
- Snapshot assembly handles missing coordinates/POIs and version races as incomplete; never claims success with fabricated geometry.
- No server package entity, DB migration, new authorization exemption or write side effect.
- Existing public POI contract and Staff route boundaries remain intact.

## Confirmed decisions — 2026-11-22
B1: Reuse existing reads with individually captured POI snapshots and itinerary A/B version check. Add no new endpoint unless a demonstrated gap requires a separately approved contract revision.
B2: Cached route/map viewing and GPS position; no new offline route computation or dynamic turn-by-turn. Whole itinerary coverage buffered1 km, zoom10–16 is the Mobile target, subject to native feasibility without silently weakening coverage.
Mobile decisions M1–M4 are recorded in its companion spec, including local package list, retained same-account read-only access, logout lock and authoritative online access-revocation handling. These are user-confirmed choices beyond explicit SRS detail, not inferred SRS rules.
Offline synchronization is referenced by name and SRS section 3.10.3 (UC70 in this SRS); older UC13/16 references to UC72 are numbering drift and do not expand this task.
