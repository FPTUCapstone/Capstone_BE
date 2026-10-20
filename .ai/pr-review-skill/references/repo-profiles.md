# Repo profiles

Base branch for all repos: `develop`. Branch names `<type>/<lowercase-kebab-case>`, Conventional Commits. No direct pushes to `main`/`develop`.

## Capstone_FE (Next.js, npm)
- Routes in `app/`; implementation in `src/features/`; shared UI in `src/components/`.
- Server Components by default; `'use client'` only where needed, narrow boundary.
- No new global state / UI framework / architecture layer without approval.
- `src/legacy/mobile/` is reference only. Platform scope per `docs/WEB_SCOPE_MATRIX.md`; no Web versions of `MOBILE_ONLY`/`NON_SCREEN` use cases.
- Checks: `npm run lint`, `npm run typecheck`, `npm run build`. Preserve `package-lock.json`.
- `NEXT_PUBLIC_*` is public; `.env.example` updated when config changes.
- Mock/prototype behaviour must be distinguishable from real integration.

## Capstone_BE (.NET)
- Checks: `dotnet build`, `dotnet test`.
- Verify validation, authn/authz on endpoints, DB schema/migration consistency (`database/`), error codes style (e.g. `auth.invalid_credentials`), no secrets in config.

## Capstone_Mobile (Flutter)
- Checks: `dart format --set-exit-if-changed .`, `flutter analyze`, `flutter test`.
- Check state handling, null-safety, platform permissions, no hard-coded URLs/keys.
