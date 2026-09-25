namespace TripMate.Infrastructure.Email;

internal static class EmailVerificationEmailComposer
{
    public static PasswordResetEmailContent Compose(string verificationLink) => new(
        "Verify your TripMate email",
        $"""
        Welcome to TripMate.

        Verify your email address by opening this link:
        {verificationLink}

        If you did not create this account, you can safely ignore this email.
        """);
}
