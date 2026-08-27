# Agent Instructions

These instructions apply to every AI coding agent working anywhere in this repository. Read
[CONTRIBUTING.md](CONTRIBUTING.md) and [README.md](README.md) before changing the project.

## Before Editing

- Inspect the current branch, working tree, `TripMate.slnx`, relevant source, tests, and nearby
  documentation.
- Preserve existing user changes and do not modify unrelated files.
- Confirm the assigned task's scope. Do not implement functionality merely because it appears in
  `Capstone_Docs` requirements, use-case, or API contract documents.
- If an API contract, requirement, role rule, or destructive action is materially ambiguous, stop
  and report the dependency instead of inventing it.

## Architecture

- Follow Clean Architecture and keep dependencies pointing inward: `TripMate.Api` depends on
  `TripMate.Application` and `TripMate.Infrastructure`; `TripMate.Infrastructure` depends on
  `TripMate.Application`; `TripMate.Application` depends only on `TripMate.Domain`.
  `TripMate.Domain` depends on nothing.
- `TripMate.Application` must not reference ASP.NET Core, EF Core provider packages (SqlServer,
  etc.), or any other concrete infrastructure — only interfaces in
  `Application/Common/Interfaces/` and the EF Core abstractions needed for `IApplicationDbContext`.
- Put each use case under `Application/Features/<Feature>/<UseCase>/` as a self-contained
  command/query, validator, and handler (vertical slice) — follow the existing `Authentication`
  feature as the template. Do not introduce a generic repository/service layer on top of MediatR
  handlers.
- Use `Result`/`Result<T>` (`Application/Common/Models/Result.cs`) for expected business failures
  (invalid credentials, account status, duplicate email, etc.). Throw exceptions only for
  genuinely unexpected conditions; `FluentValidation` failures surface as
  `Application.Common.Exceptions.ValidationException` via the MediatR pipeline behaviour, not
  inline `if` checks in handlers.
- New persisted entities go in `TripMate.Domain/Entities/`, with their EF configuration in
  `TripMate.Infrastructure/Persistence/Configurations/` as an `IEntityTypeConfiguration<T>` — do
  not configure entities with data annotations or inline `OnModelCreating` blocks.
- Controllers in `TripMate.Api/Controllers/V1/` stay thin: send the command/query via `ISender`
  and translate the `Result` to an `IActionResult` (see `ApiControllerBase.HandleFailure`). Do not
  put business logic in controllers or middleware.
- Never expose raw exception details, stack traces, or internal error messages to API clients —
  unexpected exceptions must go through `ExceptionHandlingMiddleware` and return a generic
  `ProblemDetails` response.
- Do not create empty layers, folders, entities, or handlers solely to make the tree look more
  complete.

## Database Changes

- Any change to an entity shape requires a matching EF Core migration:

  ```bash
  dotnet ef migrations add <Name> --project src/TripMate.Infrastructure --startup-project src/TripMate.Api
  ```

- Do not hand-edit generated migration files. If a migration is wrong, remove it
  (`dotnet ef migrations remove ...`) and regenerate.
- Never commit a connection string, JWT signing key, or other secret that isn't already a
  clearly-labeled local-development placeholder in `appsettings.Development.json` or
  `.env.example`.

## Git and Delivery

- Never develop directly on `main`. Use a lowercase kebab-case branch following
  `<type>/<short-description>` with `feature`, `fix`, `refactor`, `chore`, `docs`, or `test`.
- Do not commit, push, open a Pull Request, or modify remote state unless the user explicitly
  requests it.
- Never force push, rewrite shared history, discard unrelated work, or commit secrets, build
  output, local environment files, or IDE caches.
- Use Conventional Commits when a commit is explicitly requested.
- Before reporting implementation completion, run:

  ```bash
  dotnet build
  dotnet test
  ```

  All applicable checks must pass. If an environmental limitation prevents a check (e.g. no SQL
  Server instance available), state the exact limitation; do not imply it passed.

## Commit Attribution

- AI-authored commits must include `Co-Authored-By: Claude <noreply@anthropic.com>`.
