namespace TripMate.Application.Features.Providers.CommissionStatements;

public sealed record StatementBookingRequestDto(
    long ServiceBookingId,
    long ServiceId,
    string ServiceName,
    string? ServiceOption,
    int Quantity,
    string TravelerName,
    DateOnly RequestedDate,
    decimal ActualAmount,
    decimal CommissionAmount,
    DateTimeOffset? CompletedAtUtc);

public sealed record TripMateBankDetailsDto(
    string BankName,
    string AccountNumber,
    string AccountHolder,
    string TransferSyntax);

public sealed record ProviderStatementItemDto(
    long StatementId,
    string StatementCode,
    string Period,
    int TotalRequests,
    decimal TotalActualAmount,
    decimal CommissionRate,
    decimal CommissionAmount,
    DateOnly IssueDate,
    DateOnly DueDate,
    string Status,
    string? BankTransferReference,
    DateTimeOffset? ReportedAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    string? ReceiptNumber);

public sealed record ProviderStatementDetailDto(
    long StatementId,
    string StatementCode,
    long ProviderId,
    string ProviderName,
    string Period,
    int TotalRequests,
    decimal TotalActualAmount,
    decimal CommissionRate,
    decimal CommissionAmount,
    DateOnly IssueDate,
    DateOnly DueDate,
    string Status,
    string? BankTransferReference,
    DateTimeOffset? ReportedAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    string? ReceiptNumber,
    TripMateBankDetailsDto BankDetails,
    IReadOnlyList<StatementBookingRequestDto> Requests);

public sealed record ReconciliationStatementItemDto(
    long StatementId,
    string StatementCode,
    long ProviderId,
    string ProviderName,
    string ServiceCategory,
    string Period,
    int TotalRequests,
    decimal TotalActualAmount,
    decimal CommissionRate,
    decimal CommissionAmount,
    DateOnly IssueDate,
    DateOnly DueDate,
    string Status,
    string? BankTransferReference,
    DateTimeOffset? ReportedAtUtc,
    long? ConfirmedByUserId,
    DateTimeOffset? ConfirmedAtUtc,
    string? ReceiptNumber);

public sealed record ReconciliationStatementDetailDto(
    long StatementId,
    string StatementCode,
    long ProviderId,
    string ProviderName,
    string ServiceCategory,
    string? ContactEmail,
    string? ContactPhone,
    string Period,
    int TotalRequests,
    decimal TotalActualAmount,
    decimal CommissionRate,
    decimal CommissionAmount,
    DateOnly IssueDate,
    DateOnly DueDate,
    string Status,
    string? BankTransferReference,
    DateTimeOffset? ReportedAtUtc,
    long? ConfirmedByUserId,
    string? ConfirmedByStaffName,
    DateTimeOffset? ConfirmedAtUtc,
    string? ReceiptNumber,
    IReadOnlyList<StatementBookingRequestDto> Requests);

public sealed record ReportCommissionPaymentRequest(string BankTransferReference);

public sealed record ConfirmCommissionReceiptRequest(string? Notes);

public sealed record StatementGenerationSummaryDto(
    string Period,
    int GeneratedCount,
    int SkippedCount,
    decimal TotalCommissionAmount);

public sealed record OverdueProcessingSummaryDto(
    int MarkedOverdueCount,
    int AccountsLockedCount);
