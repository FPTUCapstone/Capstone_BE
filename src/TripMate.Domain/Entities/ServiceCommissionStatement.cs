using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class ServiceCommissionStatement : BaseEntity
{
    public const string StatusIssued = "Issued";
    public const string StatusPaymentReported = "PaymentReported";
    public const string StatusPaid = "Paid";
    public const string StatusOverdue = "Overdue";

    public const int StatementCodeMaxLength = 32;
    public const int PeriodMaxLength = 7;
    public const int BankTransferReferenceMaxLength = 100;
    public const int ReceiptNumberMaxLength = 50;
    public const int AutoLockGraceDays = 15;
    public const decimal DefaultCommissionRate = 10.00m;

    private ServiceCommissionStatement()
    {
    }

    public string StatementCode { get; private set; } = string.Empty;

    public long ProviderId { get; private set; }

    public ServiceProvider Provider { get; private set; } = null!;

    public string Period { get; private set; } = string.Empty;

    public int TotalRequests { get; private set; }

    public decimal TotalActualAmount { get; private set; }

    public decimal CommissionRate { get; private set; } = DefaultCommissionRate;

    public decimal CommissionAmount { get; private set; }

    public DateOnly IssueDate { get; private set; }

    public DateOnly DueDate { get; private set; }

    public string Status { get; private set; } = StatusIssued;

    public string? BankTransferReference { get; private set; }
    public string? VerifiedBankTransferReference { get; private set; }

    public DateTimeOffset? ReportedAtUtc { get; private set; }

    public long? ConfirmedByUserId { get; private set; }

    public User? ConfirmedByUser { get; private set; }

    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    public string? ReceiptNumber { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    public byte[] RowVersion { get; private set; } = [];

    public ICollection<ServiceBooking> Bookings { get; private set; } = new List<ServiceBooking>();

    public static ServiceCommissionStatement Create(
        string statementCode,
        long providerId,
        string period,
        int totalRequests,
        decimal totalActualAmount,
        decimal commissionAmount,
        DateOnly issueDate,
        DateOnly dueDate,
        decimal commissionRate = DefaultCommissionRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statementCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(period);
        if (providerId <= 0) throw new ArgumentOutOfRangeException(nameof(providerId));
        if (totalRequests <= 0) throw new ArgumentOutOfRangeException(nameof(totalRequests));
        if (totalActualAmount < 0) throw new ArgumentOutOfRangeException(nameof(totalActualAmount));
        if (commissionAmount < 0) throw new ArgumentOutOfRangeException(nameof(commissionAmount));
        if (dueDate < issueDate) throw new ArgumentException("Due date cannot precede issue date.", nameof(dueDate));

        return new ServiceCommissionStatement
        {
            StatementCode = statementCode.Trim(),
            ProviderId = providerId,
            Period = period.Trim(),
            TotalRequests = totalRequests,
            TotalActualAmount = totalActualAmount,
            CommissionAmount = commissionAmount,
            CommissionRate = commissionRate,
            IssueDate = issueDate,
            DueDate = dueDate,
            Status = StatusIssued,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public void ReportPayment(string bankTransferReference, DateTimeOffset reportedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankTransferReference);
        if (Status is not (StatusIssued or StatusOverdue))
        {
            throw new InvalidOperationException($"Cannot report payment when statement status is {Status}.");
        }

        var normalized = bankTransferReference.Trim();
        if (normalized.Length > BankTransferReferenceMaxLength)
        {
            throw new ArgumentException($"Reference cannot exceed {BankTransferReferenceMaxLength} characters.", nameof(bankTransferReference));
        }

        BankTransferReference = normalized;
        ReportedAtUtc = reportedAtUtc;
        Status = StatusPaymentReported;
    }

    public void ConfirmReceipt(long staffUserId, DateTimeOffset confirmedAtUtc, string receiptNumber,
        string? verifiedBankReference = null)
    {
        if (staffUserId <= 0) throw new ArgumentOutOfRangeException(nameof(staffUserId));
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptNumber);
        if (Status == StatusPaid)
        {
            throw new InvalidOperationException("Statement is already marked as Paid.");
        }

        if (!string.IsNullOrWhiteSpace(verifiedBankReference))
        {
            var normalizedReference = verifiedBankReference.Trim();
            if (normalizedReference.Length > BankTransferReferenceMaxLength)
            {
                throw new ArgumentException("The bank transfer reference is too long.", nameof(verifiedBankReference));
            }

            VerifiedBankTransferReference = normalizedReference.ToUpperInvariant();
        }

        Status = StatusPaid;
        ConfirmedByUserId = staffUserId;
        ConfirmedAtUtc = confirmedAtUtc;
        ReceiptNumber = receiptNumber.Trim();
    }

    public void MarkOverdue()
    {
        if (Status is StatusIssued or StatusPaymentReported)
        {
            Status = StatusOverdue;
        }
    }
}