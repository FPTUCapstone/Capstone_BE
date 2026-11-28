# Local payment mock switch

1. Verify local provider setting; regression-test the operator factory selection and persisted attempt gateway.
2. Select existing MockPaymentGateway when configured; retain payOS implementation for PayOS configuration. Reject real historical attempts at mock completion, with a failing regression first.
3. Run focused payment suites and format exact files; restart native local BE and check health. Preserve DB transactions, merchant credentials and remote configuration.
