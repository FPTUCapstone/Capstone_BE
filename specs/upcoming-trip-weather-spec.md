# Upcoming Trip Weather — spec (draft, decisions agreed 2026-11-26)

Personal layer on top of the public `/weather` page (see `public-weather-forecast-spec.md`).
Logged-in Traveler only. Guests and Travelers without qualifying bookings see the page unchanged.

## Agreed decisions

| # | Question | Decision |
|---|----------|----------|
| T1 | Source of the orange warning banner | Only an open UC-66 Weather Safety Review on the departure. No forecast-threshold banner of our own. |
| T2 | Which bookings appear | `BookingStatus.Confirmed` only, departure not yet ended (in-progress tours included). |
| T3 | How far ahead | Departures starting within the next 5 days, or in progress. Max 3 cards, nearest start first. |
| T4 | Which point's weather | Meeting point (`TourSchedule.MeetingLatitude/Longitude`) on the departure day; fallback to the first itinerary POI when meeting coordinates are missing. Card click opens the public forecast for that point. |
| T5 | Tomorrow key | Reuse `PublicWeather__ApiKey` through `IPublicWeatherForecastProvider` (shared 30-min cache per 0.01° cell). No new key. |
| T6 | Banner text and lifetime | Text follows `WeatherSafetyReview.Status`: PendingAcknowledgement → "Nhà điều hành đang xem xét"; Acknowledged → "Nhà điều hành đã xác nhận" + `ImmediateInstructions` if any; Escalated → "Đội hỗ trợ TripMate đang xử lý". Closed → no banner. Cancelled tour → booking no longer Confirmed, so card and banner disappear (existing UC-66 refund notice covers it). Button "Xem booking" → web `/bookings/{id}/ticket`. |

## Out of scope

- CSP itineraries (UC-14 covers them on mobile).
- Any action from the banner (continue/cancel stays with the Tour Operator).
- Mobile UI.

## Implementation (2026-11-26)

- `GET /api/v1/traveler/upcoming-trip-weather`, role Traveler, same `public-weather` rate limit (30/min/IP).
- Handler `GetUpcomingTripWeatherQueryHandler`: one query for ≤3 bookings, one for first itinerary stops, one for
  open reviews + their `WeatherEvent` (matched on Provider + ProviderEventId; newest open review per departure).
- Forecast day = departure's local date (Asia/Ho_Chi_Minh); for a trip already under way, today's local date.
- Point outside the Vietnam box or no point → no forecast call; trip still listed. Provider unavailable → `forecast: null`, trip still listed.
- Coordinates rounded to the public 0.01° cell, so the card and the public page share one cached Tomorrow call.
- Tests: 8 application unit tests, 3 endpoint tests (401/403/200 empty), 1 SQL Server test (real translation).
