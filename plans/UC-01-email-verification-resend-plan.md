# UC-01 resend implementation plan

1. Add application ports for verification-link generation and an atomic resend cooldown.
2. Add a command handler that validates input, DB credentials, account state, cooldown, link generation and SMTP delivery.
3. Expose the command at the anonymous rate-limited web endpoint.
4. Extend the SMTP adapter with verification-link email composition and add Firebase Admin link generation.
5. Prove success and failure contracts with application/API regression tests before wiring the FE.
