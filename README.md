# TripMate Backend

ASP.NET Core Web API for TripMate, built with Clean Architecture. Serves the Next.js Admin Web
(`Capstone_FE`) and the Flutter Traveler/Tour Operator app (`Capstone_Mobile`). Product and
requirements documentation lives in the separate `Capstone_Docs` repository.

## Architecture

```
src/
  TripMate.Domain          Entities, enums — no dependencies on anything else.
  TripMate.Application      Use cases (MediatR commands/queries), validation, DTOs, interfaces.
                            Depends only on Domain.
  TripMate.Infrastructure   EF Core, JWT, password hashing — implements Application's interfaces.
  TripMate.Api              Controllers, middleware, composition root (Program.cs).
tests/
  TripMate.Application.UnitTests
```

Dependencies point inward: `Api` → `Application` + `Infrastructure`; `Infrastructure` →
`Application`; `Application` → `Domain`. `Domain` depends on nothing. `Application` never
references EF Core's SqlServer provider, ASP.NET Core, or any concrete infrastructure — only
`Microsoft.EntityFrameworkCore` for the `DbSet<T>` shape of `IApplicationDbContext`.

Each feature lives under `Application/Features/<Feature>/<UseCase>/` as a self-contained
command/query + handler + validator (a vertical slice), following the working `Authentication`
example (`Register`, `Login`).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned via `global.json`)
- SQL Server (via Docker, or a local/LocalDB instance)
- Docker Desktop (optional, for the containerized local setup)

## Local setup — without Docker

1. Start a local SQL Server instance and update `src/TripMate.Api/appsettings.Development.json`
   with its connection string if it differs from the default.
2. Apply migrations:

   ```bash
   dotnet ef database update --project src/TripMate.Infrastructure --startup-project src/TripMate.Api
   ```

3. Run the API:

   ```bash
   dotnet run --project src/TripMate.Api
   ```

4. Swagger UI is available at `https://localhost:<port>/swagger` in Development.

## Local setup — with Docker

```bash
cp .env.example .env   # adjust values if needed
docker compose up --build
```

This starts SQL Server and the API together. The API listens on `http://localhost:5000`.
Apply migrations against the containerized database the same way as above (point
`ConnectionStrings:Default` at `localhost,1433` when running the CLI from the host).

## Running tests

```bash
dotnet test
```

## Adding a new feature (vertical slice)

1. Create `src/TripMate.Application/Features/<Feature>/<UseCase>/` with a `Command`/`Query`
   record, a `Validator`, and a `Handler`. Use `Result`/`Result<T>` for expected failures; only
   throw for truly exceptional/unexpected conditions.
2. Add entities to `TripMate.Domain` and an `IEntityTypeConfiguration<T>` under
   `TripMate.Infrastructure/Persistence/Configurations/` if new persisted state is needed, then
   add a migration (`dotnet ef migrations add <Name> --project src/TripMate.Infrastructure
   --startup-project src/TripMate.Api`).
3. Add a controller action under `src/TripMate.Api/Controllers/V1/` that sends the
   command/query via `ISender` and maps `Result` failures with `HandleFailure`.
4. Add validator and handler tests under `tests/TripMate.Application.UnitTests/`.

Do not add a new project, layer, or NuGet package to solve something the existing structure
already handles — keep the vertical slice pattern consistent across features.

## Configuration

| Key | Purpose |
| --- | --- |
| `ConnectionStrings:Default` | SQL Server connection string. |
| `Jwt:Issuer` / `Jwt:Audience` | JWT claims validation. |
| `Jwt:SigningKey` | Base64 symmetric key used to sign access tokens. **Never reuse the checked-in Development value outside local dev.** |
| `Jwt:AccessTokenLifetimeMinutes` | Access token lifetime. |
| `Cors:AllowedOrigins` | Origins allowed to call the API from a browser (the Next.js admin app). |

`appsettings.Development.json` ships with local-only placeholder values so the project runs
immediately after cloning. Any shared/deployed environment must supply its own secrets via
environment variables or a secret manager — never commit real secrets.
