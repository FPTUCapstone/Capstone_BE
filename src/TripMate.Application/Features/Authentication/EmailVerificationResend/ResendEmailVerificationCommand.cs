using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.EmailVerificationResend;

public sealed record ResendEmailVerificationCommand(string? Email, string? Password)
    : IRequest<Result<ResendEmailVerificationResponse>>;

public sealed record ResendEmailVerificationResponse(string MessageCode);

public sealed class ResendEmailVerificationCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasherService passwordHasher,
    IEmailVerificationLinkService linkService,
    IEmailVerificationSender emailSender,
    IEmailVerificationResendCooldown cooldown,
    IDateTimeProvider clock)
    : IRequestHandler<ResendEmailVerificationCommand, Result<ResendEmailVerificationResponse>>
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    public async Task<Result<ResendEmailVerificationResponse>> Handle(
        ResendEmailVerificationCommand request,
        CancellationToken cancellationToken)
    {
        var normalized = request with { Email = request.Email?.Trim().ToLowerInvariant() };
        var validation = await new Validator().ValidateAsync(normalized, cancellationToken);
        if (!validation.IsValid)
        {
            var fields = validation.Errors.GroupBy(e => e.PropertyName.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorCode).ToArray());
            return Result.Failure<ResendEmailVerificationResponse>(
                AuthErrorCodes.RequestInvalid,
                "Invalid resend request.",
                new Dictionary<string, object?> { ["errors"] = fields });
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(
            x => x.Email == normalized.Email,
            cancellationToken);
        if (user?.PasswordHash is null || !passwordHasher.Verify(normalized.Password!, user.PasswordHash))
        {
            return Result.Failure<ResendEmailVerificationResponse>(
                AuthErrorCodes.InvalidCredentials,
                "Email or password is incorrect.");
        }

        if (user.Status != AccountStatus.PendingEmailVerification)
        {
            return Result.Failure<ResendEmailVerificationResponse>(
                AuthErrorCodes.VerificationResendNotAllowed,
                "Email verification resend is not available for this account.");
        }

        if (!cooldown.TryAcquire(user.Id, clock.UtcNow, Cooldown))
        {
            return Result.Failure<ResendEmailVerificationResponse>(
                AuthErrorCodes.MsgCooldown,
                "Please wait before requesting another verification email.");
        }

        string link;
        try
        {
            link = await linkService.GenerateAsync(user.Email!, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<ResendEmailVerificationResponse>(
                AuthErrorCodes.VerificationUnavailable,
                "Email verification is temporarily unavailable.");
        }

        var delivery = await emailSender.SendAsync(user.Email!, link, cancellationToken);
        if (delivery.Status != EmailDeliveryStatus.Delivered)
        {
            return Result.Failure<ResendEmailVerificationResponse>(
                AuthErrorCodes.MsgEmailSendFailed,
                "Verification email delivery failed.");
        }

        return Result.Success(new ResendEmailVerificationResponse(AuthErrorCodes.MsgResendSuccess));
    }

    private sealed class Validator : AbstractValidator<ResendEmailVerificationCommand>
    {
        public Validator()
        {
            RuleFor(x => x.Email).Cascade(CascadeMode.Stop)
                .NotEmpty().WithErrorCode(AuthErrorCodes.Msg01)
                .EmailAddress().WithErrorCode(AuthErrorCodes.Msg02);
            RuleFor(x => x.Password).NotEmpty().WithErrorCode(AuthErrorCodes.Msg01);
        }
    }
}