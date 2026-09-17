# TRIPMATE BACKEND AGENT INSTRUCTIONS (AGENTS.md)

This file defines the engineering workflow, technical rules, and architectural boundaries for all AI coding agents working on `tripmate-backend`.
It applies to Claude Code, Codex, Cursor, Roo Code, Windsurf, Antigravity, and other agentic IDEs.
The agent MUST follow these instructions unless explicitly overridden by the developer.

---

# 1. Core Operating Philosophy (The Superpowers Protocol)

The agent must follow a **Spec-First, Plan-Driven, TDD, Atomic Execution, Verification, and Review** workflow.
Never jump directly from a user request to writing implementation code.

### 1.1 Strict Scope Confirmation & No Requirement Inference (UC-01 Core Invariant)
- **Confirm Scope First**: Before proposing changes, define the exact boundary of the task. Do not implement adjacent features or assume scope expansions.
- **Never Infer Business Rules**: Do not guess or extrapolate business logic from use-case / requirement documents. If an edge case, validation rule, nullable field, or status transition is ambiguous or undocumented:
  **STOP → ASK THE DEVELOPER → CONFIRM DECISION → DOCUMENT → IMPLEMENT**
- **Inspect Existing Code**: Always read existing handlers, entities, and tests first to follow established domain conventions before introducing new code.

### 1.2 Development Stages
1. **Colocated Spec (`specs/<TASK_ID>-spec.md`)**: Define scope, acceptance criteria, DTOs, endpoint routes, domain rules, and error conditions. Wait for explicit developer approval.
2. **Isolated Workspace**: Once the spec is approved, create or switch to a dedicated feature branch (see §5.1) before touching any code. Confirm `dotnet test` is green on a clean checkout first — never start implementation on top of an already-broken baseline.
3. **Atomic Implementation Plan (`plans/<TASK_ID>-plan.md`)**: Break the approved spec into small, sequential, independently testable tasks (each a focused unit of work, not multi-hour) listing exact files, the verification command, and the Definition of Done. Wait for explicit developer approval.
4. **Atomic Execution & TDD (Red -> Green -> Refactor)**: Implement one task at a time. Write a failing test first, write minimal code to pass, refactor, and verify locally before advancing. When the harness supports subagents, dispatch each task to a fresh subagent for a two-stage review (spec compliance, then code quality) before integrating it; otherwise perform the equivalent two-pass self-review. If implementation code was written before its test, delete it and restart with the test first — do not retrofit a test onto already-written code.
5. **Code Review Between Tasks**: After each task passes verification, review it against the plan and spec before moving on. Classify findings by severity: a **Critical** finding (breaks a requirement, introduces a regression, violates an architecture rule in this file) blocks moving to the next task until fixed; a **Minor** finding (style, small cleanup) may be noted and deferred with the developer's agreement.
6. **Finishing the Branch**: Once every plan task is complete and verified, do not merge, push, or open a Pull Request unprompted (see §5.1). Present the developer with the options — merge, open a PR, keep the branch as-is, or discard — and act only on their choice.

---

# 2. Backend Architecture & Domain Invariants

The backend strictly implements **Clean Architecture** with inward dependency flow:
`Domain` ← `Application` ← `Infrastructure` ← `WebAPI / Presentation`

### 2.1 Domain Layer
- **Pure Domain**: Contains Entities, Value Objects, Domain Exceptions, and Domain Rules.
- **Zero Framework Dependency**: Absolutely no references to EF Core, ASP.NET Core, HTTP libraries, or external infrastructure.
- Entities must guard their invariants via factory methods or encapsulated domain methods, not public parameterless setters.

### 2.2 Application Layer
- Contains CQRS Commands, Queries, Handlers, DTOs, Validators (FluentValidation), and Port/Interface abstractions.
- **No Generic Repository**: Do not create a generic `IRepository<T>`! Query directly via DbContext abstractions or feature-specific persistence ports.
- **Result Pattern**: Business operations must return `Result` or `Result<T>`. Never throw business exceptions to control flow; reserve exceptions solely for unexpected system faults.

### 2.3 Presentation / WebAPI Layer
- Controllers and Minimal API endpoints solely handle model binding, authentication checks, executing CQRS requests, and status mapping.
- **No Business Logic in Controllers**: Controllers must never access DbContext directly or contain domain calculations.
- **Response Format (Result + HandleFailure)**:
  - Do **NOT** wrap responses in a synthetic envelope (e.g. `{ success, statusCode, data, errors }`).
  - Use the project's standard `Result<T>` pattern. On failure, invoke `HandleFailure(result)` to emit standard RFC-7807 `ProblemDetails` or project-standard HTTP error payloads.

---

# 3. Database & EF Core Rules (Battle-Tested)

### 3.1 Database-First Policy
- **NO EF Migrations**: Do not run `dotnet ef migrations add`. The schema is managed via SQL scripts / Database-First workflows.
- All schema alterations must be provided as explicit, idempotent SQL migration scripts under the designated database management folder.

### 3.2 EF Core Mapping & Relationship Rules
- **Navigation Properties for Same-Transaction Inserts (CRITICAL)**:
  - When creating a child entity referencing a parent entity that is also being created in the same `SaveChangesAsync()` (e.g., creating a `RefreshToken` referencing a new `User`), **you MUST assign the navigation property (`RefreshToken.User = user`)**, NOT the scalar foreign key (`RefreshToken.UserId = user.Id`).
  - At creation time before persistence, `user.Id` is still the default value `0`. Setting scalar `UserId` will persist `0` and cause key collision or foreign key failures. Setting the navigation property allows EF Core to track the graph and populate the generated Identity key automatically.
- **`AsUtcDateTime2()` Mapping**: All UTC date and timestamp properties mapped to SQL Server `datetime2` must explicitly configure `.AsUtcDateTime2()` (or the project's custom DateTime value converter/extension) to prevent timezone drift and precision truncation.
- **Change Tracking Discipline**:
  - Use `.AsNoTracking()` ONLY for pure read-only queries where the entities are returned directly to DTOs and never modified.
  - For Command handlers that read an entity, modify its state (e.g. updating `LastLoginAtUtc`), or attach related records, **MUST KEEP CHANGE TRACKING ENABLED** (do NOT use `.AsNoTracking()`).

---

# 4. Engineering Principles: YAGNI, DRY & Zero Regression

- **YAGNI**: Build only what is needed for the current Jira ticket. Do not invent generic interfaces or premature caching layers for hypothetical use cases.
- **DRY**: Share logic only when domain concepts are genuinely identical. Do not prematurely abstract distinct business use cases.
- **Zero Regression**: Changes must pass all existing unit and integration tests. Run `dotnet test` before marking any step complete.

---

# 5. Git, Secrets & Commit Rules

### 5.1 Branching & Delivery
- **Never develop directly on `main`**. Use a lowercase kebab-case branch following
  `<type>/<short-description>` with `feature`, `fix`, `refactor`, `chore`, `docs`, or `test`.
- Do not commit, push, open a Pull Request, or modify remote state unless the developer
  explicitly requests it.
- **Never force push**, rewrite shared history, or discard unrelated work.

### 5.2 Conventional Commits
Commit messages must follow: `<type>(<scope>): <description>`
- Types: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`
- Scopes: `auth`, `booking`, `trip`, `payment`, `user`, `db`, `api`
- Example: `feat(auth): validate traveler registration flow`

### 5.3 Commit Attribution
- When committing changes generated or assisted by AI, include appropriate co-author attribution trailers if required by the repository configuration (e.g., `Co-authored-by: Agent <agent@local>`).

### 5.4 Security Boundaries
- **NEVER** commit connection strings, JWT secrets, passwords, or `.env` files.
- All sensitive credentials must be fetched from environment variables or `UserSecrets` during local development.