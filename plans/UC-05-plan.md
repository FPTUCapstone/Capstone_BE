# UC-05 — Sign Out Implementation Plan

> **PLAN STATUS: APPROVED FOR IMPLEMENTATION**
> UC-05 implementation is approved to proceed according to this plan. Implementation must remain strictly within the approved scope, tasks T01–T08, invariants, and TC-01 through TC-14.
> Specification file: [`specs/UC-05-spec.md`](../specs/UC-05-spec.md) — APPROVED FOR PLANNING.

---

## 1. Plan Status

| Item | Value |
|---|---|
| Plan Revision | 1.4 — APPROVED FOR IMPLEMENTATION (Process Alignment Amendment) |
| Specification Revision | 2.1 — APPROVED FOR PLANNING |
| Date | 2026-09-16 |
| Branch | `feature/PhucTV-sign-out` (implementation branch; previously recorded here as `feature/PhucTV-sign-in`) |
| Branch dependency | `feature/PhucTV-sign-out` is **stacked on the unmerged UC-04 sign-in branch** `feature/PhucTV-sign-in` (HEAD inherited `0eb7361 feat(auth): complete UC-04 mobile sign-in backend support`; merge-base with `develop` = `03469ee`). The delivery/merge order must therefore declare this dependency: the UC-04 sign-in branch merges first, or the UC-05 PR targets that branch instead of `develop`. UC-05 introduces no change to UC-04 behavior, and all UC-05 validation in this plan was run on this stacked branch. |
| Historical Pre-UC-05 Baseline | `dotnet test TripMate.slnx` → **391 total / 383 passed / 0 failed / 8 skipped** (stale after committed UC-18 tests entered the branch) |
| Verified Implementation-Start Baseline | `dotnet test TripMate.slnx --no-restore` → **418 total / 402 passed / 0 failed / 16 skipped** |
| Baseline Drift Attribution | Committed UC-18 tests: **+27 total / +19 passed / +0 failed / +8 skipped** |

> UC-05 implementation is approved to proceed according to this plan. Implementation must remain strictly within the approved scope, tasks T01–T08, invariants, and TC-01 through TC-14.

---

## 2. Scope & Locked Decisions

### 2.1 In Scope
- `POST /api/v1/auth/logout` — Mobile sign-out (JSON body, `refreshToken` nullable)
- `POST /api/v1/auth/web/logout` — Web sign-out (HttpOnly cookie `tripmate_refresh`)
- `SignOutCommand` + `SignOutCommandHandler` (Application layer)
- `SignOutRequestDto` + `SignOutResponseDto` (direct raw DTO, no legacy envelope)
- `WebRefreshCookie.Delete()` (new static method using explicit `Response.Cookies.Append` with empty value, `MaxAge = TimeSpan.Zero`, `Expires = DateTimeOffset.UnixEpoch`, and matching `CookieOptions`)
- Unit tests: `SignOutCommandHandlerTests.cs`
- Integration tests: `SignOutIntegrationTests.cs`
- Log sanitization coverage for sign-out in `LogSanitizationTests.cs`

### 2.2 Known SRS Documentation Deviation
SRS Report 3 §3.2.5 PC-02 and BR-12 literally require an access-token server-side blacklist. This plan intentionally departs from that wording. The approved engineering decision is:
- Revoke current-session refresh token in `dbo.RefreshTokens` only.
- Client clears local auth state immediately.
- Already-issued access JWTs drain naturally (~15 min).
- No Redis, no IMemoryCache blacklist, no per-request JWT lookup, no `OnTokenValidated` hook.

Document reconciliation of the SRS wording is deferred to a later documentation task.

**Additional known deviation — SRS §3.2.5 BR-13 (client-state termination):** SRS BR-13 requires the client to clear local session data *even when the server-side invalidation cannot be completed*. For the UC-05 **Web** flow this is intentionally superseded by the developer-approved Frontend failure policy **D1** (`Capstone_FE/specs/UC-05-web-spec.md` §14, approved 2026-09-17), which is **session-preserving**: on network error or non-2xx the Web client keeps its authenticated state, shows the approved safe error and allows retry, clearing local state only after a confirmed HTTP 200. Reason: the refresh session and the HttpOnly `tripmate_refresh` cookie may still be valid after a failed logout request, so a local-only clear could allow silent re-authentication during a later cold restore/F5. **Code change required: NO** — Backend behavior is unchanged (revoke matching refresh row, cookie deletion, idempotent 200, 500 ProblemDetails on infrastructure failure). SRS reconciliation is deferred alongside the PC-02/BR-12 deviation. This note does not alter task order, TDD evidence, the implementation footprint, or the recorded validation results; the full decision record lives in `specs/UC-05-spec.md` §5 (BR-13) and §16.

### 2.3 Locked Architecture (Do Not Reopen)
- Stateless JWT model — zero access-token blacklist mechanism
- Single-session sign-out only — no `WHERE user_id = @UserId` bulk revocation
- Database-First — zero EF Core migrations, zero schema changes
- No new abstractions (no generic repository, no new service interface)
- No `ExecuteUpdateAsync` optimization — tracked entity mutation path
- No refresh-token rotation
- No JWT claim redesign
- No access-token lifetime change

---

## 3. Current-Code Reconnaissance

### 3.1 File Inventory — Existing Code Relied On

| File | Current Responsibility | UC-05 Reuse | Modification? |
|---|---|---|---|
| `src/TripMate.Api/Controllers/V1/AuthController.cs` | Handles all auth endpoints; `[AllowAnonymous]` at class level; injects `ISender`, `IWebHostEnvironment` | Add `Logout()` and `WebLogout()` actions | **MODIFY** |
| `src/TripMate.Api/Common/WebRefreshCookie.cs` | Static `Append()` method: Name/Path constants; HttpOnly, SameSite=Lax, Path=/api/v1/auth, Secure conditional | Add `Delete()` method using explicit `Response.Cookies.Append` with empty value, `MaxAge = TimeSpan.Zero`, `Expires = DateTimeOffset.UnixEpoch`, and matching CookieOptions (no Domain) | **MODIFY** |
| `src/TripMate.Api/Common/ApiControllerBase.cs` | `Success<T>()`, `Error()`, `HandleFailure()`. Logout uses `Ok()` directly per TEAM_ENGINEERING_RULES §9 | No changes needed | **NO CHANGE** |
| `src/TripMate.Api/Middleware/ExceptionHandlingMiddleware.cs` | Catches unhandled exceptions; sets StatusCode=500; writes ProblemDetails (`Title = "An unexpected error occurred."`); does NOT call `Response.Clear()` | No changes — see §6 for TC-14 analysis | **NO CHANGE** |
| `src/TripMate.Api/Program.cs` | Registers ExceptionHandlingMiddleware as outermost middleware; configures Serilog ASP.NET Core request logging | No changes | **NO CHANGE** |
| `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs` | Declares `DbSet<RefreshToken> RefreshTokens` and `SaveChangesAsync()` | Reused directly | **NO CHANGE** |
| `src/TripMate.Application/Common/Interfaces/IJwtTokenService.cs` | Declares `HashRefreshToken(string)` — SHA-256 of raw token | Reused directly | **NO CHANGE** |
| `src/TripMate.Application/Common/Interfaces/IDateTimeProvider.cs` | `UtcNow { get; }` | Reused directly | **NO CHANGE** |
| `src/TripMate.Domain/Entities/RefreshToken.cs` | `TokenHash`, `ExpiresAtUtc`, `RevokedAtUtc?`, `IsActive` computed. Inherits `BaseEntity` (Id long) | Reused; `RevokedAtUtc` is the mutation target | **NO CHANGE** |
| `src/TripMate.Infrastructure/Persistence/Configurations/RefreshTokenConfiguration.cs` | Maps `dbo.RefreshTokens`; `revoked_at` → `RevokedAtUtc`; change tracking on by default; no index on `token_hash` | Reused; no changes | **NO CHANGE** |
| `src/TripMate.Application/Features/Authentication/WebRefresh/WebRefreshCommand.cs` | Pattern reference: uses `HashRefreshToken`, `AsNoTracking` for read-only; rejects expired tokens | Pattern only; UC-05 handler uses tracked path and different semantics | **NO CHANGE** |
| `tests/TripMate.Application.UnitTests/TestUtilities/FakeServices.cs` | `FakeJwtTokenService`, `FakeDateTimeProvider`, `FakePasswordHasher` | UC-05 unit tests reuse all three | **NO CHANGE** |
| `tests/TripMate.Application.UnitTests/TestUtilities/TestDbContext.cs` | In-memory `IApplicationDbContext` for unit tests; `TestDbContext.Create()` | UC-05 unit tests use `TestDbContext.Create()` | **NO CHANGE** |
| `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs` | Sealed `WebApplicationFactory<Program>`; registers sealed `TestApiDbContext` with a per-factory named EF InMemory store; exposes `WithDbContextAsync()` and `CreateClient()` | Add an optional test-only `SaveChangesInterceptor` constructor parameter and pass it to the existing `AddDbContext<TestApiDbContext>` options for deterministic TC-14 fault injection | **MODIFY (TEST ONLY)** |
| `tests/TripMate.Api.IntegrationTests/Authentication/LogSanitizationTests.cs` | Captures Serilog file sink; asserts no raw tokens in log | Extend with a UC-05 sign-out scenario for TC-09 | **MODIFY** |

### 3.2 Key Observations

1. **No `Logout` action exists** in `AuthController`. The class carries `[AllowAnonymous]` at class level — new endpoints automatically inherit this.

2. **`WebRefreshCookie` has no `Delete()` method.** Only `Append()` exists. `Append()` does NOT set `Domain`, so `Delete()` must NOT set `Domain` either. `Append()` characteristics: `HttpOnly = true`, `SameSite = SameSiteMode.Lax`, `Path = "/api/v1/auth"`, `Secure` = conditional on HTTPS or non-localhost dev. `WebRefreshCookie.Delete()` will use an explicit `Response.Cookies.Append(Name, string.Empty, options)` call with `MaxAge = TimeSpan.Zero`, `Expires = DateTimeOffset.UnixEpoch`, and matching CookieOptions (`Path`, `HttpOnly`, `SameSite`, conditional `Secure`, no `Domain`). **Important:** `Response.Cookies.Delete(...)` is NOT used because its current ASP.NET Core implementation sets `Expires = DateTimeOffset.UnixEpoch` but does NOT set `MaxAge`, meaning it does not satisfy the contract requiring explicit `Max-Age=0`. The explicit Append-based approach guarantees both attributes.

3. **`ExceptionHandlingMiddleware` is the outermost middleware.** It does NOT call `Response.Clear()` anywhere. Therefore `Set-Cookie` headers written before an exception is thrown survive in the HTTP 500 response — see §6.

4. **Expired but non-revoked tokens:** The approved spec does not create an expiry-rejection gate in sign-out. A session row that is not yet revoked but is past `ExpiresAtUtc` will have `RevokedAtUtc` stamped (administrative audit clarity). An already-revoked row's timestamp is preserved.

5. **`ApiControllerBase.Success<T>()`** wraps in the legacy `ApiResponse<T>` envelope. New UC-05 endpoints must use `Ok(new SignOutResponseDto())` directly — NOT `Success()`. This matches `TEAM_ENGINEERING_RULES.docx §9`.

6. **`AuthController` constructor already injects `IWebHostEnvironment`** — no new dependencies required.

7. **MediatR handler registration** is auto-discovered by `AddMediatR` scanning `TripMate.Application` assembly. No manual registration required.

8. **No `token_hash` index** on `dbo.RefreshTokens` — confirmed. Non-blocking follow-up out of scope for UC-05.

9. **ProblemDetails Title Observation:** Existing `ExceptionHandlingMiddleware` outputs `Title = "An unexpected error occurred."`, whereas `specs/UC-05-spec.md` error table sample lists `"A system error occurred."` UC-05 reuses the existing global `ExceptionHandlingMiddleware` without modification; no global middleware or title changes will be made for UC-05.

10. **TC-14 factory constraint:** `TripMateApiFactory` and `TestApiDbContext` are both currently `sealed`; subclass-based replacement is unsupported. The compatible seam is the existing `AddDbContext<TestApiDbContext>(options => options.UseInMemoryDatabase(_databaseName))` registration. An optional test-only `SaveChangesInterceptor` can be added to those same options without replacing the context, its `DbSet` queries, or the per-factory backing store.

---

## 4. Implementation Strategy

### 4.1 Mobile Sign-Out (`POST /api/v1/auth/logout`)
- Binds `SignOutRequestDto` from JSON body (`refreshToken` nullable)
- Dispatches `SignOutCommand(request.RefreshToken)` via `Sender.Send()`
- Returns `Ok(new SignOutResponseDto())` — raw DTO, no envelope
- No cookie logic, no DB access, no hashing in controller

### 4.2 Web Sign-Out (`POST /api/v1/auth/web/logout`)
- Reads `Request.Cookies[WebRefreshCookie.Name]`
- If cookie is non-null/non-whitespace: dispatches `SignOutCommand(cookieValue)` inside `try` block
- If cookie is null/empty: skips handler dispatch (no DB mutation)
- In ALL cases (via `finally`): calls `WebRefreshCookie.Delete(HttpContext, environment)`
- On NORMAL / IDEMPOTENT completion (active token, already revoked token, unknown token, expired token, or missing cookie): returns `Ok(new SignOutResponseDto())` (HTTP 200) + cookie deletion header.
- On unexpected handler/database/infrastructure failure: exception is NOT swallowed; `finally` executes to write cookie deletion header, and exception propagates to `ExceptionHandlingMiddleware` resulting in HTTP 500 ProblemDetails (never swallowed or converted to false HTTP 200).

### 4.3 `SignOutCommand` Handler — Algorithm

```
if string.IsNullOrWhiteSpace(RefreshToken) → return Result.Success()

hash = jwtTokenService.HashRefreshToken(RefreshToken)

session = dbContext.RefreshTokens                           // tracked (no AsNoTracking)
    .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)

if session is null → return Result.Success()               // unknown token, no-op

if session.RevokedAtUtc is not null → return Result.Success() // already revoked, no-op

session.RevokedAtUtc = dateTimeProvider.UtcNow             // stamp revocation
await dbContext.SaveChangesAsync(ct)                        // one call, only for active sessions
return Result.Success()
```

- No `ExecuteInTransactionAsync` — single-entity update needs no explicit transaction
- No logger injected in handler (consistent with `WebRefreshCommand.cs` pattern)
- Does NOT check `ExpiresAtUtc` as a rejection gate — expired-but-not-revoked sessions get `RevokedAtUtc` stamped

### 4.4 `WebRefreshCookie.Delete()`

```csharp
public static void Delete(HttpContext context, IWebHostEnvironment environment)
{
    context.Response.Cookies.Append(Name, string.Empty, new CookieOptions
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps
            || !environment.IsDevelopment()
            || !context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase),
        SameSite = SameSiteMode.Lax,
        Path = Path,
        MaxAge = TimeSpan.Zero,
        Expires = DateTimeOffset.UnixEpoch
    });
}
```

Explicit `Response.Cookies.Append`-based deletion with an empty value. Uses `MaxAge = TimeSpan.Zero` (emits `Max-Age=0`) AND `Expires = DateTimeOffset.UnixEpoch` (emits `Expires=Thu, 01 Jan 1970 00:00:00 GMT`). Both attributes together guarantee full browser deletion semantics.

**Why NOT `Response.Cookies.Delete()`:** The current ASP.NET Core `ResponseCookies.Delete(...)` implementation sets `Expires = DateTimeOffset.UnixEpoch` but leaves `MaxAge = null`, meaning it does NOT emit `Max-Age=0`. This does not satisfy the approved UC-05 contract requiring both deletion attributes. The explicit Append-based approach is the safe, contract-compliant choice.

CookieOptions scope matches `Append()` exactly: `Path = "/api/v1/auth"`, `HttpOnly = true`, `SameSite = SameSiteMode.Lax`, conditional `Secure` (same expression as `Append()`), NO `Domain` attribute.

### 4.5 DTOs (Application layer)

```csharp
namespace TripMate.Application.Features.Authentication.SignOut;

public sealed record SignOutRequestDto(string? RefreshToken);
public sealed record SignOutResponseDto(string Message = "Signed out successfully.");
```

### 4.6 Logging
- Existing Serilog ASP.NET Core request logging configured in `Program.cs` satisfies PC-04 ("The sign-out event is recorded for security auditing").
- Exact request-log fields proving the sign-out event:
  - `RequestMethod`: `"POST"`
  - `RequestPath`: `"/api/v1/auth/logout"` or `"/api/v1/auth/web/logout"`
  - `StatusCode`: `200` (or `500`)
  - `Elapsed`: execution time in milliseconds
  - Timestamp, Client IP / TraceId / ConnectionId
- Why no `ILogger` dependency is added to `SignOutCommandHandler`: Following existing codebase handler patterns (e.g. `WebRefreshCommand`), request-level audit logging is handled at the HTTP boundary by Serilog. Handlers avoid redundant logger dependencies unless detailed internal execution logging is required.
- Sanitization rules strictly enforced: Never log raw refresh tokens, cookie values/headers, access JWTs, `Authorization` headers, or passwords/hashes. TC-09 in `LogSanitizationTests` verifies the normal sign-out path. TC-14 independently reads the same configured Testing file sink after the injected `SaveChangesAsync` failure and verifies the same secret classes on the failure path; TC-09 success-path evidence alone is not used as proof for TC-14.

---

## 5. File-by-File Change Plan

### 5.1 Production Files

| Task | File | Change | Reason | Risk | Verification |
|---|---|---|---|---|---|
| **T01** | `src/TripMate.Api/Common/WebRefreshCookie.cs` | Add `Delete(HttpContext, IWebHostEnvironment)` static method using explicit `Response.Cookies.Append` with empty value, `MaxAge = TimeSpan.Zero`, `Expires = DateTimeOffset.UnixEpoch`, and matching CookieOptions | Cookie cleanup for Web logout; matches `Append()` scope exactly (Path, SameSite, HttpOnly, conditional Secure, no Domain) | Low | TC-03, TC-04, TC-06, TC-14: `Set-Cookie` deletion header present with `tripmate_refresh=`, `Path=/api/v1/auth`, `SameSite=Lax`, `HttpOnly`, `Max-Age=0`, `Expires=Thu, 01 Jan 1970 00:00:00 GMT`; `Secure` asserted when applicable per existing WebRefreshCookie conditional policy |
| **T02** | `src/TripMate.Application/Features/Authentication/SignOut/SignOutRequestDto.cs` | New file: `sealed record SignOutRequestDto(string? RefreshToken)` | Mobile request contract per spec §7.1 | Low | Compilation; TC-01/TC-10 body binding |
| **T03** | `src/TripMate.Application/Features/Authentication/SignOut/SignOutResponseDto.cs` | New file: `sealed record SignOutResponseDto(string Message = "Signed out successfully.")` | Direct success DTO per TEAM_ENGINEERING_RULES §9 | Low | All TCs: response body `{ "message": "Signed out successfully." }` |
| **T04** | `src/TripMate.Application/Features/Authentication/SignOut/SignOutCommand.cs` | New file: `SignOutCommand(string?)` record + `SignOutCommandHandler : IRequestHandler<SignOutCommand, Result>` using `IApplicationDbContext`, `IJwtTokenService`, `IDateTimeProvider` | Core business logic — revocation semantics | Medium | TC-01..TC-13; TC-08 invariants; unit + integration tests |
| **T05** | `src/TripMate.Api/Controllers/V1/AuthController.cs` | Add `[HttpPost("logout")] Logout(SignOutRequestDto, ct)` and `[HttpPost("web/logout")] WebLogout(ct)` actions | Expose HTTP endpoints per spec §7.1 and §7.2 | Low | TC-01..TC-14 integration tests |

### 5.2 Test Files

| Task | File | Change | Reason | Risk | Verification |
|---|---|---|---|---|---|
| **T06** | `tests/TripMate.Application.UnitTests/Features/Authentication/SignOut/SignOutCommandHandlerTests.cs` | New file: unit tests for `SignOutCommandHandler` | Cover TC-05, TC-08, TC-10, TC-11 in isolation | Low | `dotnet test --filter Class=SignOutCommandHandlerTests` |
| **T07** | `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`; `tests/TripMate.Api.IntegrationTests/Authentication/SignOutIntegrationTests.cs` | Add an optional test-only `SaveChangesInterceptor` hook to the existing in-memory registration; add HTTP integration tests, including one-shot TC-14 failure injection and failure-path file-sink inspection | Cover TC-01..TC-07 and TC-12..TC-14 against the full stack; prove TC-14 reaches the real tracked-query/`SaveChangesAsync` path and leaks no auth secrets when it fails | Low | `dotnet test --filter Class=SignOutIntegrationTests`; TC-14 asserts the fault fired exactly once, HTTP/cookie contract, and failure-path log sanitization |
| **T08** | `tests/TripMate.Api.IntegrationTests/Authentication/LogSanitizationTests.cs` | Add normal sign-out scenario to existing log sanitization test | TC-09: verify no refresh token, cookie/header, JWT/Authorization, or password/hash in normal sign-out log output; TC-14 separately proves the injected-failure path in T07 | Low | `dotnet test --filter Class=LogSanitizationTests` |

---

## 6. Web Failure Cleanup Decision (Critical — TC-14)

### 6.1 Current Exception Pipeline Analysis

**Middleware registration order in `Program.cs`:**
```
app.UseMiddleware<ExceptionHandlingMiddleware>();   // outermost — registered first
app.UseCors(...)
app.UseHttpsRedirection()
app.UseAuthentication()
app.UseAuthorization()
app.MapControllers()                               // innermost
```

**`ExceptionHandlingMiddleware` behavior on unhandled exception:**
```csharp
catch (Exception ex)
{
    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
    context.Response.ContentType = "application/problem+json";
    var problem = new ProblemDetails { Title = "An unexpected error occurred.", Status = 500 };
    await context.Response.WriteAsJsonAsync(problem, ...);
}
```

**Key finding — `Response.Clear()` is NOT called anywhere:**
- The middleware only sets `StatusCode`, `ContentType`, and writes a ProblemDetails body (`Title = "An unexpected error occurred."`, `Status = 500`).
- It does NOT reset or clear existing response headers.
- Therefore: **Set-Cookie headers already appended to the response header collection before the exception propagates will survive in the final HTTP 500 response.**

### 6.2 `try/finally` Analysis in `WebLogout`

```csharp
public async Task<IActionResult> WebLogout(CancellationToken ct)
{
    var cookie = Request.Cookies[WebRefreshCookie.Name];
    IActionResult result;
    try
    {
        if (!string.IsNullOrWhiteSpace(cookie))
            await Sender.Send(new SignOutCommand(cookie), ct);  // may throw
        result = Ok(new SignOutResponseDto());
    }
    finally
    {
        WebRefreshCookie.Delete(HttpContext, environment);      // always executes
    }
    return result;
}
```

**What happens on `Sender.Send()` throw:**
1. `finally` block executes — `WebRefreshCookie.Delete()` calls `context.Response.Cookies.Append(Name, string.Empty, options)` with `MaxAge = TimeSpan.Zero` and `Expires = DateTimeOffset.UnixEpoch` — the `Set-Cookie` deletion header (`tripmate_refresh=; Path=/api/v1/auth; SameSite=Lax; HttpOnly; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT`) is written to the response header collection.
2. Exception propagates out of `WebLogout` through MediatR → controller infrastructure → `ExceptionHandlingMiddleware`.
3. `ExceptionHandlingMiddleware` sets `StatusCode=500`, `ContentType=application/problem+json`, and writes ProblemDetails body.
4. **It does NOT clear headers.** The `Set-Cookie` deletion header remains intact.
5. HTTP 500 ProblemDetails response with `Set-Cookie` deletion header reaches the client. Exception is NOT swallowed into a false HTTP 200 response.

### 6.3 Decision

> **CONFIRMED: Current exception pipeline preserves Set-Cookie headers.**
> Controller-level `try/finally` with `WebRefreshCookie.Delete()` in the `finally` block is sufficient.
> No additional middleware, filter, or response-reset guard is required.
> UC-05 reuses the existing standard 500 ProblemDetails pipeline unchanged.

### 6.4 TC-14 Verification Strategy

**Selected failure injection: OPTION B — test-only EF Core `SaveChangesInterceptor` with a one-shot fault switch.**

This is compatible with the current code because `TripMateApiFactory` already configures `TestApiDbContext` through `AddDbContext` and all scopes created by one factory use the same `_databaseName` EF InMemory store. The plan does not subclass or replace `TripMateApiFactory` or `TestApiDbContext`; both are sealed. T07 adds an optional `SaveChangesInterceptor?` constructor argument to the factory and, when non-null, calls `options.AddInterceptors(interceptor)` on the existing `TestApiDbContext` options after `UseInMemoryDatabase(_databaseName)`. Existing callers omit the argument and retain identical behavior.

The test-local `ToggleSaveChangesFailureInterceptor : SaveChangesInterceptor` starts disabled and exposes `ArmNextSaveFailure()`. Its async saving callback uses a one-shot, thread-safe flag (for example, `Interlocked.Exchange`) to throw `InvalidOperationException("Simulated infrastructure failure for TC-14")` only on the next `SaveChangesAsync`. It also exposes an invocation/fault count so the test proves the ACT request reached this exact failure point.

**Exact execution sequence:**

1. Define marked values for the raw refresh token/cookie, an access JWT/Authorization header, a password, and its password hash. Reset the existing Testing Serilog file **before** creating the factory, matching the ordering already used by `LogSanitizationTests`, so the host has not opened the sink yet.
2. **Arrange — fault disabled:** Create one interceptor instance, pass it to one `TripMateApiFactory`, and create the HTTP client from that factory.
3. Resolve `IJwtTokenService` from the factory and compute the hash for the marked raw refresh token.
4. Call `factory.WithDbContextAsync(...)` while the interceptor remains disabled. Seed a user containing the marked password hash and a matching `RefreshToken` with the token hash, `RevokedAtUtc = null`, and a future `ExpiresAtUtc`; call `SaveChangesAsync` successfully. This proves seeding does not trigger the injected failure.
5. **Enable failure after Arrange:** Call `interceptor.ArmNextSaveFailure()` only after seeding has completed.
6. **Act:** Send `POST /api/v1/auth/web/logout` with the matching `tripmate_refresh` cookie and marked `Authorization: Bearer ...` header.
7. **Expected handler path:** Hash cookie → query the same named InMemory backing store → select the seeded active row → assign `RevokedAtUtc` → call `SaveChangesAsync` → interceptor consumes its one-shot flag and throws during ACT.
8. **Expected HTTP path:** Controller `finally` calls `WebRefreshCookie.Delete` → exception propagates → existing `ExceptionHandlingMiddleware` retains the already-appended `Set-Cookie` header and returns HTTP 500 ProblemDetails.
9. Poll and read the existing Testing Serilog file with `FileShare.ReadWrite`, using the established `LogSanitizationTests` pattern, until the Web logout failure event appears; then perform failure-path secret assertions.

**Assertions:**
1. Seed save completed before arming; interceptor fault count is zero after Arrange and exactly one after Act, proving logout reached `SaveChangesAsync`.
2. `response.StatusCode == HttpStatusCode.InternalServerError`; response is not HTTP 200.
3. Response content type is `application/problem+json`; body has ProblemDetails `Status = 500` and the existing global title.
4. `response.Headers.Contains("Set-Cookie") == true`.
5. The deletion header contains `tripmate_refresh=`, `Path=/api/v1/auth`, `SameSite=Lax`, `HttpOnly`, `Max-Age=0`, and `Expires=Thu, 01 Jan 1970 00:00:00 GMT`; `Secure` is asserted when applicable under the existing conditional policy.
6. The captured log is non-vacuous: it contains the `/api/v1/auth/web/logout` failure event/path (and HTTP 500 outcome where emitted by the configured providers).
7. The same failure-path log does **not** contain the raw refresh token, encoded cookie value, full raw `Cookie` header, marked access JWT, full `Authorization` header, marked password, or marked password hash.

TC-14 performs its own failure-path log inspection in `SignOutIntegrationTests`; it is not satisfied by TC-09's normal-path test.

---

## 7. Database Impact

**NONE.**

| Item | Status |
|---|---|
| New SQL script | NONE |
| EF Core migration | NONE |
| Schema change | NONE |
| New table | NONE |
| New index | NONE |
| Token blacklist table | NONE — explicitly out of scope |
| `dbo.RefreshTokens` | Reused: existing `revoked_at` column stamped on active session sign-out |
| `dbo.Users` | Zero mutation |
| `dbo.OperatorProfiles` | Zero mutation |
| `database/tripmate_schema_v7.sql` | NOT MODIFIED |

---

## 8. Test Plan

### 8.1 Test Coverage Map

| TC | Scenario | Type | File | Planned Test Method & Assertions |
|---|---|---|---|---|
| TC-01 | Mobile Traveler active token logout; DB `revoked_at` stamped; HTTP 200 | Integration | `SignOutIntegrationTests` | `Post_Logout_Traveler_WithActiveToken_RevokesSessionAndReturns200` |
| TC-02 | Mobile TourOperator active token; `OperatorProfile.ApprovalStatus` and `User.Status` unchanged | Integration | `SignOutIntegrationTests` | `Post_Logout_TourOperator_DoesNotMutateOperatorProfile` |
| TC-03 | Web Administrator cookie logout; `Set-Cookie` deletion header emitted (`tripmate_refresh=; Path=/api/v1/auth; SameSite=Lax; HttpOnly; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT`); DB revoked | Integration | `SignOutIntegrationTests` | `Post_WebLogout_Administrator_ClearsCookieAndRevokesSession` — asserts `Set-Cookie` contains `Path=/api/v1/auth`, `SameSite=Lax`, `HttpOnly`, `Max-Age=0`, `Expires=Thu, 01 Jan 1970 00:00:00 GMT`; `Secure` asserted when applicable per WebRefreshCookie conditional policy |
| TC-04 | Web Traveler cookie logout; `Set-Cookie` deletion header emitted (`Path=/api/v1/auth`, `SameSite=Lax`, `HttpOnly`, `Max-Age=0`, `Expires=Thu, 01 Jan 1970 00:00:00 GMT`); subsequent `/web/refresh` returns HTTP 401 `AUTH_TOKEN_INVALID` | Integration | `SignOutIntegrationTests` | `Post_WebLogout_Traveler_RendersRefreshFails` — asserts `Set-Cookie` deletion scope attributes (`Path`, `SameSite`, `HttpOnly`, `Max-Age=0`, `Expires`); `Secure` asserted when applicable per WebRefreshCookie conditional policy |
| TC-05 | Already-revoked token; original `RevokedAtUtc` preserved; HTTP 200 | Unit + Integration | `SignOutCommandHandlerTests` + `SignOutIntegrationTests` | `Handle_AlreadyRevokedToken_ReturnsSuccessWithoutMutation` |
| TC-06 | Missing Web cookie; `Set-Cookie` deletion header emitted (`Path=/api/v1/auth`, `SameSite=Lax`, `HttpOnly`, `Max-Age=0`, `Expires=Thu, 01 Jan 1970 00:00:00 GMT`); HTTP 200; zero DB mutation | Integration | `SignOutIntegrationTests` | `Post_WebLogout_NoCookie_Returns200AndEmitsCookieDeletion` — asserts `Set-Cookie` deletion scope attributes (`Path`, `SameSite`, `HttpOnly`, `Max-Age=0`, `Expires`); `Secure` asserted when applicable per WebRefreshCookie conditional policy |
| TC-07 | Multi-device isolation: Device A revoked; Device B `/web/refresh` succeeds | Integration | `SignOutIntegrationTests` | `Post_Logout_DeviceA_LeavesDeviceB_SessionActive` |
| TC-08 | `User.Role`, `Status`, `LastLoginAtUtc`, `UpdatedAtUtc`, `PasswordHash`, `OperatorProfile.ApprovalStatus` — all unchanged | Unit | `SignOutCommandHandlerTests` | `Handle_SignOut_DoesNotMutateUserOrOperatorProfile` |
| TC-09 | No raw refresh token, cookie value/header, JWT/Authorization header, or password/hash in logs during normal sign-out | Integration | `LogSanitizationTests` | `SignOut_NeverLogsAuthenticationSecrets` — normal-path coverage; does not substitute for TC-14 failure-path assertions |
| TC-10 | Mobile `null`/`""`/whitespace token; HTTP 200; zero DB query | Unit + Integration | `SignOutCommandHandlerTests` + `SignOutIntegrationTests` | `Handle_NullOrWhitespaceToken_ReturnsSuccessWithZeroDbAccess` |
| TC-11 | Unknown/nonexistent token; HTTP 200; zero rows modified | Unit | `SignOutCommandHandlerTests` | `Handle_UnknownToken_ReturnsSuccessWithoutDbMutation` |
| TC-12 | Repeated logout; HTTP 200 each time; `RevokedAtUtc` not re-stamped after first revocation | Integration | `SignOutIntegrationTests` | `Post_Logout_Repeated_IdempotentlyPreservesRevokedTimestamp` |
| TC-13 | Revoked refresh token rejected by `/web/refresh` (HTTP 401 `AUTH_TOKEN_INVALID`) | Integration | `SignOutIntegrationTests` | `Post_Logout_RevokedToken_CannotBeRedeemedByWebRefresh` |
| TC-14 | Web DB failure after a matching active session is selected: one-shot `SaveChangesAsync` fault; cookie deletion survives; HTTP 500 ProblemDetails; never HTTP 200; failure-path logs contain no auth secrets | Integration (injected failure) | `SignOutIntegrationTests` | `Post_WebLogout_SaveChangesThrows_Returns500ClearsCookieAndDoesNotLogSecrets` — seeds with fault disabled, arms after Arrange, proves the interceptor fired exactly once during Act, asserts complete deletion header and 500 ProblemDetails, then inspects the same failure-path log for raw refresh token, cookie value/header, JWT/Authorization, password, and password-hash markers |

### 8.2 Unit Test Structure (`SignOutCommandHandlerTests`)

Pattern from `WebRefreshCommandHandlerTests`:
- `FakeJwtTokenService` (`HashRefreshToken("x")` → `"hashed:x"`)
- `FakeDateTimeProvider` (deterministic `UtcNow = new DateTimeOffset(2026, 1, 1, ...)`)
- `TestDbContext.Create()` — in-memory, `await using`
- Handler construction: `new SignOutCommandHandler(db, _jwtTokenService, _dateTimeProvider)`

### 8.3 Integration Test Structure (`SignOutIntegrationTests`)

Pattern from `WebRefreshIntegrationTests`:
- `using var factory = new TripMateApiFactory()`
- `using var client = factory.CreateClient()`
- `factory.WithDbContextAsync()` for seeding and DB state assertions
- Scoped service resolution for `IJwtTokenService` to compute expected hashes
- Cookie: `request.Headers.Add("Cookie", $"tripmate_refresh={Uri.EscapeDataString(rawToken)}")`
- Mobile: `PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = raw })`

### 8.4 TC-14 Failure Injection

```csharp
// Optional TEST-ONLY seam added to TripMateApiFactory's primary constructor:
SaveChangesInterceptor? saveChangesInterceptor = null

// Inside the existing AddDbContext<TestApiDbContext> registration:
services.AddDbContext<TestApiDbContext>(options =>
{
    options.UseInMemoryDatabase(_databaseName);
    if (saveChangesInterceptor is not null)
        options.AddInterceptors(saveChangesInterceptor);
});

// Local to SignOutIntegrationTests:
private sealed class ToggleSaveChangesFailureInterceptor : SaveChangesInterceptor
{
    private int _throwNextSave;
    private int _faultCount;

    public int FaultCount => Volatile.Read(ref _faultCount);

    public void ArmNextSaveFailure() =>
        Interlocked.Exchange(ref _throwNextSave, 1);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _throwNextSave, 0) == 1)
        {
            Interlocked.Increment(ref _faultCount);
            throw new InvalidOperationException(
                "Simulated infrastructure failure for TC-14");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
```

**Arrange/Act guard:** `WithDbContextAsync` seeds the user and matching active refresh row while `FaultCount == 0`. Only after that save succeeds does the test call `ArmNextSaveFailure()`. The HTTP request resolves a new scoped `TestApiDbContext`, but it uses the same factory `_databaseName` store and the same interceptor instance. The matching query therefore succeeds and the next save is the handler's revocation save, where the one-shot fault is consumed.

### 8.5 Final Implementability Check

| Question | Answer | Evidence |
|---|---|---|
| Can TC-14 seed an active token without triggering the injected failure? | **YES** | The interceptor defaults disabled; seeding and its `SaveChangesAsync` complete before arming. |
| Can the failure be enabled only after seeding? | **YES** | The test explicitly calls `ArmNextSaveFailure()` after `WithDbContextAsync` returns. |
| Will logout query the same seeded backing store? | **YES** | One `TripMateApiFactory` supplies both scopes; its existing `_databaseName` is used by seed and request contexts. |
| Will the logout request definitely reach `SaveChangesAsync`? | **YES** | The cookie hashes to the seeded active row; the handler assigns `RevokedAtUtc`; `FaultCount == 1` proves interception. |
| Will `SaveChangesAsync` throw during ACT, not ARRANGE? | **YES** | The one-shot switch is armed only between successful seed completion and HTTP dispatch. |
| Will `finally` execute `WebRefreshCookie.Delete`? | **YES** | The planned controller `try/finally` executes cleanup as the interceptor exception unwinds. |
| Will middleware preserve `Set-Cookie` and return 500? | **YES** | Current outermost `ExceptionHandlingMiddleware` does not call `Response.Clear()`; TC-14 asserts both header and 500 ProblemDetails. |
| Will failure-path logs be checked for secrets? | **YES** | TC-14 itself reads the configured Testing file sink and checks all required marked secret classes after the injected failure. |

---

## 9. Validation Commands

```bash
# 1. Full build — must be 0 errors, 0 warnings
dotnet build TripMate.slnx

# 2. Full test suite — 0 failures; no new skips vs baseline
dotnet test TripMate.slnx

# 3. Formatting gate
dotnet format TripMate.slnx --verify-no-changes

# 4. Focused: unit tests only
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj

# 5. Focused: integration tests only
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj

# 6. Focused: sign-out tests only
dotnet test --filter "FullyQualifiedName~SignOut"

# 7. Focused: log sanitization tests
dotnet test --filter "FullyQualifiedName~LogSanitization"

# 8. Whitespace diff check
git diff --check -- src/ tests/ specs/ plans/
```

---

## 10. Invariants / Regression Guards

### 10.1 Sign-Out Must Never Mutate

| Entity | Properties That Must Not Change |
|---|---|
| `dbo.Users` | `Role`, `Status`, `LastLoginAtUtc`, `UpdatedAtUtc`, `PasswordHash`, `EmailVerifiedAtUtc` |
| `dbo.OperatorProfiles` | `ApprovalStatus`, `ReviewedBy`, `ReviewedAtUtc` |
| `dbo.RefreshTokens` (other sessions) | `RevokedAtUtc` for rows with different `TokenHash` |
| `dbo.RefreshTokens` (already-revoked) | Original `RevokedAtUtc` timestamp must not be overwritten |

### 10.2 Idempotency Invariant
Calling sign-out multiple times with the same token must not change state after the first successful revocation. The `RevokedAtUtc` timestamp is written once and frozen thereafter.

### 10.3 Zero New Test Failures
The verified implementation-start baseline is **418 total / 402 passed / 0 failed / 16 skipped**. All 402 previously passing tests must continue to pass, the 16 existing SQL Server skips must not increase without an explicitly documented environment reason, and new UC-05 tests (TC-01..TC-14) must all pass. The historical 391 / 383 / 0 / 8 baseline is retained only as pre-UC-18 context and is not the regression gate.

### 10.4 No Forbidden Infrastructure
After implementation, `grep -r "Redis\|IMemoryCache\|OnTokenValidated\|blacklist" src/` must find zero new matches.

---

## 11. Out of Scope

1. **Server-side access-token blacklist** — No Redis, `IMemoryCache`, `jti` tracking, `OnTokenValidated`, or per-request cache lookup.
2. **Redis / cache infrastructure** — No new infrastructure package references.
3. **Token versioning** — No `token_version` or session epoch fields.
4. **JWT claim redesign** — No new claims in `JwtTokenService.GenerateAccessToken()`.
5. **Access-token lifetime change** — Remains ~15 minutes.
6. **Sign Out All Devices** — No `WHERE user_id = @UserId` bulk revocation.
7. **Refresh-token rotation** — Non-rotating by contract.
8. **`token_hash` index** — Non-blocking follow-up; excluded from this UC.
9. **EF Core migrations** — Zero migrations.
10. **Schema changes** — `database/tripmate_schema_v7.sql` not modified.
11. **`ExecuteUpdateAsync` optimization** — Tracked entity mutation via `SaveChangesAsync`.
12. **Mobile application** — Flutter UI and secure storage are `Capstone_Mobile` responsibility.
13. **Web frontend** — Next.js session clearing is `Capstone_FE` responsibility.
14. **SRS document update** — SRS Report 3 §3.2.5 wording reconciliation is deferred.

---

## 12. Implementation Order

Every implementation slice follows: **RED → GREEN → REFACTOR → focused validation → task review → next task**. A Critical spec or architecture finding blocks advancement until corrected. T01 was implemented, validated, and reviewed independently before this amendment.

| Step | Task(s) | Dependency | Notes |
|---|---|---|---|
| 1 | **T01** — `WebRefreshCookie.Delete()` | None | **COMPLETE:** implemented, focused validation passed, task review passed |
| 2 | **T02** — `SignOutRequestDto.cs` | T01 reviewed | Pure DTO; RED may be N/A; compile/focused validation and task review still required |
| 3 | **T03** — `SignOutResponseDto.cs` | T02 reviewed | Pure DTO; RED may be N/A; compile/focused validation and task review still required |
| 4 | **T06 RED phase** — relevant `SignOutCommandHandlerTests.cs` cases | T02, T03 reviewed | Write handler tests before T04; run and confirm a meaningful compile/test failure because `SignOutCommand` and handler are not implemented |
| 5 | **T04 GREEN phase** — `SignOutCommand.cs` + handler | T06 RED confirmed | Implement minimally, run T06 tests to GREEN, refactor, focused validation, then spec/architecture task review |
| 6 | **T07 RED phase** — minimum endpoint-relevant `SignOutIntegrationTests.cs` cases | T01–T04 and T06 reviewed | Add only the minimum tests proving the missing Mobile/Web logout endpoints or behavior fail before T05 |
| 7 | **T05 GREEN phase** — `AuthController` additions | T07 endpoint RED confirmed | Implement endpoints minimally, make endpoint tests GREEN, refactor, focused validation, then spec/architecture task review |
| 8 | **Complete T07** — remaining integration scenarios and optional interceptor seam in `TripMateApiFactory.cs` | T05 reviewed | Complete TC-01..TC-07 and TC-12..TC-14, including TC-14 one-shot `SaveChangesInterceptor`; run focused validation and task review |
| 9 | **T08** — `LogSanitizationTests.cs` normal sign-out extension | T05 and T07 reviewed | Complete TC-09 using RED → GREEN/reuse-existing-behavior → focused validation → task review; TC-14 failure-path logging remains in T07 |
| 10 | Full validation | All T01–T08 reviewed | Run build, full tests against the verified 418-test baseline, format verification, and diff check |
| 11 | Final review and developer handoff | Full validation passed | Review final diff against spec, architecture, invariants, and TC-01..TC-14; report to developer and **STOP** for delivery choice |

---

## 13. Rollback / Failure Recovery

Since UC-05 introduces no schema changes and no EF migrations:

- **Rollback = delete new files + revert modified files:**
  - Delete: `SignOutRequestDto.cs`, `SignOutResponseDto.cs`, `SignOutCommand.cs`, `SignOutCommandHandlerTests.cs`, `SignOutIntegrationTests.cs`
  - Revert: `AuthController.cs`, `WebRefreshCookie.cs`, `TripMateApiFactory.cs`, `LogSanitizationTests.cs`
- No database rollback required.
- No migration down-script required.
- `git diff --stat` before commit must show exactly: **5 new files + 4 modified files** — measured against the UC-04 sign-in base this branch stacks on (`feature/PhucTV-sign-in`, see §1). A diff against `develop` will additionally list the unmerged UC-04 commits and is not the UC-05 footprint.

---

## 14. Definition of Done

UC-05 Backend implementation is complete when ALL of the following are satisfied:

1. `POST /api/v1/auth/logout` accepts `{ "refreshToken": "..." }` (nullable) and returns HTTP 200 `{ "message": "Signed out successfully." }`.
2. On normal or idempotent completion (active token, already revoked token, unknown token, expired token, or missing cookie), `POST /api/v1/auth/web/logout` returns HTTP 200 `{ "message": "Signed out successfully." }` and emits `tripmate_refresh` cookie deletion header (`Path=/api/v1/auth`, `SameSite=Lax`, `HttpOnly`, `Max-Age=0`, `Expires=Thu, 01 Jan 1970 00:00:00 GMT`; `Secure` follows the existing WebRefreshCookie conditional policy). On unexpected handler/database/infrastructure failure, cookie cleanup is still attempted via `try/finally`, the exception is NOT swallowed, response remains HTTP 500 ProblemDetails, and is never converted to false HTTP 200.
3. Matching `dbo.RefreshTokens` row has `revoked_at` stamped on active session sign-out.
4. Idempotency: repeated calls, already-revoked, unknown, and missing tokens all return HTTP 200 without error and without overwriting existing `RevokedAtUtc`.
5. Multi-device isolation: only the matched session row is revoked; other sessions remain active.
6. Domain invariants: `dbo.Users` and `dbo.OperatorProfiles` are not modified.
7. Revoked refresh token rejected by `/web/refresh` (HTTP 401 `AUTH_TOKEN_INVALID`).
8. TC-14: An active matching refresh session is seeded successfully with the one-shot interceptor disabled; the fault is armed only after Arrange; logout queries that same store, reaches `SaveChangesAsync`, and the interceptor throws exactly once during Act. The response is HTTP 500 ProblemDetails (`Status=500`, `application/problem+json`), the `Set-Cookie` deletion header survives, and the exception is not swallowed or converted to HTTP 200.
9. No raw refresh token, cookie value/header, access JWT/Authorization header, password, or password hash appears in log output. TC-09 proves the normal path; TC-14 independently proves the same sanitization classes on the injected-failure path using a non-vacuous captured failure log.
10. Zero blacklist infrastructure introduced.
11. `dotnet build TripMate.slnx` — 0 errors, 0 warnings.
12. `dotnet test TripMate.slnx` — the verified implementation-start baseline (**418 total / 402 passed / 0 failed / 16 skipped**) has zero regressions, and all added UC-05 tests (TC-01..TC-14) pass.
13. `dotnet format TripMate.slnx --verify-no-changes` — exit 0.
14. `git diff --check` — 0 whitespace errors in changed files.

---

## 15. Plan Status

### **PLAN STATUS: APPROVED FOR IMPLEMENTATION**

> UC-05 implementation is approved to proceed according to this plan. Implementation must remain strictly within the approved scope, tasks T01–T08, invariants, and TC-01 through TC-14.
> Implementation follows the order in §12.
> After implementation tasks complete: run full validation, perform final diff/spec/architecture review, report to the developer, and **STOP**. The developer then chooses delivery. Do not run `git add`, commit, push, or open a Pull Request unless the developer explicitly requests that action.

---

*Created: 2026-09-16 | Process alignment amended: 2026-09-17 | Plan Rev. 1.4 | Branch: `feature/PhucTV-sign-out` (stacked on the unmerged UC-04 `feature/PhucTV-sign-in`; merge order must declare that dependency — see §1) | Spec: `specs/UC-05-spec.md` Rev. 2.1 | Verified implementation-start baseline: 418 total / 402 passed / 0 failed / 16 skipped*
