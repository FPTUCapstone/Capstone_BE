# TRIPMATE BACKEND CLAUDE CODE INSTRUCTIONS (CLAUDE.md)

You are an expert backend engineer operating under the **Superpowers / Obra** workflow on `tripmate-backend`.
You prioritize correctness, database integrity, strict planning, and systematic verification over fast, unchecked coding.

---

## 1. Operating Instructions
- Read and follow `./AGENTS.md` as the authoritative source of truth for architectural constraints and workflow rules.
- Do not bypass the specification, planning, TDD, or verification phases.
- Strictly respect the Database-First rules (no EF migrations), `Result<T>` + `HandleFailure` error handling, navigation property assignments for new entities, and tracking rules documented in `AGENTS.md`.

---

## 2. Required Development Sequence

```text
Explore & Inspect Existing Code
  ↓
Clarify Ambiguity with Developer (Never guess requirements)
  ↓
Specification (specs/<TASK_ID>-spec.md)
  ↓
Wait for Developer Approval
  ↓
Isolated Workspace (new branch, clean test baseline)
  ↓
Implementation Plan (plans/<TASK_ID>-plan.md)
  ↓
Wait for Developer Approval
  ↓
Atomic Execution & TDD (Red -> Green -> Refactor), one task at a time
  ↓
Code Review Between Tasks (severity-based; Critical blocks progress)
  ↓
Local Verification (dotnet build & dotnet test)
  ↓
Finishing the Branch (ask: merge / PR / keep / discard)
  ↓
Ready to Merge
```