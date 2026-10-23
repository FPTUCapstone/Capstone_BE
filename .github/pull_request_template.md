## 1. Related PRs
- Link related PRs or _No related PRs_
## 2. Attention!
**These are things you need to get done before release to production. Please read it carefully!**
- _write here_
## 3. Summary(short description about PRs's goal)
- _write here_
## 4. Link specs
- _write here_
## 5. Output(Image, video, JSON example)
- _write here_

## Definition of Done

**Validation performed**

- [ ] `dotnet build`
- [ ] `dotnet test`
- [ ] Migration added and applied locally (or not applicable)
- [ ] Manual endpoint checks via Swagger/HTTP client (or not applicable)

**Checklist**

- [ ] The change matches the assigned scope and contains no unrelated work.
- [ ] Clean Architecture dependency direction is respected.
- [ ] New use cases follow the vertical-slice (command/query + validator + handler) pattern.
- [ ] Expected failures use `Result`/`Result<T>`; exceptions are reserved for unexpected errors.
- [ ] No secrets, credentials, or real connection strings were added.
- [ ] No generated files, local environment files, IDE files, or temporary artifacts were
      committed.
- [ ] `README.md` and `.env.example` were updated if configuration or setup changed.
- [ ] Self-review is complete and remaining technical debt is documented.
