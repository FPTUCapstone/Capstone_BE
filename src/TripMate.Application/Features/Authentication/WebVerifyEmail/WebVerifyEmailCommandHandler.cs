using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.WebVerifyEmail;

/// <summary>Synchronizes verified Firebase evidence only. Session establishment belongs to sign-in.</summary>
public sealed class WebVerifyEmailCommandHandler(
    IApplicationDbContext dbContext, IFirebaseAuthService firebaseAuthService, IDateTimeProvider dateTimeProvider)
    : IRequestHandler<WebVerifyEmailCommand, Result<WebVerifyEmailResponse>>
{
    public async Task<Result<WebVerifyEmailResponse>> Handle(WebVerifyEmailCommand request, CancellationToken cancellationToken)
    {
        FirebaseTokenValidationResult evidence;
        try
        {
            evidence = await firebaseAuthService.VerifyIdTokenAsync(request.FirebaseIdToken, cancellationToken);
        }
        catch (FirebaseUnavailableException)
        {
            return Result.Failure<WebVerifyEmailResponse>(AuthErrorCodes.VerificationUnavailable,
                "Email verification is temporarily unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result.Failure<WebVerifyEmailResponse>(AuthErrorCodes.Msg14, "Invalid or expired Firebase authentication token.");
        }

        if (!evidence.EmailVerified)
            return Result.Failure<WebVerifyEmailResponse>(AuthErrorCodes.MsgEmailNotVerified, "Email has not been verified.");
        if (string.IsNullOrWhiteSpace(evidence.Email))
            return Result.Failure<WebVerifyEmailResponse>(AuthErrorCodes.VerificationEmailMissing, "Email verification evidence has no usable email.");

        var email = evidence.Email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null)
            return Result.Failure<WebVerifyEmailResponse>(AuthErrorCodes.VerificationUserNotFound, "User account was not found. Please register first.");
        switch (user.Status)
        {
            case AccountStatus.Locked:
                return Result.Failure<WebVerifyEmailResponse>(AuthErrorCodes.AccountLocked, "Your account is locked. Please contact support.");
            case AccountStatus.Inactive:
                return Result.Failure<WebVerifyEmailResponse>(AuthErrorCodes.AccountInactive, "Your account is inactive. Please contact support.");
            case AccountStatus.PendingEmailVerification:
                var now = dateTimeProvider.UtcNow;
                user.Status = AccountStatus.Active;
                user.EmailVerifiedAtUtc = now;
                user.UpdatedAtUtc = now;
                await dbContext.SaveChangesAsync(cancellationToken);
                break;
            case AccountStatus.Active:
            case AccountStatus.PendingApproval:
            case AccountStatus.Rejected:
                // Preserve current legacy/application state; sign-in applies all eligibility gates.
                break;
            default:
                return Result.Failure<WebVerifyEmailResponse>(AuthErrorCodes.AccountInactive, "Your account is inactive. Please contact support.");
        }
        return Result.Success(new WebVerifyEmailResponse(true));
    }
}