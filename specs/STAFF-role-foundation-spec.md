# Staff role foundation — PR #61

## Authority and approval

Report 3 Software Requirement Specification V2 is authoritative. Source: [Report 3 V2](https://drive.google.com/file/d/1WNp9w7HfhMDH4M9vy9HecMYCxpeePL9O/view), SHA-256 `08941571986ba5a4775af070a5f975a32d050b422b82028781a649b008786785`.

The repository specs/plans audit found authentication specifications (UC-01 through UC-06) but no dedicated approved Staff foundation specification. This document records the existing PR behavior and the user's authorized P1–P3 remediation scope; it does not assert Product/BA approval. **Product/BA approval: PENDING.** In particular, V2 actor/screen ownership includes Staff while §3.2.4 lists Traveler, Tour Operator and Administrator, and detailed §3.9 rules retain Administrator-only restrictions (BR-115). Staff operational grants remain pending clarification. No endpoint permission is broadened by this PR.

## Actor ownership and role matrix

Staff is an internal operational actor using the Web administration workspace. Administrator remains the owner of Administrator-only actions. A group host remains a Traveler, not a role. Staff is not a Tour Operator and cannot acquire operator ownership through login.

| Role | Persisted string / enum value | Administration password login | Mobile login | AdministratorOnly | StaffOnly | StaffOrAdministrator |
|---|---|---|---|---|---|---|
| Traveler | Traveler / 1 | denied | existing eligibility | denied | denied | denied |
| Tour Operator | TourOperator / 2 | denied | existing eligibility | denied | denied | denied |
| Administrator | Administrator / 3 | existing eligibility | denied | allowed | denied | allowed |
| Staff | Staff / 4 | existing eligibility | denied | denied | allowed | allowed |

Policy membership is not a new endpoint grant. Existing controller policies and traveler/operator restrictions remain intact. Policies use RequireRole with distinct role names on JWT-authenticated endpoints; Staff is never aliased to Administrator.

## Authentication and Firebase boundary

Web password login and Web refresh support persisted Staff roles under existing account eligibility rules. Google sign-in rejects internal administration accounts before mutation/session creation; status failures keep their existing precedence. Email verification also rejects Google for Staff/Administrator without activating an account. Password verification retains existing behavior. Mobile login rejects administration accounts; this foundation supplies no Staff Mobile workspace. Public registration and Google auto-provisioning do not create Staff.

Firebase verifies identity and sign-in provider. Unverifiable Firebase tokens fail closed; Google sign-in also rejects missing required provider/email claims. This does not add a missing-provider rule to email verification. The backend reads the role from its persisted User and emits that role in its own JWT; Firebase custom role claims do not authorize Staff or override database roles. No Firebase custom-claim write or synchronization feature is added.

## Migration and compatibility

`database/migrations/20261007_allow_staff_role.sql` updates the database-first Users role CHECK transactionally, validates existing roles, discovers column/table-level dependencies, refuses multiple or cross-column constraints, and rolls back on failure. It creates a trusted canonical constraint and supports reruns. The fresh v7 schema allows the same four string roles. There are no seeds, backfills, role promotions or new provisioning APIs. Deploy schema support before assigning Staff; rollback to software without Staff requires an explicit operational plan for existing Staff rows/tokens, not an automatic destructive downgrade.

Values 1–3 and existing role strings stay unchanged; Staff is additive value 4. FE/Mobile clients must recognize the additive role to offer a Staff workspace. Existing JSON/DTO, routes, JWT shapes, error identifiers and HTTP mappings remain unchanged. Human copy for `auth.admin_google_sign_in_disabled` becomes “Administration accounts must sign in with email and password.” The legacy identifier is intentionally retained. The administration access/mobile messages already describe both roles.

## Acceptance and reviewer verification

1. Exact `dotnet format TripMate.slnx --verify-no-changes --no-restore` succeeds, including FINALNEWLINE and whitespace. Formatting-only inherited violations may be repaired without changing behavior.
2. Both internal roles receive inclusive Google rejection copy, with unchanged codes, account status precedence and no mutation/session creation.
3. Existing Staff foundation, Web/Mobile contract, JWT/current-user and seven SQL migration cases pass. SQL Server 2022 and Redis suites execute without skips in CI; opt-in external suites must be reported separately.
4. Release build, unit/integration and full regression succeed. PR description links this spec and its plan and states approval pending. Re-review readiness is technical handoff, not approval or merge readiness.
