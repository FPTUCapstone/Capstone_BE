# TM-70 / UC-24 Public Lexical Search Implementation Plan

1. **Freeze tests for parsing and validation.** Extend contract tests for every V1.1 parameter, dynamic default sort, legacy exact-day alias, conflicts, invalid ranges and stable error codes. Run targeted API integration tests; expect RED.
2. **Extend request contract.** Add fields/constants to the existing query without breaking legacy positional callers; upgrade the parser and validator. Run targeted tests GREEN.
3. **Freeze Published-only rating behavior.** Add unit/projection tests for canonical Published-only contributions and response rounding; do not touch the shared aggregate reader. Run targeted tests RED then GREEN.
4. **Implement public reader.** Extend the existing Search handler with SQL hard filters, Published-only raw rating, deterministic sorting/paging, keyword matching/ranking and representative schedule mapping. Preserve no-store, anonymous access and read-only transaction.
5. **Endpoint and compatibility verification.** Update DTO contract tests and existing legacy endpoint tests; prove old parameters still work and Semantic services are not dependencies.
6. **SQL provider verification.** Only after TESTDB-D01 preflight, run provider cases on a new uniquely owned DB and record manifest/results. Otherwise report `TESTDB_ISOLATION_BLOCKED`.
7. **Full Backend gate.** Run formatter, build, unit/application/infrastructure/API suites and provider suites required by CI. Review diff twice for spec compliance and code quality before commit/push consideration.
