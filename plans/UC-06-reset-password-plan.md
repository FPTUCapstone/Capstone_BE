
UC-06 Reset Password — Implementation Plan Rev 6

Plan Status: DRAFT — AWAITING DEVELOPER APPROVAL

Authoritative Specification: UC-06 Reset Password Specification Rev 1.4

ZERO DB schema changes.

Reset state in Backend memory.

OTP TTL = 3 minutes.

OTP single-use.

Backend restart/redeploy invalidates outstanding OTPs.

Current reset-state design assumes one authoritative Backend process.

1. Workflow

Revised Spec approved
→ Revised Plan approved
→ TDD task by task
→ Open Code Review after every task
→ Full validation
→ Developer chooses delivery

Hard gates:

Plan not APPROVED FOR IMPLEMENTATION → no code.

Current task review not PASS → no next task.

Full validation not PASS → no delivery.

No explicit developer instruction → no commit/push/PR/merge/cleanup.

Review prompt:

Review my current changes using Open Code Review

2. Baseline

Existing clean Backend baseline remains valid:

Tests: 418 total
Passed: 402
Failed: 0
Skipped: 16
Build: PASS
Format: PASS
git diff --check: PASS
Clean baseline worktree: PASS

Aborted DB Task 1 was cleaned up completely.

Preserve pre-existing SMTP/document WIP.

3. Database boundary

Forbidden:

reset table;

reset columns;

reset indexes;

reset constraints;

triggers;

reset SQL migration;

full-schema reset additions;

EF migrations;

OTP/reset fields on Users;

RefreshTokens as reset-state storage.

Allowed existing-schema updates:

Users.password_hash;

existing refresh-token revocation.

4. Architecture

Concern

Decision

Password authority

existing Backend dbo.Users.password_hash

Reset state

process-local Backend memory

State abstraction

Application IPasswordResetStateStore

Implementation

Infrastructure in-memory store

OTP

CSPRNG, 6-digit string

TTL

3 minutes

Cooldown

60 seconds

Wrong attempts

5

Protection

HMAC-SHA-256 over OTP + userId + canonical CreatedAtUtc

Clock

existing IDateTimeProvider

Email

Application port + Infrastructure SMTP adapter

API

two shared anonymous endpoints

Final DB mutation

existing Users + RefreshTokens only

FE

src/features/auth/passwordRecovery/

Mobile

lib/features/auth/password_recovery/

No Redis/distributed cache.

5. In-memory state store

Logical state:

UserId

protected OTP

CreatedAtUtc

ExpiresAtUtc = CreatedAtUtc + 3 minutes

FailedAttemptCount

DeliveryState

invalidated/consumed marker

(the logical consumed/invalidated terminal state is represented by removing the
reset state from the current in-memory store — not by a stored boolean/status
marker — and removed state is therefore permanently unusable)

internal generation/version

Delivery states:

Pending

Sent

Failed

Unknown

Required behavior:

get current state by user;

race-safe issue/replace;

60s cooldown;

3-minute expiry;

guarded delivery transition for same generation;

atomic wrong-attempt increment;

5th-attempt invalidation;

consume/remove after success;

invalidate/remove on confirm-time eligibility failure.

Internal generation:

memory-only;

not exposed;

not HMAC input;

prevents stale result from mutating a newer generation.

6. Expiry

TTL = exactly 3 minutes.

After expiry:

state unusable;

confirm → generic MSG14;

cleanup may be eager or lazy;

new eligible request may issue another OTP subject to cooldown.

New store instance/restart:

previous reset state unavailable;

previous OTP invalid.

7. Concurrency

Correctness required within one Backend process.

Use per-account synchronization inside the state-store implementation.

Must guarantee:

concurrent request → at most one current usable generation;

stale provider result cannot mutate newer generation;

concurrent wrong attempts do not lose increments;

5th mismatch invalidates;

concurrent confirms → exactly one successful reset;

confirm-time invalidation cannot revive.

No new DB lock/schema mechanism for reset state.

8. OTP security services

Application:

IOtpCodeGenerator

IOtpProtectionService

Production:

CryptographicOtpCodeGenerator

HmacOtpProtectionService

Generation:

RandomNumberGenerator.GetInt32(0, 1_000_000)
→ D6

Protection:

HMAC-SHA256(OTP + userId + canonical CreatedAtUtc, OtpPepper)

Use fixed-time comparison.

Pepper from User Secrets/environment/approved secret store.

Raw OTP:

transient memory only;

transient email payload;

never DB;

never persistent cache;

never logs.

9. Email abstraction

Re-inspect and reuse existing SMTP scaffold where compatible.

Application:

IEmailSender

EmailDeliveryResult

IPasswordResetEmailQueue for a non-blocking, bounded, process-local handoff;

Results:

Delivered

DefiniteFailure

Unknown

Infrastructure:

Gmail SMTP/MailKit according to current config;

bounded Channel queue with a hosted delivery worker;

several bounded consumers prevent one slow SMTP attempt from blocking every account;

timeout → Unknown;

rejection → DefiniteFailure.

Only matching current generation may transition to Sent.

Older/stale generation result is ignored.

10. Request handler

Flow:

validate/normalize email;

resolve account;

evaluate eligibility;

eligible account enters per-account synchronization;

enforce 60s cooldown;

if not cooling down:

generate OTP;

create new Pending generation with 3-minute expiry;

supersede previous generation;

enqueue OTP delivery without waiting for SMTP;

if the bounded queue is full, invalidate the matching generation and fail closed;

the worker checks the pending generation, releases the account lock before SMTP, then
re-enters the lock and generation-guards the delivery result;

if an older SMTP send is already in flight when a resend supersedes it, that older email may
still arrive, but its OTP is unusable and its late result cannot mutate the newer generation;

guarded transition:

Delivered → Sent;

DefiniteFailure → unusable;

Unknown/timeout → unusable;

normalize response timing;

generic 200.

Unknown/ineligible/cooldown/delivery outcomes remain externally indistinguishable.

11. Confirm handler

Flow:

validate input;

resolve account from submitted normalized email only;

enter account synchronization;

get current reset state;

re-check eligibility;

if ineligible:

invalidate/remove state;

no attempt increment;

no password/token mutation;

generic 400 + MSG14;

verify:

state exists;

Sent;

not consumed/invalidated/superseded;

current UTC < ExpiresAtUtc;

attempts < 5;

compare HMAC;

real mismatch:

increment exactly once;

5th mismatch invalidates;

generic MSG14;

correct OTP:

existing-schema DB transaction:

hash/update password;

revoke all refresh tokens;

commit;

consume/remove memory state;

return 200.

DB failure:

rollback password/token changes;

do not mark state successfully consumed;

standard 5xx/MSG127.

12. Cross-account

OTP A + email B:

resolve B;

inspect B state only;

never search OTP globally;

A untouched;

if B otherwise-valid state exists, mismatch increments B;

otherwise no reset-state mutation;

generic MSG14.

13. API

Add only:

POST /api/v1/auth/password-reset/request

POST /api/v1/auth/password-reset/confirm

Both anonymous.

Contract:

direct DTO success;

validation → ValidationProblemDetails;

invalid reset → 400 + MSG14;

global/transport limiter → 429;

system failure → 5xx + MSG127;

no legacy envelope.

Controller stays thin.

14. Frontend

Feature:

src/features/auth/passwordRecovery/

Flow:

email → OTP → new password → completion → Sign-In

No Firebase Password Reset.
No legacy envelope.
OTP/newPassword transient only.

15. Mobile

Feature:

lib/features/auth/password_recovery/

Architecture:

Page → Cubit → Repository → RemoteDataSource → DioClient

No Firebase Password Reset.
No _unwrap.
OTP/newPassword transient only.
Return to LoginPage.

16. TDD tasks

Every task:

RED
→ GREEN
→ REFACTOR
→ validation
→ Review my current changes using Open Code Review
→ PASS
→ next task

Task 1 — In-memory reset-state foundation

Implement only:

state model;

IPasswordResetStateStore;

in-memory implementation;

per-account synchronization;

3-minute expiry;

60s cooldown;

generation replacement;

stale-generation protection;

atomic failed-attempt increment;

5th-attempt invalidation;

consume/remove;

confirm-time invalidate/remove.

RED tests:

issue/get;

3-minute expiry;

cooldown;

replacement;

stale generation ignored;

concurrent issue → one current generation;

concurrent wrong attempts → no lost increments;

fifth mismatch invalidates;

consume removes;

new store instance → old state unavailable.

No email/handler/API.

STOP → Open Code Review.

Task 2 — Security services + contracts

DTOs;

validators;

security options;

CSPRNG;

HMAC;

eligibility resolver.

STOP → Open Code Review.

Task 3 — Email abstraction

inspect existing SMTP WIP;

Application email port;

Infrastructure adapter;

bounded process-local delivery queue + hosted worker;

non-blocking HTTP handoff and guarded asynchronous delivery transitions;

timeout/failure mapping;

secret/log hygiene.

STOP → Open Code Review.

Task 4 — Request handler

generic request;

cooldown;

OTP generation;

state generation;

non-blocking delivery enqueue;

timing normalization.

STOP → Open Code Review.

Task 5 — Confirm handler

confirm eligibility;

TTL enforcement;

attempt policy;

cross-account;

existing-schema password + refresh-token transaction;

consume state only after DB commit.

STOP → Open Code Review.

Task 6 — API + limiter

endpoints;

ProblemDetails;

transport/global limiter;

API tests.

STOP → Open Code Review.

Task 7 — Backend integration/security

request→confirm;

concurrency;

restart/new-store behavior;

existing SQL transaction atomicity;

zero-schema-change guard.

STOP → Open Code Review.

Task 8 — Frontend

Baseline first, implement FE, review.

Task 9 — Mobile

Baseline first, implement Mobile, review.

Task 10 — Full validation

Validation only.

PASS:

report evidence
→ STOP
→ developer chooses delivery

17. Test strategy

State-store:

no raw OTP storage;

one current generation;

TTL exactly 3 minutes;

expiry;

cooldown;

supersession;

stale result rejection;

atomic attempts;

5th invalidation;

consume/remove;

restart/new instance clears state.

Security:

CSPRNG;

six-digit string;

leading zero;

HMAC binding;

secret config.

API:

generic request response;

ValidationProblemDetails;

MSG14 equivalence;

429 account-independent;

MSG127 5xx;

no information leak;

no legacy envelope.

SQL Server:

existing password hash update;

all refresh tokens revoked;

forced failure leaves no partial mutation.

Schema guard:

no PasswordResetCredentials;

no reset SQL migration;

no reset full-schema additions;

no EF migrations;

no reset columns/indexes/constraints.

18. Validation

Backend:

dotnet build TripMate.slnx --nologo
dotnet test TripMate.slnx --nologo --no-build
dotnet format TripMate.slnx --verify-no-changes --no-restore
git diff --check

Additionally:

OTP TTL = 3 minutes;

ZERO UC-06 DB schema changes;

ZERO reset SQL migrations;

ZERO EF migrations.

Frontend/Mobile follow repository AGENTS.md.

19. Delivery

Full Validation PASS does not authorize delivery.

PASS
→ report evidence
→ STOP
→ developer chooses commit/push/PR/nothing

No automatic commit/push/PR/merge/cleanup.

20. Approval gate

Plan Status: DRAFT — AWAITING DEVELOPER APPROVAL

Implementation remains stopped until developer changes status to:

APPROVED FOR IMPLEMENTATION

End of UC-06 Reset Password Implementation Plan Rev 6.
