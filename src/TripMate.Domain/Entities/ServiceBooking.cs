using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Read model for commercial.ServiceBookings. UC-63 uses the persisted commission amount as the
/// platform-recognized amount for a fulfilled partner service booking.
/// </summary>
public sealed class ServiceBooking : BaseEntity
{
    public const string PendingConfirmation = "PendingConfirmation";
    public const string Confirmed = "Confirmed";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string Completed = "Completed";
    public const string NoShow = "NoShow";
    public const string Cancelled = "Cancelled";
    public const int RejectionReasonMaxLength = 1000;
    public const int ServiceOptionMaxLength = 200;
    public const int SpecialRequestMaxLength = 1000;
    public const decimal MaximumAmount = 9999999999.99m;
    public static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public long TravelerUserId { get; private set; }
    public long ServiceId { get; private set; }
    public CommercialService Service { get; private set; } = null!;
    public DateTimeOffset StartAtUtc { get; private set; }
    public DateTimeOffset? EndAtUtc { get; private set; }
    public int Quantity { get; private set; }
    public decimal TotalPrice { get; private set; }
    public decimal? CommissionAmount { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? RequestSubmittedAtUtc { get; private set; }
    public decimal? ActualAmount { get; private set; }
    public string? RejectionReason { get; private set; }
    public long? RespondedByUserId { get; private set; }
    public DateTimeOffset? RespondedAtUtc { get; private set; }
    public long? OutcomeByUserId { get; private set; }
    public DateTimeOffset? OutcomeAtUtc { get; private set; }
    public bool IsAutoCompleted { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public long? ItineraryId { get; private set; }
    public string? ServiceOption { get; private set; }
    public string? SpecialRequest { get; private set; }
    public Guid? SubmitKey { get; private set; }
    public string? SubmitHash { get; private set; }
    public string? ContactName { get; private set; }
    public string? ContactPhone { get; private set; }
    public string? ContactEmail { get; private set; }
    public long? CommissionStatementId { get; private set; }
    public ServiceCommissionStatement? CommissionStatement { get; private set; }

    public DateOnly RequestedDate => DateOnly.FromDateTime(StartAtUtc.ToOffset(VietnamOffset).DateTime);
    public DateTimeOffset? ResponseDeadlineUtc => RequestSubmittedAtUtc?.AddHours(24);
    public DateTimeOffset OutcomeAvailableAtUtc => VietnamMidnight(RequestedDate.AddDays(1));
    public DateTimeOffset AutoCompleteAtUtc => VietnamMidnight(RequestedDate.AddDays(4));

    public static ServiceBooking CreatePendingRequest(long travelerUserId, long serviceId,
        DateTimeOffset startAtUtc, DateTimeOffset? endAtUtc, int quantity,
        decimal estimatedAmount, DateTimeOffset submittedAtUtc, long? itineraryId = null,
        string? serviceOption = null, string? specialRequest = null)
    {
        if (travelerUserId <= 0 || serviceId <= 0 || quantity <= 0
            || estimatedAmount < 0 || estimatedAmount > MaximumAmount
            || decimal.Round(estimatedAmount, 2) != estimatedAmount || endAtUtc < startAtUtc
            || itineraryId <= 0 || serviceOption?.Length > ServiceOptionMaxLength
            || specialRequest?.Length > SpecialRequestMaxLength)
            throw new ArgumentException("The service booking request is invalid.");

        return new ServiceBooking
        {
            TravelerUserId = travelerUserId,
            ServiceId = serviceId,
            StartAtUtc = startAtUtc.ToUniversalTime(),
            EndAtUtc = endAtUtc?.ToUniversalTime(),
            Quantity = quantity,
            TotalPrice = estimatedAmount,
            CommissionAmount = 0,
            Status = PendingConfirmation,
            CreatedAtUtc = submittedAtUtc.ToUniversalTime(),
            RequestSubmittedAtUtc = submittedAtUtc.ToUniversalTime(),
            ItineraryId = itineraryId,
            ServiceOption = string.IsNullOrWhiteSpace(serviceOption) ? null : serviceOption.Trim(),
            SpecialRequest = string.IsNullOrWhiteSpace(specialRequest) ? null : specialRequest.Trim()
        };
    }

    public void SetSubmission(Guid key, string hash, string contactName, string contactPhone, string contactEmail)
    {
        if (SubmitKey.HasValue || key == Guid.Empty || hash.Length != 64
            || string.IsNullOrWhiteSpace(contactName) || contactName.Length > 150
            || string.IsNullOrWhiteSpace(contactPhone) || contactPhone.Length > 20
            || string.IsNullOrWhiteSpace(contactEmail) || contactEmail.Length > 256)
            throw new ArgumentException("Invalid booking submission details.");

        SubmitKey = key;
        SubmitHash = hash;
        ContactName = contactName;
        ContactPhone = contactPhone;
        ContactEmail = contactEmail;
    }

    public bool TryDecide(string decision, string? reason, long actorUserId, DateTimeOffset nowUtc)
    {
        if (RequestSubmittedAtUtc is null || Status != PendingConfirmation || actorUserId <= 0
            || nowUtc < RequestSubmittedAtUtc || nowUtc >= ResponseDeadlineUtc
            || decision is not (Confirmed or Rejected)) return false;
        if (decision == Rejected && (string.IsNullOrWhiteSpace(reason)
            || reason.Trim().Length > RejectionReasonMaxLength)) return false;

        Status = decision;
        RejectionReason = decision == Rejected ? reason!.Trim() : null;
        RespondedByUserId = actorUserId;
        RespondedAtUtc = nowUtc.ToUniversalTime();
        CommissionAmount = 0;
        return true;
    }

    public bool TryCancel(DateTimeOffset nowUtc)
    {
        if (RequestSubmittedAtUtc is null || Status != PendingConfirmation
            || nowUtc < RequestSubmittedAtUtc || nowUtc >= ResponseDeadlineUtc)
            return false;

        Status = Cancelled;
        CommissionAmount = 0;
        return true;
    }

    public bool TryExpire(DateTimeOffset nowUtc)
    {
        if (RequestSubmittedAtUtc is null || Status != PendingConfirmation || nowUtc < ResponseDeadlineUtc)
            return false;
        Status = Expired;
        CommissionAmount = 0;
        return true;
    }

    public bool TryRecordOutcome(string outcome, decimal? actualAmount, long actorUserId, DateTimeOffset nowUtc)
    {
        if (RequestSubmittedAtUtc is null || Status != Confirmed || actorUserId <= 0
            || CommissionStatementId is not null
            || nowUtc < OutcomeAvailableAtUtc || nowUtc >= AutoCompleteAtUtc
            || outcome is not (Completed or NoShow)) return false;
        if (outcome == Completed && (actualAmount is null || actualAmount <= 0
            || actualAmount > MaximumAmount || decimal.Round(actualAmount.Value, 2) != actualAmount)) return false;
        if (outcome == NoShow && actualAmount is not (null or 0)) return false;

        SetOutcome(outcome, outcome == NoShow ? 0 : actualAmount!.Value, actorUserId, nowUtc, false);
        return true;
    }

    public void AssignCommissionStatement(long statementId)
    {
        if (statementId <= 0) throw new ArgumentOutOfRangeException(nameof(statementId));
        if (Status != Completed) throw new InvalidOperationException("Only completed service bookings can be assigned to a commission statement.");
        if (CommissionStatementId is not null && CommissionStatementId != statementId)
        {
            throw new InvalidOperationException("Service booking is already assigned to another commission statement.");
        }

        CommissionStatementId = statementId;
    }

    public bool TryDefaultComplete(DateTimeOffset nowUtc)
    {
        if (RequestSubmittedAtUtc is null || Status != Confirmed || nowUtc < AutoCompleteAtUtc) return false;
        SetOutcome(Completed, TotalPrice, null, nowUtc, true);
        return true;
    }

    private void SetOutcome(string outcome, decimal actualAmount, long? actorUserId,
        DateTimeOffset nowUtc, bool automatic)
    {
        Status = outcome;
        ActualAmount = actualAmount;
        CommissionAmount = outcome == NoShow ? 0 : decimal.Floor(actualAmount * 0.10m);
        OutcomeByUserId = actorUserId;
        OutcomeAtUtc = nowUtc.ToUniversalTime();
        IsAutoCompleted = automatic;
    }

    private static DateTimeOffset VietnamMidnight(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), VietnamOffset).ToUniversalTime();
}