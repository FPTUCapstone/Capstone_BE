using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.TestUtilities;

public class FakePasswordHasher : IPasswordHasher, IPasswordHasherService
{
    public string Hash(string password) => $"hashed:{password}";

    public bool Verify(string password, string passwordHash) => passwordHash == Hash(password);
}

public class FakeJwtTokenService : IJwtTokenService
{
    public int AccessTokenGenerationCount { get; private set; }
    public int RefreshTokenGenerationCount { get; private set; }

    public Exception? ThrowOnGenerateAccessToken { get; set; }

    public (string Token, DateTimeOffset ExpiresAtUtc) GenerateAccessToken(User user)
    {
        AccessTokenGenerationCount++;
        if (ThrowOnGenerateAccessToken is not null)
        {
            throw ThrowOnGenerateAccessToken;
        }

        return ($"access-token-for-{user.Id}", DateTimeOffset.UtcNow.AddMinutes(15));
    }

    public string GenerateRefreshToken()
    {
        RefreshTokenGenerationCount++;
        return $"refresh-token-{Guid.NewGuid()}";
    }

    public string HashRefreshToken(string rawRefreshToken) => $"hashed:{rawRefreshToken}";
}

public class FakeDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
public class FakeCurrentUserService : ICurrentUserService
{
    public long? UserId { get; set; }

    public string? Role { get; set; }
}

/// <summary>Reversible only within the test process; never a stand-in for real key management.</summary>
public class FakePiiProtector : IPiiProtector
{
    // Data Protection ciphertext is materially longer than its plaintext input. Keep this fake
    // long enough to catch a regression to the old NVARCHAR(50) persistence limit.
    public string Protect(string plaintext) => $"protected:{plaintext}:{new string('x', 256)}";

    public string Unprotect(string protectedValue) =>
        protectedValue.StartsWith("protected:", StringComparison.Ordinal)
            ? protectedValue["protected:".Length..].Split(':')[0]
            : throw new FormatException("Value was not protected by FakePiiProtector.");
}

public class FakePaymentCredentialProtector : IPaymentCredentialProtector
{
    public string Protect(string rawCredential) => $"ref:{rawCredential}";

    public string Unprotect(string referenceToken) =>
        referenceToken.StartsWith("ref:", StringComparison.Ordinal)
            ? referenceToken["ref:".Length..]
            : throw new FormatException("Not protected by FakePaymentCredentialProtector.");
}

public class FakePaymentChannelVerifier : IPaymentChannelVerifier
{
    public ChannelVerificationOutcome OutcomeToReturn { get; set; } = ChannelVerificationOutcome.PendingAdminApproval;
    public string? WebhookUrlToReturn { get; set; }
    public string? ErrorCodeToReturn { get; set; }

    public Task<ChannelVerificationResult> VerifyAsync(
        PaymentChannelCredentials credentials,
        string webhookBaseUrl,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ChannelVerificationResult(OutcomeToReturn, WebhookUrlToReturn ?? webhookBaseUrl, ErrorCodeToReturn));
}

public class FakePaymentGateway : IPaymentGateway
{
    public PaymentGateway Gateway => PaymentGateway.Mock;

    public int CreateCallCount { get; private set; }
    public int CancelCallCount { get; private set; }
    public Exception? ThrowOnCreate { get; set; }
    public Exception? ThrowOnCancel { get; set; }
    public PaymentLinkResult? ResultToReturn { get; set; }

    public Task<PaymentLinkResult> CreatePaymentLinkAsync(
        PaymentLinkRequest request, CancellationToken cancellationToken)
    {
        CreateCallCount++;
        if (ThrowOnCreate is not null) throw ThrowOnCreate;
        return Task.FromResult(ResultToReturn ?? new PaymentLinkResult(
            PaymentLinkId: "link-id",
            CheckoutUrl: "https://pay.example.com/checkout",
            ExpiresAtUtc: request.ExpiresAtUtc));
    }

    public Task CancelPaymentLinkAsync(
        PaymentLinkCancellationRequest request, CancellationToken cancellationToken)
    {
        CancelCallCount++;
        if (ThrowOnCancel is not null) throw ThrowOnCancel;
        return Task.CompletedTask;
    }

    public Task<RefundSubmissionResult> SubmitRefundAsync(
        RefundSubmissionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new RefundSubmissionResult(false));
}

public class FakeOperatorPaymentGatewayFactory : IOperatorPaymentGatewayFactory
{
    public OperatorChannelSnapshot? LastSnapshot { get; private set; }
    public FakePaymentGateway GatewayToReturn { get; set; } = new();

    public IPaymentGateway Create(OperatorChannelSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        return GatewayToReturn;
    }
}

public class FakePaymentCompletionProcessor : IPaymentCompletionProcessor
{
    public int CompleteCallCount { get; private set; }
    public long? LastOrderCode { get; private set; }
    public PaymentTransactionStatus? LastOutcome { get; private set; }

    public Result<PaymentCompletionResult> ResultToReturn { get; set; } =
        Result.Success(new PaymentCompletionResult(
            PaymentTransaction.Create(1L, PaymentGateway.PayOS, 100_000m, 20261125001L,
                DateTimeOffset.UtcNow, null, PaymentTransaction.DefaultCurrency),
            Booking.Restore(1L, 1L, 100L, 1, 100_000m, BookingStatus.Confirmed,
                PaymentStatus.Paid, Booking.DefaultCancellationPolicySnapshot),
            true));

    public Task<Result<PaymentCompletionResult>> CompleteAsync(
        long? transactionId,
        long? orderCode,
        PaymentTransactionStatus outcome,
        string? gatewayRef,
        string? rawResponse,
        decimal? verifiedAmount,
        string? verifiedCurrency,
        CancellationToken cancellationToken)
    {
        CompleteCallCount++;
        LastOrderCode = orderCode;
        LastOutcome = outcome;
        return Task.FromResult(ResultToReturn);
    }
}

/// <summary>
/// No-op stand-in for IAccountCredentialLock. The real implementation issues sp_getapplock,
/// which the EF InMemory provider cannot execute; the true cross-process serialization
/// guarantee is proven only by the SQL Server integration test.
/// </summary>
public class FakeAccountCredentialLock : IAccountCredentialLock
{
    public int AcquireCallCount { get; private set; }

    public Exception? ThrowOnAcquire { get; set; }

    public Task AcquireAsync(long userId, CancellationToken cancellationToken)
    {
        AcquireCallCount++;
        if (ThrowOnAcquire is not null)
        {
            throw ThrowOnAcquire;
        }

        return Task.CompletedTask;
    }
}