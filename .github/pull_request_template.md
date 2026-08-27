## Summary

<!-- What changed and why? Keep this focused on one logical change. -->

## Affected endpoints and features

<!-- List endpoints/features, or write "None" for non-functional changes. -->

- <!-- Example: `POST /api/v1/auth/login` -->

## Related task or issue

<!-- Add a link or identifier, or write "None". -->

## Validation performed

- [ ] `dotnet build`
- [ ] `dotnet test`
- [ ] Migration added and applied locally (or not applicable)
- [ ] Manual endpoint checks via Swagger/HTTP client (or not applicable)

## Database changes

<!-- List new/changed entities and migrations, or write "None". -->

## Known limitations

<!-- Describe limitations, follow-up work, or write "None". -->

## Definition of Done

- [ ] The change matches the assigned scope and contains no unrelated work.
- [ ] Clean Architecture dependency direction is respected.
- [ ] New use cases follow the vertical-slice (command/query + validator + handler) pattern.
- [ ] Expected failures use `Result`/`Result<T>`; exceptions are reserved for unexpected errors.
- [ ] No secrets, credentials, or real connection strings were added.
- [ ] No generated files, local environment files, IDE files, or temporary artifacts were
      committed.
- [ ] `README.md` and `.env.example` were updated if configuration or setup changed.
- [ ] Self-review is complete and remaining technical debt is documented.

## Reviewer notes

<!-- Call out architecture decisions, risky areas, or specific feedback requested. -->
