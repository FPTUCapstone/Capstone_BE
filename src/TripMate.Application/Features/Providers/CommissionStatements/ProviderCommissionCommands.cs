using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Providers.CommissionStatements;

public sealed record ReportCommissionPaymentCommand(
    long StatementId,
    long CurrentUserId,
    string BankTransferReference);

public sealed class ReportCommissionPaymentCommandHandler
{
    private readonly IApplicationDbContext _context;

    public ReportCommissionPaymentCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<ProviderStatementItemDto>> HandleAsync(
        ReportCommissionPaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.BankTransferReference))
        {
            return Result<ProviderStatementItemDto>.Failure(
                CommissionStatementErrorCodes.MissingBankReference,
                "Bank transfer reference is required.");
        }

        var provider = await _context.ServiceProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == command.CurrentUserId, cancellationToken);

        if (provider is null)
        {
            return Result<ProviderStatementItemDto>.Failure(
                CommissionStatementErrorCodes.Forbidden,
                "The current account is not linked to a service provider.");
        }

        var statement = await _context.ServiceCommissionStatements
            .FirstOrDefaultAsync(s => s.Id == command.StatementId, cancellationToken);

        if (statement is null)
        {
            return Result<ProviderStatementItemDto>.Failure(
                CommissionStatementErrorCodes.NotFound,
                "Commission statement was not found.");
        }

        // BR-193: Service Provider can only report payment for their own statement
        if (statement.ProviderId != provider.Id)
        {
            return Result<ProviderStatementItemDto>.Failure(
                CommissionStatementErrorCodes.Forbidden,
                "You do not have permission to modify this statement.");
        }

        if (statement.CommissionAmount == 0)
        {
            return Result<ProviderStatementItemDto>.Failure(
                CommissionStatementErrorCodes.InvalidState,
                "No payment is due for this commission statement.");
        }

        // Validate state: must be Issued or Overdue
        if (statement.Status is not (ServiceCommissionStatement.StatusIssued or ServiceCommissionStatement.StatusOverdue))
        {
            return Result<ProviderStatementItemDto>.Failure(
                CommissionStatementErrorCodes.InvalidState,
                "Payment can only be reported for statements in Issued or Overdue status.");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        statement.ReportPayment(command.BankTransferReference, nowUtc);

        await _context.SaveChangesAsync(cancellationToken);

        var dto = new ProviderStatementItemDto(
            statement.Id,
            statement.StatementCode,
            statement.Period,
            statement.TotalRequests,
            statement.TotalActualAmount,
            statement.CommissionRate,
            statement.CommissionAmount,
            statement.IssueDate,
            statement.DueDate,
            statement.Status,
            statement.BankTransferReference,
            statement.ReportedAtUtc,
            statement.ConfirmedAtUtc,
            statement.ReceiptNumber);

        return Result<ProviderStatementItemDto>.Success(dto);
    }
}