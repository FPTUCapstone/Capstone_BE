using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Providers.CommissionStatements;

public sealed record ConfirmCommissionReceiptCommand(
    long StatementId,
    long StaffUserId,
    string? Notes = null);

public sealed record GenerateMonthlyCommissionStatementsCommand(string? TargetPeriod = null);

public sealed record ProcessCommissionOverdueCommand(DateTimeOffset? NowUtc = null);

public sealed class AdminReconciliationCommandHandler
{
    private readonly IApplicationDbContext _context;
    private readonly ICommissionStatementService _commissionService;

    public AdminReconciliationCommandHandler(
        IApplicationDbContext context,
        ICommissionStatementService commissionService)
    {
        _context = context;
        _commissionService = commissionService;
    }

    public async Task<Result<ReconciliationStatementItemDto>> HandleAsync(
        ConfirmCommissionReceiptCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.StaffUserId <= 0)
        {
            return Result<ReconciliationStatementItemDto>.Failure(
                CommissionStatementErrorCodes.Forbidden,
                "A valid staff user is required to confirm receipt.");
        }

        var statement = await _context.ServiceCommissionStatements
            .Include(s => s.Provider)
            .FirstOrDefaultAsync(s => s.Id == command.StatementId, cancellationToken);

        if (statement is null)
        {
            return Result<ReconciliationStatementItemDto>.Failure(
                CommissionStatementErrorCodes.NotFound,
                "Commission statement was not found.");
        }

        if (statement.Status == ServiceCommissionStatement.StatusPaid)
        {
            return Result<ReconciliationStatementItemDto>.Failure(
                CommissionStatementErrorCodes.AlreadyPaid,
                "The statement is already marked as Paid.");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var receiptNumber = $"REC-{statement.Period.Replace("-", "")}-{statement.Id:D6}";
        var beforeStatus = statement.Status;

        statement.ConfirmReceipt(command.StaffUserId, nowUtc, receiptNumber);

        // BR-174: The recorded receipt is written to the immutable audit log
        var audit = new AuditLog(
            actorUserId: command.StaffUserId,
            actionType: AuditActionTypes.CommissionReceiptConfirmed,
            affectedEntity: "ServiceCommissionStatement",
            affectedEntityId: statement.Id,
            createdAtUtc: nowUtc,
            beforeData: $"Status={beforeStatus}",
            afterData: $"Status={ServiceCommissionStatement.StatusPaid}; ReceiptNumber={receiptNumber}; Notes={command.Notes}");

        _context.AuditLogs.Add(audit);

        await _context.SaveChangesAsync(cancellationToken);

        var dto = new ReconciliationStatementItemDto(
            statement.Id,
            statement.StatementCode,
            statement.ProviderId,
            statement.Provider.Name,
            statement.Provider.ServiceCategory,
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
            statement.ConfirmedByUserId,
            statement.ConfirmedAtUtc,
            statement.ReceiptNumber);

        return Result<ReconciliationStatementItemDto>.Success(dto);
    }

    public Task<StatementGenerationSummaryDto> HandleAsync(
        GenerateMonthlyCommissionStatementsCommand command,
        CancellationToken cancellationToken = default)
    {
        return _commissionService.GenerateStatementsAsync(command.TargetPeriod, cancellationToken);
    }

    public Task<OverdueProcessingSummaryDto> HandleAsync(
        ProcessCommissionOverdueCommand command,
        CancellationToken cancellationToken = default)
    {
        return _commissionService.ProcessOverdueStatementsAsync(command.NowUtc, cancellationToken);
    }
}
