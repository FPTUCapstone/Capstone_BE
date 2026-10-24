# Web Login Unification — Backend Specification

## Status

**Draft — awaiting approval.** Direction approved by the developer on 2026-10-24: merge the
Administration and general Web sign-in into one flow. No code may start until this specification
is approved.

## Problem

- Web sign-in is split across two entry points. `/sign-in` serves every role; `/admin/login` serves
  only Administrator and Staff.
- Admin/Staff sessions use a separate sealed cookie (`tripmate_admin_*`) minted only by the admin
  session endpoint. An Administrator who signs in at `/sign-in` is routed into `/admin/*`, the admin
  layout finds no admin cookie, and the user must sign in again.

## Decisions

| # | Decision |
| --- | --- |
| D1 | One Web sign-in endpoint: `POST /api/v1/auth/web/login` for every role. |
| D2 | `POST /api/v1/auth/web/admin/login` is retired after the FE switch-over; it is removed in the same delivery once no caller remains. |
| D3 | Administrator and Staff have **the same session lifetime and refresh policy as every other role**. No shorter admin TTL. |
| D4 | Administrator and Staff remain password-only. The Backend rejects Google sign-in for administration roles with `auth.admin_google_sign_in_disabled`; the shared sign-in screen keeps its Google button because the role is unknown before authentication. |
| D5 | Manually locked (`auth.account_locked`) and inactive accounts are rejected identically for all roles. Automatic failed-attempt lockout and login rate limiting are out of scope. |
| D6 | Administration routes stay fail-closed on the server: the admin console verifies the role on every request, not just at sign-in. |
| D7 | Mobile sign-in still rejects administration roles (`auth.admin_mobile_sign_in_disabled`). |

## Behaviour

### Sign-in (`POST /api/v1/auth/web/login`)

- Accepts email and password for any role; returns the existing `WebAuthResponseDto` with `role`
  and `status`.
- Sets the existing refresh cookie for every role, honouring `keepMeSignedIn`.
- Failure codes (existing, unchanged; `AdminAccessRequired` is **not** used on this endpoint):

  | Situation | Code | HTTP |
  | --- | --- | --- |
  | Wrong email or password | existing invalid-credentials code | 401 |
  | Email registered but not verified | `MSG_UNVERIFIED` (`AccountPendingVerification`) | existing mapping |
  | Account manually locked | `auth.account_locked` | existing mapping |
  | Account inactive | `auth.account_inactive` | existing mapping |
  | Administrator or Staff signing in with Google | `auth.admin_google_sign_in_disabled` | existing mapping |

- `auth.admin_access_required` remains only on the retired admin-only endpoint until D2 removes it.

### Admin console access

- The admin console no longer requires a separately minted admin cookie. It derives the admin
  session from the same web session (refresh cookie → bearer) the rest of the app uses, and
  requires `role ∈ {Administrator, Staff}` and `status = Active` on each request.
- Staff are still limited to their own route allow-list (existing `STAFF_ALLOWED_EXACT_ROUTES`).
- Traveler and Tour Operator never gain access to `/admin/*`; the existing `403` and redirect
  behaviour is preserved.

### Session expiry

- Unchanged. Refresh-token rotation and expiry are the same for all roles.

### Retired

- `/admin/login` (FE page) redirects to `/sign-in`, preserving `returnUrl` only when it is a
  same-origin `/admin/*` path.
- `/api/admin/session` (POST/DELETE) is retired after the FE switch-over; logout uses the existing
  `web/logout` endpoint.

## Test obligations

- Administrator and Staff can sign in through `/web/login` and receive the normal refresh cookie.
- Administrator and Staff accounts with status other than Active are rejected with the existing
  codes; Google sign-in for these roles is still rejected.
- A manually locked Administrator and a manually locked Traveler are both rejected with `auth.account_locked`.
- Traveler and Tour Operator requests to admin endpoints return `403`.
- `/web/admin/login` returns `404` after removal (or the removal is covered by its absence in the
  OpenAPI document).
- Mobile sign-in still rejects administration roles.

## Non-goals

Multi-factor authentication, changes to password policy, changes to Google sign-in for other roles,
and any change to the admin console's visual design.
