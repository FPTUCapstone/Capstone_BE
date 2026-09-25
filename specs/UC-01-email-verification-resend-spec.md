# UC-01 Web email-verification resend

`POST /api/v1/auth/web/resend-verification` accepts JSON `{ "email", "password" }`.
The Backend validates the credentials against `dbo.Users`; it never signs the password in with Firebase.
Only `PendingEmailVerification` accounts are eligible. A successful request generates a Firebase Admin
email-verification action link, sends it through the existing SMTP port, and returns `MSG_RESEND_SUCCESS`.
It creates no application session and changes no account state.

Invalid input is 400, invalid credentials are 401, an ineligible account is 409, the per-account
60-second cooldown and per-IP limiter are 429, and unavailable link/email delivery is 503.
The existing registration `/verify-account` Firebase-client resend remains unchanged.
