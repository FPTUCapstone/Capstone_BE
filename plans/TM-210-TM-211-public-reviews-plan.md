# TM-210 and TM-211 atomic delivery

1. Verify existing detail/search/review source and baseline unit and SQL contracts.
2. Add a regression proving publication-unknown legacy rows cannot affect public Detail.
3. Replace Detail's legacy aggregate call with the existing canonical public projection; verify regression and existing tests.
4. Add privacy/publication/order list tests before adding the typed public list to Detail.
5. Verify endpoint/OpenAPI additive schema, SQL aggregation, fixed query count, unit suite and contract documentation; run specification and quality review.

Execution and Draft PR publication are authorized by the master request. No SRS mutation or duplicated API is permitted.
