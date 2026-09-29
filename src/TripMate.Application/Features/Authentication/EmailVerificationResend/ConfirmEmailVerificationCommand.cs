using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.EmailVerificationResend;

public sealed record ConfirmEmailVerificationCommand(string? Email)
    : IRequest<Result<ConfirmEmailVerificationResponse>>;

public sealed record ConfirmEmailVerificationResponse(bool EmailVerified);

public sealed class ConfirmEmailVerificationCommandHandler(
    IApplicationDbContext dbContext,
    IEmailVerificationStatusService verificationStatus,
    IDateTimeProvider clock)
    : IRequestHandler<ConfirmEmailVerificationCommand, Result<ConfirmEmailVerificationResponse>>
{
    public async Task<Result<ConfirmEmailVerificationResponse>> Handle(
        ConfirmEmailVerificationCommand request,
        CancellationToken cancellationToken)
    {
        var normalized = request with { Email = request.Email?.Trim().ToLowerInvariant() };
        var validation = await new Validator().ValidateAsync(normalized, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<ConfirmEmailVerificationResponse>(
                AuthErrorCodes.RequestInvalid,
                "Invalid email verification confirmation.");
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Email == normalized.Email,
            cancellationToken);
        if (user is null)
        {
            return Result.Failure<ConfirmEmailVerificationResponse>(
                AuthErrorCodes.VerificationUserNotFound,
                "User account was not found.");
        }

        if (user.Status == AccountStatus.Locked)
            return Result.Failure<ConfirmEmailVerificationResponse>(AuthErrorCodes.AccountLocked, "Your account is locked.");
        if (user.Status == AccountStatus.Inactive)
            return Result.Failure<ConfirmEmailVerificationResponse>(AuthErrorCodes.AccountInactive, "Your account is inactive.");

        bool isVerified;
        try
        {
            isVerified = await verificationStatus.IsVerifiedAsync(user.Email!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result.Failure<ConfirmEmailVerificationResponse>(
                AuthErrorCodes.VerificationUnavailable,
                "Email verification is temporarily unavailable.");
        }

        if (!isVerified)
        {
            return Result.Failure<ConfirmEmailVerificationResponse>(
                AuthErrorCodes.MsgEmailNotVerified,
                "Email has not been verified.");
        }

        if (user.Status == AccountStatus.PendingEmailVerification)
        {
            var now = clock.UtcNow;
            user.Status = AccountStatus.Active;
            user.EmailVerifiedAtUtc = now;
            user.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(new ConfirmEmailVerificationResponse(true));
    }

    private sealed class Validator : AbstractValidator<ConfirmEmailVerificationCommand>
    {
        public Validator() => RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}