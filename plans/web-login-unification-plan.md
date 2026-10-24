# Web Login Unification — Backend Plan

Companion to `specs/web-login-unification-spec.md` (decisions D1–D7). Frontend plan:
`Capstone_FE/plans/web-login-unification-plan.md`. Branch: `fix/linhnv-fix-login-web`.

Status: **Draft — starts only after the spec and this plan are approved.**

## Current baseline (verified in code)

- `POST /api/v1/auth/web/login` already accepts every role (`WebPasswordLogin` with
  `administratorOnly = false`); `LoginCommandHandler` applies the admin-only check only when the
  command is admin-only, and rejects administration roles on mobile.
- `POST /api/v1/auth/web/admin/login` is the only endpoint that restricts to Administrator/Staff.
- Locked accounts (`AccountStatus.Locked`) and inactive accounts are rejected by
  `AccountEligibilityResolver` (`auth.account_locked`, `auth.account_inactive`).
- Google sign-in for administration roles is rejected (`auth.admin_google_sign_in_disabled`).
- No automatic failed-attempt lockout and no login rate limiting exist; they are out of scope.

## Work

### B1 — Verify the shared sign-in contract (tests only)

Add or confirm tests that `/web/login`:
- signs in Administrator and Staff and sets the refresh cookie exactly as for other roles;
- returns `auth.account_locked`, `auth.account_inactive`, `MSG_UNVERIFIED` for the matching
  states, for every role including administration roles;
- rejects Google sign-in for Administrator and Staff with `auth.admin_google_sign_in_disabled`;
- rejects administration roles on the mobile endpoint.

Exit: all tests green before any removal. No production code change expected here.

### B2 — Verify refresh returns what the FE resolver needs

`POST /api/v1/auth/web/refresh` must return `accessToken`, `role`, and `status` for
Administrator and Staff. Add a test per role. If any field is missing for these roles, fix it in
this task; otherwise no change.

### B3 — Confirm no FE dependency on the admin-only endpoint remains (gate for B4)

After the FE plan's F4 is merged to the feature branch, search the FE repo for
`auth/web/admin/login`. The result must be empty before B4.

### B4 — Remove the admin-only endpoint (after FE F5 is deployed)

- Delete `WebAdminLogin` from `AuthController` and its `ProducesResponseType` declarations.
- Remove the `administratorOnly` flag from `WebPasswordSignInCommand` and the branch in
  `LoginCommandHandler`. Keep the mobile administration-role rejection.
- Remove `auth.admin_access_required` from the sign-in code path if no other caller uses it;
  otherwise keep it and document the remaining caller.
- Update OpenAPI tests: the path must be absent and `/web/login` must document the codes in the
  spec table.

Exit: `dotnet build`, unit tests, integration tests, and OpenAPI tests pass.

## Deploy order

1. Merge B1 and B2 (tests and any B2 fix). Deployable alone.
2. Frontend F1–F5 (see the FE plan). The FE must stop calling `/web/admin/login` before B4.
3. Deploy B4 after the FE is live.

Rollback: if B4 is deployed and the FE needs the endpoint again, redeploy the previous backend
image; the FE in step 2 does not depend on B4 being absent.

## Verification

- `dotnet build TripMate.slnx`, `dotnet format --verify-no-changes`.
- Unit tests for the login handler and eligibility resolver, per role.
- Integration tests against SQL Server for the four failure codes and Google rejection.
- Manual check: Administrator signs in once at `/sign-in` and reaches the admin dashboard.

## Out of scope

Automatic failed-attempt lockout, login rate limiting, MFA, password policy.
