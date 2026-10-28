# UC-41 implementation plan

1. Add failing application tests for authorization, lifecycle state, policy tiers,
   idempotent replay, and duplicate-refund protection. Verify the focused unit suite.
2. Add Database-First domain entities/mappings and an idempotent SQL migration for
   cancellation data, policy snapshots, operations, and database constraints. Add
   SQL Server migration and transaction regression tests.
3. Implement the command, validator, handler, result/error contracts, and controller.
   Verify focused unit and endpoint tests.
4. Run formatting, build, relevant integration tests, then the full solution tests if
   local SQL Server capacity permits. Review the diff against the specification.
