# Operator dashboard metrics implementation plan

Implements the user-approved four-metric scope and calculation proposal.

1. Add focused handler tests: access, owner isolation, unique tour counting,
   deadline/capacity boundaries, Vietnam month boundaries, successful payments,
   voucher validity/quota, and parity with the existing revenue report.
2. Add a read-only CQRS query/response/handler and thin authorized controller.
   Reuse OperatorRevenueAggregator without changing its formula.
3. Run focused dashboard and existing revenue tests; verify changed C# formatting
   and API compilation. Report unavailable integration checks explicitly.

Definition of done: tests pass, endpoint returns the documented DTO, no unrelated
behavior changes, and Mobile can consume the four totals through one request.
