using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Providers.CommissionStatements;

public sealed record GetProviderStatementsQuery(
    long CurrentUserId,
    string? Status = null,
    string? Period = null,
    string? Keyword = null,
    int PageNumber = 1,
    int PageSize = 20);

public sealed record GetProviderStatementDetailQuery(
    long StatementId,
    long CurrentUserId);

public sealed class ProviderCommissionQueryHandler
{
    private readonly IApplicationDbContext _context;

    public ProviderCommissionQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public static readonly TripMateBankDetailsDto DefaultBankDetails = new(
        BankName: "MB Bank (Ngân hàng Quân Đội)",
        AccountNumber: "999988887777",
        AccountHolder: "CONG TY TNHH TRIPMATE VIETNAM",
        TransferSyntax: "TRIPMATE {CODE}");

    public async Task<Result<PaginatedList<ProviderStatementItemDto>>> HandleAsync(
        GetProviderStatementsQuery query,
        CancellationToken cancellationToken = default)
    {
        var provider = await _context.ServiceProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == query.CurrentUserId, cancellationToken);

        if (provider is null)
        {
            return Result<PaginatedList<ProviderStatementItemDto>>.Failure(
                CommissionStatementErrorCodes.Forbidden,
                "The current account is not linked to a service provider.");
        }

        var dbQuery = _context.ServiceCommissionStatements
            .AsNoTracking()
            .Where(s => s.ProviderId == provider.Id);

        if (!string.IsNullOrWhiteSpace(query.Status) && !string.Equals(query.Status, "All", StringComparison.OrdinalIgnoreCase))
        {
            dbQuery = dbQuery.Where(s => s.Status == query.Status);
        }

        if (!string.IsNullOrWhiteSpace(query.Period) && !string.Equals(query.Period, "All", StringComparison.OrdinalIgnoreCase))
        {
            dbQuery = dbQuery.Where(s => s.Period == query.Period);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var term = query.Keyword.Trim();
            dbQuery = dbQuery.Where(s => s.StatementCode.Contains(term)
                || (s.BankTransferReference != null && s.BankTransferReference.Contains(term))
                || (s.VerifiedBankTransferReference != null && s.VerifiedBankTransferReference.Contains(term)));
        }

        var page = query.PageNumber < 1 ? 1 : query.PageNumber;
        var size = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;

        var totalCount = await dbQuery.CountAsync(cancellationToken);

        var items = await dbQuery
            .OrderByDescending(s => s.Period)
            .ThenByDescending(s => s.DueDate)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(s => new ProviderStatementItemDto(
                s.Id,
                s.StatementCode,
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
                s.ConfirmedAtUtc,
                s.ReceiptNumber))
            .ToListAsync(cancellationToken);

        return Result<PaginatedList<ProviderStatementItemDto>>.Success(
            new PaginatedList<ProviderStatementItemDto>(items, totalCount, page, size));
    }

    public async Task<Result<ProviderStatementDetailDto>> HandleAsync(
        GetProviderStatementDetailQuery query,
        CancellationToken cancellationToken = default)
    {
        var provider = await _context.ServiceProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == query.CurrentUserId, cancellationToken);

        if (provider is null)
        {
            return Result<ProviderStatementDetailDto>.Failure(
                CommissionStatementErrorCodes.Forbidden,
                "The current account is not linked to a service provider.");
        }

        var statement = await _context.ServiceCommissionStatements
            .AsNoTracking()
            .Include(s => s.Provider)
            .Include(s => s.Bookings)
                .ThenInclude(b => b.Service)
            .FirstOrDefaultAsync(s => s.Id == query.StatementId, cancellationToken);

        if (statement is null)
        {
            return Result<ProviderStatementDetailDto>.Failure(
                CommissionStatementErrorCodes.NotFound,
                "Commission statement was not found.");
        }

        // BR-193: Service Provider may access only their own statements
        if (statement.ProviderId != provider.Id)
        {
            return Result<ProviderStatementDetailDto>.Failure(
                CommissionStatementErrorCodes.Forbidden,
                "You do not have permission to view this statement.");
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

        var bankDetails = DefaultBankDetails with
        {
            TransferSyntax = $"TRIPMATE {statement.StatementCode}"
        };

        var dto = new ProviderStatementDetailDto(
            statement.Id,
            statement.StatementCode,
            statement.ProviderId,
            statement.Provider.Name,
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
            statement.ConfirmedAtUtc,
            statement.ReceiptNumber,
            bankDetails,
            requests);

        return Result<ProviderStatementDetailDto>.Success(dto);
    }
}
