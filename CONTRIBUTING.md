# Contributing to TripMate Backend

TripMate Backend is the ASP.NET Core Web API consumed by the Next.js Admin Web
(`Capstone_FE`) and the Flutter Traveler/Tour Operator app (`Capstone_Mobile`). Product,
requirement, and API contract documentation lives in the separate `Capstone_Docs` repository.

Detailed architecture rules are in [`README.md`](README.md). All contributors must follow them.

## Local setup

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and either Docker Desktop or
a local SQL Server instance.

```bash
git clone https://github.com/FPTUCapstone/Capstone_BE.git
cd Capstone_BE
dotnet restore
dotnet ef database update --project src/TripMate.Infrastructure --startup-project src/TripMate.Api
dotnet run --project src/TripMate.Api
```

See [`README.md`](README.md) for the Docker-based alternative.

## Development workflow

1. Fetch the latest remote changes and update your local `main` branch.
2. Create a focused branch from `main` using `<type>/<short-description>`.
3. Implement only the assigned scope and update documentation when behavior or setup changes.
4. Run the required validation commands.
5. Commit using Conventional Commits.
6. Push the branch and open a Pull Request into `main`.
7. Address review feedback before merge.

Do not develop or push directly on `main`.

### Branch names

Allowed types are `feature`, `fix`, `refactor`, `chore`, `docs`, and `test`. Use lowercase
kebab-case after the slash.

Examples:

- `feature/traveler-registration`
- `fix/login-account-status-check`
- `refactor/auth-error-mapping`
- `docs/readme-docker-setup`

## Commit messages

Use Conventional Commit style:

```text
<type>(<optional-scope>): <imperative summary>
```

Examples:

- `feat(auth): add tour operator login support`
- `fix(auth): block sign-in for restricted accounts`
- `refactor(persistence): extract user entity configuration`
- `docs(readme): document docker compose setup`

Avoid vague messages such as `update`, `fix code`, `done`, or `final`.

## Required validation

Run before opening or updating a Pull Request:

```bash
dotnet build
dotnet test
```

## Pull Requests

- Target `main` unless maintainers explicitly direct otherwise.
- Keep one logical change per PR.
- Complete the repository PR template.
- Identify affected endpoints and features.
- Report validation results and known limitations honestly.
- Never include secrets, generated artifacts, or unrelated changes.

A PR must be reviewable and must not be merged until required checks pass and review feedback
is resolved.
