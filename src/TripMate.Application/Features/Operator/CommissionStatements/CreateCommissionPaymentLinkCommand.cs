using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.CommissionStatements;

public sealed record CreateCommissionPaymentLinkCommand(long StatementId) : IRequest<Result<CreateCommissionPaymentLinkResponse>>;
public sealed record CreateCommissionPaymentLinkResponse(string? CheckoutUrl, long CommissionAmountVnd, string Provider,
    long PaymentId, DateTimeOffset ExpiresAtUtc, string Status);

public sealed class CreateCommissionPaymentLinkCommandHandler(IApplicationDbContext db, ICurrentUserService user,
    IPaymentGateway gateway, IDateTimeProvider clock, PaymentOptions? options = null)
    : IRequestHandler<CreateCommissionPaymentLinkCommand, Result<CreateCommissionPaymentLinkResponse>>
{
    private sealed record Claim(CommissionPaymentAttempt Attempt, bool Create);
    public async Task<Result<CreateCommissionPaymentLinkResponse>> Handle(CreateCommissionPaymentLinkCommand request, CancellationToken ct)
    {
        if (!DirectCommissionAccess.Allowed(user, false)) return Fail("commission.forbidden", "Tour Operator access required.");
        try
        {
            var claim = await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                var s = await db.DirectCommissionStatements.SingleOrDefaultAsync(s => s.Id == request.StatementId && s.TourOperatorUserId == user.UserId, token);
                if (s is null) return Result.Failure<Claim>("commission.forbidden", "Statement not found or access denied.");
                if (s.Status is not (DirectCommissionStatement.StatusIssued or DirectCommissionStatement.StatusOverdue) || s.CommissionAmountVnd <= 0)
                    return Result.Failure<Claim>("commission.invalid_state", "Statement is not payable.");
                var now = clock.UtcNow;
                var active = await db.CommissionPaymentAttempts.Where(a => a.StatementId == s.Id &&
                    (a.Status == CommissionPaymentAttempt.Creating || a.Status == CommissionPaymentAttempt.Pending)).SingleOrDefaultAsync(token);
                if (active is not null)
                {
                    if (active.Status == CommissionPaymentAttempt.Pending && active.ExpiresAtUtc > now)
                        return Result.Success(new Claim(active, false));
                    if (active.Status == CommissionPaymentAttempt.Creating && active.CreatedAtUtc.AddMinutes(2) > now)
                        return Result.Failure<Claim>("commission.payment_in_progress", "A payment link is being prepared. Refresh shortly.");
                    if (active.Gateway != gateway.Gateway)
                        return Result.Failure<Claim>("commission.payment_gateway_changed", "Restore the original provider before replacing this link.");
                    await gateway.CancelCommissionPaymentLinkAsync(new(active.OrderCode, active.PaymentLinkId, "Replace expired commission payment"), token);
                    active.Cancel(now);
                    await db.SaveChangesAsync(token);
                }
                var attempt = CommissionPaymentAttempt.Create(s.Id, gateway.Gateway, s.CommissionAmountVnd, now, now.AddMinutes(15));
                if (await db.PaymentTransactions.AnyAsync(p => p.OrderCode == attempt.OrderCode, token))
                    return Result.Failure<Claim>("commission.payment_conflict", "Payment reference conflict. Retry.");
                db.CommissionPaymentAttempts.Add(attempt);
                await db.SaveChangesAsync(token);
                return Result.Success(new Claim(attempt, true));
            }, ct);
            if (claim.IsFailure) return Fail(claim.ErrorCode!, claim.ErrorMessage!);
            var a = claim.Value!.Attempt;
            if (!claim.Value.Create) return Result.Success(Map(a));
            var link = await gateway.CreatePaymentLinkAsync(new(0, a.OrderCode, a.AmountVnd, "VND",
                $"HH{a.OrderCode % 10000000:D7}", a.ExpiresAtUtc,
                CommissionReturnUrl(options?.PayOs.ReturnUrl, a.StatementId), CommissionReturnUrl(options?.PayOs.CancelUrl, a.StatementId)), ct);
            if (string.IsNullOrWhiteSpace(link.PaymentLinkId) || link.PaymentLinkId.Length > 100 ||
                a.Gateway == PaymentGateway.PayOS && !SafePayOsUrl(link.CheckoutUrl))
                return Fail("commission.payment_gateway_unavailable", "The gateway returned an invalid payment link.");
            return await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                var attempt = await db.CommissionPaymentAttempts.SingleAsync(p => p.Id == a.Id, token);
                var statement = await db.DirectCommissionStatements.SingleAsync(s => s.Id == a.StatementId, token);
                if (attempt.Status == CommissionPaymentAttempt.Pending) return Result.Success(Map(attempt));
                if (attempt.Status != CommissionPaymentAttempt.Creating || statement.Status is not (DirectCommissionStatement.StatusIssued or DirectCommissionStatement.StatusOverdue))
                    return Fail("commission.invalid_state", "Payment state changed. Refresh the statement.");
                attempt.AttachLink(link.PaymentLinkId, attempt.Gateway == PaymentGateway.Mock ? null : link.CheckoutUrl, clock.UtcNow);
                statement.RecordPaymentLink(link.PaymentLinkId, clock.UtcNow);
                await db.SaveChangesAsync(token);
                return Result.Success(Map(attempt));
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DbUpdateException) { return Fail("commission.payment_conflict", "Payment changed concurrently. Refresh and retry."); }
        catch (Exception) { return Fail("commission.payment_gateway_unavailable", "Payment gateway unavailable. Refresh before retrying."); }
    }
    private static CreateCommissionPaymentLinkResponse Map(CommissionPaymentAttempt a) => new(a.CheckoutUrl, a.AmountVnd,
        a.Gateway.ToString(), a.Id, a.ExpiresAtUtc, a.Status);
    private static Result<CreateCommissionPaymentLinkResponse> Fail(string code, string message) => Result.Failure<CreateCommissionPaymentLinkResponse>(code, message);
    private static bool SafePayOsUrl(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme == "https" &&
        string.IsNullOrEmpty(u.UserInfo) && (u.Host == "payos.vn" || u.Host.EndsWith(".payos.vn", StringComparison.OrdinalIgnoreCase));
    private static string? CommissionReturnUrl(string? configured, long id) => Uri.TryCreate(configured, UriKind.Absolute, out var u) &&
        (u.Scheme == "https" || u.Scheme == "http" && u.IsLoopback) && string.IsNullOrEmpty(u.UserInfo)
            ? $"{u.GetLeftPart(UriPartial.Authority)}/partner/commission-statements/{id}" : null;
}