# List Favorite POIs (Backend Spec)

Extends `specs/UC-10-traveler-feedback-spec.md` (§3.2) so signed-in Travelers can view all POIs they have marked as favorite in their profile workspace.

## 1. Scope
- Add `GET /api/v1/pois/favorites` for signed-in `Traveler` accounts.
- Return a paginated list of the asking Traveler's favorite POIs that are currently `Active`, sorted newest favorited first (`CreatedAtUtc DESC, PoiId DESC`).
- No database schema changes (reuses `social.PoiFavorites` and existing POI/photo/opening-hour/review tables).

## 2. API Contract

### `GET /api/v1/pois/favorites`
- **Authorization:** `[Authorize(Roles = nameof(UserRole.Traveler))]`
  - Unauthenticated → `401 Unauthorized`
  - Authenticated non-Traveler (`TourOperator`, `Administrator`, `Staff`, `ServiceProvider`) → `403 Forbidden`
- **Cache:** `[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]`
- **Query Parameters:**
  - `page` (`int`, default `1`, min `1`)
  - `pageSize` (`int`, default `12`, min `1`, max `50`)
  - Invalid values → `400 Bad Request` (`ValidationProblemDetails`)
- **Success Response (`200 OK`, `PagedFavoritePoisResponse`):**
  ```json
  {
    "page": 1,
    "pageSize": 12,
    "totalCount": 2,
    "totalPages": 1,
    "items": [
      {
        "id": 12,
        "name": "Bà Nà Hills",
        "categoryId": 1,
        "categoryName": "Sightseeing",
        "latitude": 15.9977,
        "longitude": 107.9881,
        "address": "An Sơn, Hòa Ninh, Hòa Vang, Đà Nẵng",
        "indoorOutdoor": "Outdoor",
        "averageVisitDurationMinutes": 240,
        "hasShelter": true,
        "averageRating": 4.7,
        "reviewCount": 18,
        "thumbnailUrl": "https://...",
        "isOpenNow": true,
        "favoritedAtUtc": "2026-11-26T06:30:00+00:00"
      }
    ]
  }
  ```

## 3. Business Rules & Edge Cases
- **Traveler isolation:** Only favorites belonging to `currentUserService.UserId` are returned.
- **Active-only visibility:** Favorites pointing to `Inactive` POIs are excluded from both `totalCount` and `items` (matching `/api/v1/pois` and `/api/v1/pois/{id}`).
- **Ordering:** `CreatedAtUtc DESC`, then `PointOfInterest.Id DESC` for deterministic pagination.
- **Empty / out-of-range pages:**
  - `totalCount == 0` → `totalPages = 0`, `items = []`.
  - `page > totalPages` (when `totalCount > 0`) → `items = []`, preserving `page`, `pageSize`, `totalCount`, `totalPages`.
- **Opening state & ratings:** Uses `PoiOpeningState.GetVietnamDayAndTime(dateTimeProvider.UtcNow)` and `TripReviewAggregateReader.QueryPoiReviews(dbContext)` + `TripReviewAggregateDto.Round(...)`, matching `ExplorePoisQueryHandler`.
- **Route precedence:** Literal `GET /api/v1/pois/favorites` must not break `GET /api/v1/pois/{id}`.
