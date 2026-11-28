using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.CommissionStatements;

public static class CommissionPaymentSettlement
{
    public static async Task<Result<bool>> SettleAsync(IApplicationDbContext db, long orderCode, decimal amount, string? currency,
        string? linkId, string? reference, PaymentGateway gateway, DateTimeOffset now, CancellationToken ct, long? owner = null)
    {
        if (!CommissionPaymentAttempt.IsCommissionOrderCode(orderCode) || amount <= 0 || amount != decimal.Truncate(amount) ||
            currency != "VND" || string.IsNullOrWhiteSpace(linkId) || string.IsNullOrWhiteSpace(reference) || reference.Length > 150)
            return Invalid("Invalid commission callback fields.");
        try
        {
            return await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                var payment = await db.CommissionPaymentAttempts.SingleOrDefaultAsync(a => a.OrderCode == orderCode, token);
                if (payment is null || payment.Gateway != gateway || payment.AmountVnd != amount || payment.PaymentLinkId != linkId)
                    return Invalid("Callback does not match a commission payment.");
                var statement = await db.DirectCommissionStatements.SingleAsync(s => s.Id == payment.StatementId, token);
                if (owner is not null && statement.TourOperatorUserId != owner) return Invalid("Access denied.");
                if (statement.CommissionAmountVnd != amount) return Invalid("Commission amount mismatch.");
                if (payment.Status == CommissionPaymentAttempt.Success && statement.Status == DirectCommissionStatement.StatusPaid)
                    return payment.GatewayReference == reference ? Result.Success(true) : Invalid("Payment reference mismatch.");
                if (payment.Status != CommissionPaymentAttempt.Pending || statement.Status is not (DirectCommissionStatement.StatusIssued or DirectCommissionStatement.StatusOverdue))
                    return Invalid("Payment is not eligible for completion.");
                if (gateway == PaymentGateway.Mock && payment.ExpiresAtUtc <= now) return Invalid("Mock payment expired.");
                if (await db.CommissionPaymentAttempts.AnyAsync(a => a.Id != payment.Id && a.Gateway == gateway && a.GatewayReference == reference, token))
                    return Invalid("This bank transaction was already recorded.");
                payment.Complete(reference, now);
                statement.MarkPaid(payment.AmountVnd, now, now);
                db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(owner, "CommissionPaymentCompleted", "DirectCommissionStatement",
                    statement.Id, now, AuditOutcome.Success, afterData: System.Text.Json.JsonSerializer.Serialize(new { PaymentId = payment.Id, Gateway = gateway.ToString(), Amount = payment.AmountVnd })));
                await db.SaveChangesAsync(token);
                return Result.Success(true);
            }, ct);
        }
        catch (DbUpdateException) { return Invalid("Payment changed concurrently. Retry the callback."); }
    }
    private static Result<bool> Invalid(string message) => Result.Failure<bool>("commission.payment_callback_invalid", message);
}

public sealed record CompleteMockCommissionPaymentCommand(long StatementId, long PaymentId) : IRequest<Result<bool>>;
public sealed class CompleteMockCommissionPaymentCommandHandler(IApplicationDbContext db, ICurrentUserService user,
    PaymentOptions options, IDateTimeProvider clock) : IRequestHandler<CompleteMockCommissionPaymentCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(CompleteMockCommissionPaymentCommand request, CancellationToken ct)
    {
        if (!DirectCommissionAccess.Allowed(user, false)) return Result.Failure<bool>("commission.forbidden", "Tour Operator access required.");
        if (!string.Equals(options.Provider, "Mock", StringComparison.OrdinalIgnoreCase)) return Result.Failure<bool>("commission.mock_disabled", "Mock payments are disabled.");
        var statement = await db.DirectCommissionStatements.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.StatementId && s.TourOperatorUserId == user.UserId, ct);
        if (statement is null) return Result.Failure<bool>("commission.forbidden", "Access denied.");
        var payment = await db.CommissionPaymentAttempts.AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.PaymentId && p.StatementId == statement.Id, ct);
        if (payment is null || payment.Gateway != PaymentGateway.Mock) return Result.Failure<bool>("commission.mock_disabled", "This is not a mock commission payment.");
        return await CommissionPaymentSettlement.SettleAsync(db, payment.OrderCode, payment.AmountVnd, "VND", payment.PaymentLinkId,
            $"MOCK-COMMISSION-{payment.Id}", PaymentGateway.Mock, clock.UtcNow, ct, user.UserId);
    }
}