# Public bookable discovery plan

Approved scope: public-bookable-tour-discovery-spec.md, Web task 3.

1. Add shared set-based reader for eligible schedule IDs using existing timeline
   policy; regression for full/closed/past/cancelled/overlap/later valid schedule.
2. Add strict optional search bool, filter before count/page and representative
   selection. SQL provider regression verifies totals, page 2 and date matching.
3. Web tour proxy forces flag after query sanitization; no client opt-out.
4. Direct detail reports canonical timeline bookability without new DB queries.
5. Refresh and cached recommendation paths reuse the shared reader. Run focused
   unit and SQL/API tests and review final implementation. No commit or push.

Verification: 106 relevant Application unit tests passed; 68 API/OpenAPI/SQL
integration tests passed with no skips. SQL fixtures create disposable databases,
not changes to local TripMateDb. `dotnet format whitespace --verify-no-changes`
and `git diff --check` passed for changed C# files. Optional flag is documented
as boolean in the V1 search OpenAPI operation. Independent two-pass review found
no blocking findings. Full unrelated BE suites were not rerun.
