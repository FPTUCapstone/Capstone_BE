# TM-70 Phase 2 execution plan

User authorized autonomous spec/plan execution, commits and draft delivery.

1. RED parser/validator tests in `tests/TripMate.Api.IntegrationTests/Tours/`;
   implement isolated request/parser/validator under Search/V2 and Requests.
   Verify focused `dotnet test --filter FullyQualifiedName~TourSearchV2`.
2. RED taxonomy HTTP and SQL tests; expose category DbSet through application
   abstraction and add GetCategories query/handler/controller. Verify focused tests.
3. RED provider-backed tests for independent projection; implement V2 read pipeline
   with no registered request handler and no production route. Verify real SQL
   boundaries, sorting, visibility, snapshot, DTO serialization and availability.
4. Two-stage parent review, scoped format, Release build, full SQL-enabled suite
   coordinated with parent, diff/secret checks. Record coverage and blocked ACs.

No PR72 migration copied, no frozen Docs edits, no production search activation.
