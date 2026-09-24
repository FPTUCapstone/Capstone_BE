
UC-06 Reset Password — Specification Rev 1.4

Document Status: DRAFT — AWAITING DEVELOPER APPROVAL

Authoritative decisions

ZERO DATABASE SCHEMA CHANGES.

Reset OTP state is ephemeral Backend memory.

OTP TTL = 3 minutes.

OTP is single-use.

Backend restart/redeploy invalidates outstanding OTPs.

Current design assumes one authoritative Backend process.

Previous dbo.PasswordResetCredentials design is withdrawn.

1. Goal

Guest can reset a TripMate password by email OTP:

enter registered email;

receive a 6-digit OTP;

submit OTP + new password;

Backend updates existing dbo.Users.password_hash;

Backend revokes all active refresh tokens.

Backend remains the sole password authority. Firebase Password Reset is not used.

2. Scope

In scope:

EMAIL-only reset.

Shared Backend API for Web + Mobile.

Backend-generated 6-digit OTP.

OTP state in Backend memory only.

OTP TTL 3 minutes.

Resend cooldown 60 seconds.

Maximum 5 real wrong-code attempts.

Enumeration protection.

Confirm-time eligibility re-check.

Existing password hash update and refresh-token revocation.

Web + Mobile integration.

Out of scope:

Phone/SMS.

Firebase Password Reset.

Reset links/deep links.

New DB table/column/index/constraint/trigger.

SQL migration scripts.

EF migrations.

Redis/distributed reset-state store.

Multi-instance reset-state synchronization.

Account activation/unlock/role/application-status changes.

3. Database rule

Database schema impact: NONE

UC-06 MUST NOT introduce:

new tables;

new columns;

new indexes;

new constraints;

triggers;

SQL migration scripts;

full-schema additions;

EF migrations.

UC-06 MUST NOT:

create dbo.PasswordResetCredentials;

create an equivalent reset table;

add OTP/reset fields to dbo.Users;

reuse dbo.RefreshTokens as reset-state storage.

Allowed existing-schema data changes:

update dbo.Users.password_hash;

update existing user timestamp according to current convention;

revoke existing dbo.RefreshTokens rows.

4. Reset-state model

Reset state exists only in Backend process memory.

Logical state:

UserId

protected OTP representation

CreatedAtUtc

ExpiresAtUtc

FailedAttemptCount

delivery state

invalidated/consumed state (a consumed or invalidated reset state is removed
from the current in-memory store — it is not a stored boolean/status marker —
and is therefore permanently unusable)

internal generation/version identifier

Delivery states:

Pending

Sent

Failed

Unknown

Backend restart/redeploy/crash clears outstanding reset state.

restart/redeploy
→ outstanding OTP invalid
→ user requests a new OTP

Current UC-06 assumes one authoritative Backend process. Multi-instance shared-state design is a separate future decision.

5. API contract

Base route: /api/v1/auth.

POST /password-reset/request

Request:

{ "email": "user@example.com" }

For every syntactically valid account-specific outcome:

{ "message": "If an account exists for this email, reset instructions have been sent." }

Return 200 OK for:

eligible account;

unknown email;

Google-only;

Locked/Inactive/ineligible;

account cooldown;

email delivery failure;

email delivery unknown/timeout.

Account-independent transport/IP/global limiter may return 429.

POST /password-reset/confirm

Request:

{
  "email": "user@example.com",
  "code": "042731",
  "newPassword": "..."
}

Success:

{ "message": "Your password has been reset. You can now sign in with your new password." }

No separate /verify endpoint.

6. Validation and errors

Validation uses ValidationProblemDetails.

Rules:

email: required, valid format, max 254 chars;

code: exactly 6 numeric chars, string, leading zero preserved;

newPassword: same policy as registration.

Business/system failures:

invalid/expired/consumed/superseded/exhausted/mismatched OTP → 400 ProblemDetails, MSG14;

transport/IP/global limiter → 429;

unexpected system/infrastructure failure → 5xx ProblemDetails, MSG127.

Success uses direct typed DTO. No legacy ApiResponse<T></t>.

7. OTP security

Mandatory:

EMAIL only;

6 numeric digits;

string representation;

leading zeros valid;

CSPRNG;

TTL = 3 minutes;

server-enforced UTC;

cooldown = 60 seconds;

max 5 real wrong attempts;

single-use;

raw OTP never persisted/logged;

protected representation only in memory;

secret-backed HMAC;

constant-time comparison where applicable.

HMAC binding:

OTP + userId + canonical CreatedAtUtc

8. Expiry and deletion

Example:

09:00 OTP generated
09:03 OTP expires

Before expiry + correct OTP:

reset succeeds;

state is consumed/removed;

OTP cannot be reused.

After expiry:

OTP invalid;

state removed or treated invalid;

user requests new OTP;

response remains generic MSG14.

9. Request/resend

For eligible local-password account:

normalize email;

enforce 60s cooldown;

generate 6-digit CSPRNG OTP;

create new in-memory Pending generation;

supersede previous generation;

send OTP;

matching current generation:

Delivered → may become Sent;

DefiniteFailure → unusable;

Unknown/timeout → unusable;

stale delivery result for older generation is ignored;

return generic 200.

Resend inside cooldown:

no new OTP;

no new email;

existing valid OTP remains valid;

same generic 200.

Concurrent eligible requests must produce at most one usable current generation.

10. Enumeration protection

Request must not reveal:

account existence;

account status;

provider type;

cooldown state;

delivery result.

Account-specific outcomes always use same generic 200.

Implementation must avoid an obvious reliable timing oracle.

11. Failed-attempt semantics

Increment exactly once only when:

submitted email resolves;

account is confirm-eligible;

current state exists;

state is Sent;

not consumed/invalidated/superseded;

not expired;

attempts < 5;

submitted OTP is a real HMAC mismatch.

Do not increment for:

unknown email;

no state;

Pending/Failed/Unknown;

expired;

consumed/replayed;

superseded/invalidated;

exhausted;

confirm-time ineligible.

5th real mismatch invalidates/removes current state.

User account is never locked by reset OTP failures.

12. Cross-account

OTP A + email B:

resolve B only;

inspect B state only;

never search other users by OTP/HMAC;

A untouched;

if B otherwise has valid state, mismatch increments B;

otherwise no reset-state mutation;

generic MSG14.

13. Confirm-time eligibility

Re-check account eligibility during confirm.

If account is now ineligible:

no password update;

no refresh-token revocation;

no failed-attempt increment;

invalidate/remove current in-memory reset state;

return generic 400 + MSG14.

Eligibility:

Active local-password → allowed;

PendingEmailVerification local-password → allowed, status unchanged;

Administrator local-password → allowed via shared API;

Locked → not allowed;

Inactive → not allowed;

Google-only → not allowed, no local password creation;

TourOperator approval_status is irrelevant.

UC-06 never changes account status, role, or operator approval status.

14. Successful reset

On valid OTP:

Within existing-schema DB transaction:

hash new password using IPasswordHasherService;

update existing dbo.Users.password_hash;

revoke all active refresh tokens;

commit.

Only after DB commit:

consume/remove in-memory reset state.

If DB transaction fails:

no partial password/session mutation;

reset state is not treated as successfully consumed;

return 5xx/MSG127.

Concurrent confirm of same OTP → exactly one success.

Existing access JWTs expire naturally.

15. Email behavior

Application-level email abstraction; SMTP implementation in Infrastructure.

Rules:

Domain does not depend on SMTP/MailKit;

handlers depend on port only;

secrets from User Secrets/environment/approved secret store;

no real secret committed;

late result from older generation cannot revive it.

16. Web

Feature:

src/features/auth/passwordRecovery/

Flow:

Forgot password
→ email
→ OTP
→ new password
→ completion
→ Sign-In

No Firebase Password Reset.
No legacy envelope.
OTP/newPassword transient only; no browser storage/URL/log/analytics persistence.

17. Mobile

Feature:

lib/features/auth/password_recovery/

Architecture:

Page → Cubit → Repository → RemoteDataSource → DioClient

No Firebase Password Reset.
No legacy _unwrap.
OTP/newPassword transient only.
Completion returns to LoginPage.

18. Test expectations

Existing UC-06 test IDs remain authoritative through UC06-35I.

Rev 1.4 corrections:

reset state is memory-only;

TTL = 3 minutes;

restart/new store instance invalidates outstanding OTP;

resend supersedes previous generation;

stale provider result cannot revive older generation;

concurrent wrong attempts do not lose increments;

successful reset updates existing password hash + revokes existing refresh tokens;

no reset table/column/index/migration is expected.

No new UC06-* IDs are introduced by the Plan.

19. Definition of Done

Backend:

dotnet build TripMate.slnx
dotnet test TripMate.slnx
dotnet format TripMate.slnx --verify-no-changes
git diff --check

Also:

zero UC-06 DB schema changes;

zero EF migrations;

zero reset SQL migrations;

OTP TTL = 3 minutes everywhere;

no raw OTP/password/SMTP secret logs;

API/OpenAPI consistency;

Web/Mobile contract agreement.

20. Final decisions

Reset channel: EMAIL ONLY
OTP: 6 digits
OTP TTL: 3 minutes
Resend cooldown: 60 seconds
Max wrong attempts: 5
OTP storage: Backend memory only
DB schema change: ZERO
OTP single-use: YES
Restart/redeploy invalidates outstanding OTP: YES
Deployment assumption: one authoritative Backend process
Success: update existing password_hash + revoke all refresh tokens

End of UC-06 Reset Password Specification Rev 1.4.
