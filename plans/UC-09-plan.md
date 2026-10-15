# UC-09 Update Travel Preferences — Backend Implementation Plan

Status: Updated 2026-10-13

1. Define DTOs, commands and validators for the five profile preferences: interests, transport, pace, food and risk. Do not accept or return profile budget or travel-style fields.
2. Load interest choices from `POICategories` and `Tags`; retain canonical English keys and backend validation.
3. Persist the five values in `dbo.TravelerProfiles` with one atomic save and active-Traveler authorization in controller and handler.
4. Remove `TravelerProfile.DefaultBudget`, API `defaultBudgetVnd`, the option range, EF mapping and canonical-schema column. Add the idempotent `20261013_remove_traveler_profile_default_budget.sql` upgrade migration.
5. Update unit, endpoint and SQL Server integration tests to verify the five-field contract, row update/clear, authorization and migration compatibility. Budget tests remain under UC-10 scheduling, where `budgetVnd` is optional.
6. Verify formatting, build, focused unit tests and SQL Server tests when the integration connection is configured.
