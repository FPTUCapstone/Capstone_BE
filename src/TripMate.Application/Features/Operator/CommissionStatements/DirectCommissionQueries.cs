using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.CommissionStatements;

public sealed record DirectCommissionPaymentInfo(long PaymentId, string Provider, string Status, string? CheckoutUrl,
    DateTimeOffset? ExpiresAtUtc, bool CanCompleteMock);
public sealed record DirectCommissionCalculationInfo(long GrossRevenueVnd, long CompletedRefundsVnd,
    long RevenueCommissionVnd, long RefundCommissionCreditVnd, long CreditBroughtForwardVnd,
    long CreditAppliedVnd, long CreditCarriedForwardVnd, int HeldBookingCount, int AmbiguousLegacyBookingCount);
public sealed record DirectCommissionStatementDto(long Id, string StatementCode, long TourOperatorUserId, string Period,
    long EligibleBaseVnd, decimal CommissionRateSnapshot, long CommissionAmountVnd, string Status,
    DateTimeOffset IssuedAtUtc, DateTimeOffset DueAtUtc, DateTimeOffset? PaidAtUtc, long PaidAmountVnd,
    IReadOnlyList<string> AllowedActions, DirectCommissionPaymentInfo? Payment = null, DirectCommissionCalculationInfo? Calculation = null);
public sealed record DirectCommissionStatementPage(IReadOnlyList<DirectCommissionStatementDto> Items, int TotalCount, int PageNumber, int PageSize);
public sealed record ListDirectCommissionStatementsQuery(int PageNumber = 1, int PageSize = 20, string? Status = null,
    string? Period = null, bool AdminView = false, long? TourOperatorUserId = null) : IRequest<Result<DirectCommissionStatementPage>>;
public sealed record GetDirectCommissionStatementQuery(long Id, bool AdminView = false) : IRequest<Result<DirectCommissionStatementDto>>;

public static class DirectCommissionAccess
{
    public const string Forbidden = "commission.forbidden";
    public const string NotFound = "commission.not_found";
    public const string Invalid = "commission.invalid_query";
    public static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    public static bool Allowed(ICurrentUserService user, bool admin) => user.UserId is > 0 &&
        (admin ? user.Role is nameof(UserRole.Administrator) or nameof(UserRole.Staff) : user.Role == nameof(UserRole.TourOperator));
    public static bool ValidPeriod(string? value) => value is not null && value.Length == 7 &&
        DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    public static string EffectiveStatus(DirectCommissionStatement s, DateTimeOffset now) =>
        s.CommissionAmountVnd > 0 && s.Status == DirectCommissionStatement.StatusIssued && DateOnly.FromDateTime(now.ToOffset(VietnamOffset).DateTime) > s.DueDate
            ? DirectCommissionStatement.StatusOverdue : s.Status;

    public static async Task<List<DirectCommissionStatementDto>> MapAsync(IApplicationDbContext db,
        IReadOnlyList<DirectCommissionStatement> statements, DateTimeOffset now, PaymentOptions? options, CancellationToken ct)
    {
        var ids = statements.Select(s => s.Id).ToArray();
        var attempts = (await db.CommissionPaymentAttempts.AsNoTracking().Where(p => ids.Contains(p.StatementId)).ToListAsync(ct))
            .GroupBy(p => p.StatementId).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Id).First());
        var calculations = await db.DirectCommissionCalculations.AsNoTracking().Where(c => ids.Contains(c.StatementId)).ToDictionaryAsync(c => c.StatementId, ct);
        return statements.Select(s =>
        {
            var status = EffectiveStatus(s, now);
            var payable = s.CommissionAmountVnd > 0 && status is (DirectCommissionStatement.StatusIssued or DirectCommissionStatement.StatusOverdue);
            List<string> actions = payable ? ["Pay"] : [];
            DirectCommissionPaymentInfo? payment = null;
            if (attempts.TryGetValue(s.Id, out var a))
            {
                bool active = payable && a.Status == CommissionPaymentAttempt.Pending && a.ExpiresAtUtc > now;
                bool mock = active && a.Gateway == PaymentGateway.Mock && string.Equals(options?.Provider, "Mock", StringComparison.OrdinalIgnoreCase);
                if (mock) actions.Add("CompleteMock");
                payment = new(a.Id, a.Gateway.ToString(), a.Status, active ? a.CheckoutUrl : null, a.ExpiresAtUtc, mock);
            }
            var due = new DateTimeOffset(s.DueDate.Year, s.DueDate.Month, s.DueDate.Day, 0, 0, 0, VietnamOffset).AddDays(1).AddTicks(-1).ToUniversalTime();
            DirectCommissionCalculationInfo? calculation = calculations.TryGetValue(s.Id, out var c)
                ? new(c.GrossRevenueVnd, c.CompletedRefundsVnd, c.RevenueCommissionVnd, c.RefundCommissionCreditVnd,
                    c.CreditBroughtForwardVnd, c.CreditAppliedVnd, c.CreditCarriedForwardVnd, c.HeldBookingCount, c.AmbiguousLegacyBookingCount) : null;
            return new DirectCommissionStatementDto(s.Id, s.StatementCode, s.TourOperatorUserId, s.Period, s.EligibleBaseVnd,
                s.CommissionRate, s.CommissionAmountVnd, status, s.CreatedAtUtc, due, s.PaidAtUtc, s.PaidAmountVnd, actions, payment, calculation);
        }).ToList();
    }
}

public sealed class ListDirectCommissionStatementsQueryHandler(IApplicationDbContext db, ICurrentUserService user,
    PaymentOptions? options = null, IDateTimeProvider? clock = null)
    : IRequestHandler<ListDirectCommissionStatementsQuery, Result<DirectCommissionStatementPage>>
{
    public async Task<Result<DirectCommissionStatementPage>> Handle(ListDirectCommissionStatementsQuery request, CancellationToken ct)
    {
        if (!DirectCommissionAccess.Allowed(user, request.AdminView)) return Result.Failure<DirectCommissionStatementPage>(DirectCommissionAccess.Forbidden, "Access denied.");
        string[] statuses = [DirectCommissionStatement.StatusIssued, DirectCommissionStatement.StatusOverdue,
            DirectCommissionStatement.StatusPaid, DirectCommissionStatement.StatusCancelled, DirectCommissionStatement.StatusPaymentReported];
        if (request.PageNumber < 1 || request.PageSize is < 1 or > 100 || request.PageNumber > int.MaxValue / request.PageSize ||
            request.Status is not null && !statuses.Contains(request.Status) || request.Period is not null && !DirectCommissionAccess.ValidPeriod(request.Period) ||
            request.TourOperatorUserId is <= 0 || !request.AdminView && request.TourOperatorUserId is not null)
            return Result.Failure<DirectCommissionStatementPage>(DirectCommissionAccess.Invalid, "Invalid filter or pagination.");
        var now = clock?.UtcNow ?? DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.ToOffset(DirectCommissionAccess.VietnamOffset).DateTime);
        var query = db.DirectCommissionStatements.AsNoTracking();
        if (!request.AdminView) query = query.Where(s => s.TourOperatorUserId == user.UserId);
        else if (request.TourOperatorUserId is { } owner) query = query.Where(s => s.TourOperatorUserId == owner);
        if (request.Period is not null) query = query.Where(s => s.Period == request.Period);
        if (request.Status == DirectCommissionStatement.StatusOverdue)
            query = query.Where(s => s.CommissionAmountVnd > 0 && (s.Status == DirectCommissionStatement.StatusOverdue || s.Status == DirectCommissionStatement.StatusIssued && s.DueDate < today));
        else if (request.Status == DirectCommissionStatement.StatusIssued)
            query = query.Where(s => s.Status == DirectCommissionStatement.StatusIssued && (s.CommissionAmountVnd == 0 || s.DueDate >= today));
        else if (request.Status is not null) query = query.Where(s => s.Status == request.Status);
        int count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(s => s.Period).ThenByDescending(s => s.Id)
            .Skip((request.PageNumber - 1) * request.PageSize).Take(request.PageSize).ToListAsync(ct);
        var dto = await DirectCommissionAccess.MapAsync(db, rows, now, options, ct);
        return Result.Success(new DirectCommissionStatementPage(dto, count, request.PageNumber, request.PageSize));
    }
}

public sealed class GetDirectCommissionStatementQueryHandler(IApplicationDbContext db, ICurrentUserService user,
    PaymentOptions? options = null, IDateTimeProvider? clock = null)
    : IRequestHandler<GetDirectCommissionStatementQuery, Result<DirectCommissionStatementDto>>
{
    public async Task<Result<DirectCommissionStatementDto>> Handle(GetDirectCommissionStatementQuery request, CancellationToken ct)
    {
        if (!DirectCommissionAccess.Allowed(user, request.AdminView)) return Result.Failure<DirectCommissionStatementDto>(DirectCommissionAccess.Forbidden, "Access denied.");
        var query = db.DirectCommissionStatements.AsNoTracking().Where(s => s.Id == request.Id);
        if (!request.AdminView) query = query.Where(s => s.TourOperatorUserId == user.UserId);
        var row = await query.SingleOrDefaultAsync(ct);
        if (row is null) return Result.Failure<DirectCommissionStatementDto>(DirectCommissionAccess.NotFound, "Statement not found.");
        var dto = await DirectCommissionAccess.MapAsync(db, [row], clock?.UtcNow ?? DateTimeOffset.UtcNow, options, ct);
        return Result.Success(dto.Single());
    }
}
