using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Providers.CommissionStatements;

public sealed record ConfirmCommissionReceiptCommand(
    long StatementId,
    long StaffUserId,
    string? Notes = null,
    string? BankTransferReference = null,
    decimal? ReceivedAmountVnd = null);

public sealed record GenerateMonthlyCommissionStatementsCommand(string? TargetPeriod = null);

public sealed record ProcessCommissionOverdueCommand(DateTimeOffset? NowUtc = null);

public sealed class AdminReconciliationCommandHandler
{
    private const string ReferenceAlreadyUsedMessage = "This bank transfer is already recorded against another commission statement.";
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
        var attempted = false;
        try
        {
            return await _context.ExecuteInSerializableTransactionAsync(ct =>
            {
                if (attempted) _context.ClearTrackedEntities();
                attempted = true;
                return ConfirmAsync(command, ct);
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (_context.IsCommissionReceiptReferenceConflict(exception))
        {
            _context.ClearTrackedEntities();
            return Result<ReconciliationStatementItemDto>.Failure(
                CommissionStatementErrorCodes.BankReferenceAlreadyUsed, ReferenceAlreadyUsedMessage);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException || _context.IsDeadlock(exception))
        {
            _context.ClearTrackedEntities();
            return Result<ReconciliationStatementItemDto>.Failure(
                CommissionStatementErrorCodes.InvalidState,
                "The statement changed while you were recording the receipt. Refresh it and verify the details.");
        }
    }

    private async Task<Result<ReconciliationStatementItemDto>> ConfirmAsync(
        ConfirmCommissionReceiptCommand command, CancellationToken cancellationToken)
    {
        if (command.StaffUserId <= 0 || !await _context.Users.AnyAsync(
            user => user.Id == command.StaffUserId && user.Role == UserRole.Staff && user.Status == AccountStatus.Active,
            cancellationToken))
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

        if (statement.CommissionAmount <= 0
            || statement.Status is not (ServiceCommissionStatement.StatusPaymentReported or ServiceCommissionStatement.StatusOverdue))
        {
            return Result<ReconciliationStatementItemDto>.Failure(
                CommissionStatementErrorCodes.InvalidState,
                "Only an overdue or payment-reported statement with a positive amount can be reconciled.");
        }

        var verifiedReference = command.BankTransferReference?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(verifiedReference)
            || verifiedReference.Length > ServiceCommissionStatement.BankTransferReferenceMaxLength)
        {
            return Result<ReconciliationStatementItemDto>.Failure(
                CommissionStatementErrorCodes.MissingBankReference,
                "Enter the bank transfer reference you verified before recording the receipt.");
        }
        if (command.ReceivedAmountVnd != statement.CommissionAmount)
        {
            return Result<ReconciliationStatementItemDto>.Failure(
                CommissionStatementErrorCodes.ReceiptAmountMismatch,
                "The amount credited to TripMate must match the amount due on this statement.");
        }
        var referenceUsed = await _context.ServiceCommissionStatements.AnyAsync(other =>
            other.Id != statement.Id
            && (other.VerifiedBankTransferReference == verifiedReference
                || (other.VerifiedBankTransferReference == null && other.Status == ServiceCommissionStatement.StatusPaid
                    && other.BankTransferReference != null && other.BankTransferReference.Trim().ToUpper() == verifiedReference)),
            cancellationToken);
        if (referenceUsed)
        {
            return Result<ReconciliationStatementItemDto>.Failure(
                CommissionStatementErrorCodes.BankReferenceAlreadyUsed, ReferenceAlreadyUsedMessage);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var receiptNumber = $"REC-{statement.Period.Replace("-", "")}-{statement.Id:D6}";
        var beforeStatus = statement.Status;

        statement.ConfirmReceipt(command.StaffUserId, nowUtc, receiptNumber, verifiedReference);

        // BR-174: The recorded receipt is written to the immutable audit log
        var audit = new AuditLog(
            actorUserId: command.StaffUserId,
            actionType: AuditActionTypes.CommissionReceiptConfirmed,
            affectedEntity: "ServiceCommissionStatement",
            affectedEntityId: statement.Id,
            createdAtUtc: nowUtc,
            beforeData: $"Status={beforeStatus}",
            afterData: JsonSerializer.Serialize(new
            {
                Status = ServiceCommissionStatement.StatusPaid,
                ReceiptNumber = receiptNumber,
                ReportedBankTransferReference = statement.BankTransferReference,
                VerifiedBankTransferReference = verifiedReference,
                command.ReceivedAmountVnd,
                command.Notes,
            }));

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
            statement.VerifiedBankTransferReference ?? statement.BankTransferReference,
            statement.ReportedAtUtc,
            statement.ConfirmedByUserId,
            statement.ConfirmedAtUtc,
            statement.ReceiptNumber,
            statement.BankTransferReference,
            statement.VerifiedBankTransferReference);

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