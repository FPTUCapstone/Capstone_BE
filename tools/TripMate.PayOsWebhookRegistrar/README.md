# PayOS webhook registration

Use this one-time local tool after starting a public HTTPS tunnel to the API
webhook receiver. It reads `Payment:PayOs:ClientId` and
`Payment:PayOs:ApiKey` from the existing TripMate API User Secrets or
environment variables; it never prints those values.

```powershell
dotnet run --project tools/TripMate.PayOsWebhookRegistrar -- `
  https://your-public-host/api/v1/payments/payos/webhook
```

Run the command again whenever the temporary tunnel URL changes. A successful
command means PayOS accepted the URL and sent its verification webhook. Keep
the API and tunnel running until it completes.
