using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Contracts.Common;

public static class OperatorContractSigningPolicy
{
    public const string ConsentVersion = "operator-contract-v1";
    public static TimeSpan OtpTimeToLive => PasswordResetPolicy.OtpTimeToLive;
    public static TimeSpan ResendCooldown => PasswordResetPolicy.ResendCooldown;
    public const int MaximumFailedAttempts = PasswordResetPolicy.MaxFailedAttempts;
    public const int MaximumChallengesPerFifteenMinutes = 10;
}

public interface IOperatorContractOtpProtector
{
    string Protect(string otp, long userId, long contractId, string contentSha256, Guid challengeId, DateTimeOffset createdAtUtc);
    bool Verify(string otp, long userId, long contractId, string contentSha256, Guid challengeId, DateTimeOffset createdAtUtc, string protectedOtp);
}

public interface IOperatorContractOtpEmailSender
{
    Task<EmailDeliveryResult> SendAsync(string destinationEmail, string otp, long contractId, CancellationToken cancellationToken);
}