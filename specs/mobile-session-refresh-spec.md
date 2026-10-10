# Specification: Mobile Session Refresh

**Status**: Implemented on `feature/phuctv-uc13-navigation-backend` (uncommitted), pending team review.
**Relates to**: UC-03 Sign In (Mobile), UC-05 Sign Out, UC-13 Navigate Route.
**Supersedes**: the "no Mobile refresh endpoint" statements in UC-05 (`Out of scope`) and in the Mobile API
contract. UC-05 itself is unchanged.

## Problem

Mobile sign-in returns a 15-minute access token and a 7-day refresh token, but no endpoint redeemed the refresh
token for Mobile. Every Mobile session therefore ended after 15 minutes. UC-13 trips last hours, so reach, skip,
depart and finish calls failed mid-trip and the Traveler was sent to the sign-in screen.

## Contract

`POST /api/v1/auth/refresh` (anonymous, like `login`)

Request body:

```json
{ "refreshToken": "<token returned by POST /api/v1/auth/login>" }
```

| Case | Result |
|---|---|
| Active, unexpired refresh token of an eligible account | `200`, `data` = the Mobile login shape (`userId`, `email`, `fullName`, `role`, `status`, `accessToken`, `refreshToken`, `accessTokenExpiresAtUtc`) with a new access token |
| Missing, empty, unknown, revoked (after `POST /auth/logout`) or expired token | `401` `AUTH_TOKEN_INVALID` |
| Account no longer eligible (for example `Locked`) | `403`, same rules as sign-in |

## Rules

- Same redemption as `POST /api/v1/auth/web/refresh` (`WebRefreshCommand`): lookup by token hash, revocation,
  expiry and `AccountEligibilityResolver` checks. Only the transport differs (body instead of HttpOnly cookie).
- Non-rotating, like Web: the refresh row is never modified and the same refresh token is echoed back.
- No rate-limit policy, matching `login` and `web/refresh`; tokens are random and only their hash is stored.

## Mobile client behaviour

- `AuthInterceptor`: an authenticated (Bearer) non-auth request that returns `401` triggers one refresh through
  an interceptor-free client, stores the new access token and replays the request once.
- Concurrent `401`s share one refresh. A failed or impossible refresh, or a second `401` after the replay,
  invalidates the session as before (sign-in screen).
- Auth endpoints (`/api/v1/auth/*`) are never refreshed.

## Verification

- BE: `MobileRefreshIntegrationTests` (7 tests, including real JWT validation of the refreshed token).
- Mobile: `auth_interceptor_refresh_test.dart` (renewal and replay, single shared refresh, rejected refresh,
  missing refresh token, no second retry, auth endpoints excluded).
- Device: signed in at 12:47; after 13:03 the finish call returned `401`, `POST /auth/refresh` returned `200`
  and the replayed call returned `200` without leaving the trip.
