using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Providers.CommissionStatements;

public sealed record GetReconciliationStatementsQuery(
    long? ProviderId = null,
    string? Period = null,
    string? Status = null,
    string? Keyword = null,
    int PageNumber = 1,
    int PageSize = 20);

public sealed record GetReconciliationStatementDetailQuery(long StatementId);

public sealed class AdminReconciliationQueryHandler
{
    private readonly IApplicationDbContext _context;

    public AdminReconciliationQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<PaginatedList<ReconciliationStatementItemDto>>> HandleAsync(
        GetReconciliationStatementsQuery query,
        CancellationToken cancellationToken = default)
    {
        var dbQuery = _context.ServiceCommissionStatements
            .AsNoTracking()
            .Include(s => s.Provider)
            .AsQueryable();

        if (query.ProviderId.HasValue && query.ProviderId.Value > 0)
        {
            dbQuery = dbQuery.Where(s => s.ProviderId == query.ProviderId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Period) && !string.Equals(query.Period, "All", StringComparison.OrdinalIgnoreCase))
        {
            dbQuery = dbQuery.Where(s => s.Period == query.Period);
        }

        if (!string.IsNullOrWhiteSpace(query.Status) && !string.Equals(query.Status, "All", StringComparison.OrdinalIgnoreCase))
        {
            dbQuery = dbQuery.Where(s => s.Status == query.Status);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var term = query.Keyword.Trim();
            dbQuery = dbQuery.Where(s => s.StatementCode.Contains(term)
                                         || (s.BankTransferReference != null && s.BankTransferReference.Contains(term))
                                         || (s.VerifiedBankTransferReference != null && s.VerifiedBankTransferReference.Contains(term))
                                         || s.Provider.Name.Contains(term));
        }

        var page = query.PageNumber < 1 ? 1 : query.PageNumber;
        var size = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;

        var totalCount = await dbQuery.CountAsync(cancellationToken);

        var offset = ((long)page - 1) * size;
        var items = offset >= totalCount ? [] : await dbQuery
            .OrderByDescending(s => s.Period)
            .ThenByDescending(s => s.DueDate)
            .ThenByDescending(s => s.Id)
            .Skip((int)offset)
            .Take(size)
            .Select(s => new ReconciliationStatementItemDto(
                s.Id,
                s.StatementCode,
                s.ProviderId,
                s.Provider.Name,
                s.Provider.ServiceCategory,
                s.Period,
                s.TotalRequests,
                s.TotalActualAmount,
                s.CommissionRate,
                s.CommissionAmount,
                s.IssueDate,
                s.DueDate,
                s.Status,
                s.VerifiedBankTransferReference ?? s.BankTransferReference,
                s.ReportedAtUtc,
                s.ConfirmedByUserId,
                s.ConfirmedAtUtc,
                s.ReceiptNumber,
                s.BankTransferReference,
                s.VerifiedBankTransferReference))
            .ToListAsync(cancellationToken);

        return Result<PaginatedList<ReconciliationStatementItemDto>>.Success(
            new PaginatedList<ReconciliationStatementItemDto>(items, totalCount, page, size));
    }

    public async Task<Result<ReconciliationStatementDetailDto>> HandleAsync(
        GetReconciliationStatementDetailQuery query,
        CancellationToken cancellationToken = default)
    {
        var statement = await _context.ServiceCommissionStatements
            .AsNoTracking()
            .Include(s => s.Provider)
            .Include(s => s.ConfirmedByUser)
            .Include(s => s.Bookings)
                .ThenInclude(b => b.Service)
            .FirstOrDefaultAsync(s => s.Id == query.StatementId, cancellationToken);

        if (statement is null)
        {
            return Result<ReconciliationStatementDetailDto>.Failure(
                CommissionStatementErrorCodes.NotFound,
                "Commission statement was not found.");
        }

        var requests = statement.Bookings
            .OrderBy(b => b.OutcomeAtUtc ?? b.StartAtUtc)
            .Select(b => new StatementBookingRequestDto(
                b.Id,
                b.ServiceId,
                b.Service.Name,
                b.ServiceOption,
                b.Quantity,
                $"Traveler #{b.TravelerUserId}",
                DateOnly.FromDateTime(b.StartAtUtc.DateTime),
                b.ActualAmount ?? 0m,
                b.CommissionAmount ?? 0m,
                b.OutcomeAtUtc))
            .ToList();

        var dto = new ReconciliationStatementDetailDto(
            statement.Id,
            statement.StatementCode,
            statement.ProviderId,
            statement.Provider.Name,
            statement.Provider.ServiceCategory,
            statement.Provider.ContactEmail,
            statement.Provider.ContactPhone,
            statement.Period,
            statement.TotalRequests,
            statement.TotalActualAmount,
            statement.CommissionRate,
            statement.CommissionAmount,
            statement.IssueDate,
            statement.DueDate,
            statement.Status,
            statement.VerifiedBankTransferReference ?? statement.BankTransferReference,
            statement.ReportedAtUtc,
            statement.ConfirmedByUserId,
            statement.ConfirmedByUser?.FullName,
            statement.ConfirmedAtUtc,
            statement.ReceiptNumber,
            requests,
            statement.BankTransferReference,
            statement.VerifiedBankTransferReference);

        return Result<ReconciliationStatementDetailDto>.Success(dto);
    }
}
