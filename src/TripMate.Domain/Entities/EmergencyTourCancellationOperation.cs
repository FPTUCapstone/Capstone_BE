using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class EmergencyTourCancellationOperation : BaseEntity
{
    public const string CancelBeforeDepartureAction = "CancelBeforeDeparture";
    public const string EmergencyStopActiveAction = "EmergencyStopActive";
    public const int ActionMaxLength = 24;
    public const int ReasonCategoryMaxLength = 40;
    public const int ReasonDetailsMaxLength = 1000;
    public const int InstructionsMaxLength = 1000;
    public const int ContactMaxLength = 100;
    public const int EvidenceMaxLength = 500;
    public const int EvidenceUrlMaxLength = 1000;
    public const int AuthorityReferenceMaxLength = 200;
    public const int EvidenceProvenanceMaxLength = 20;
    public const int HashMaxLength = 64;

    public const string DefaultProvenance = EmergencyEvidenceProvenance.OperatorProvided;

    private EmergencyTourCancellationOperation() { }

    public long TourScheduleId { get; private set; }
    public TourSchedule TourSchedule { get; private set; } = null!;
    public long ActorUserId { get; private set; }
    public Guid CommandId { get; private set; }
    public string RequestHash { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string ReasonCategory { get; private set; } = string.Empty;
    public string ReasonDetails { get; private set; } = string.Empty;
    public string? ImmediateInstructions { get; private set; }
    public string? EmergencyContact { get; private set; }
    public string? EvidenceReference { get; private set; }
    public string? EvidenceUrl { get; private set; }
    public string? AuthorityReference { get; private set; }
    public string EvidenceProvenance { get; private set; } = DefaultProvenance;
    public bool EvidenceAddedLater { get; private set; }
    public int AffectedBookingCount { get; private set; }
    public DateTimeOffset CommittedAtUtc { get; private set; }

    public static EmergencyTourCancellationOperation Create(
        long tourScheduleId, long actorUserId, Guid commandId, string requestHash,
        string action, string reasonCategory, string reasonDetails,
        string? immediateInstructions, string? emergencyContact, string? evidenceReference,
        int affectedBookingCount,
        DateTimeOffset committedAtUtc,
        string? evidenceUrl = null,
        string? authorityReference = null,
        string? evidenceProvenance = null,
        bool evidenceAddedLater = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tourScheduleId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorUserId);
        if (commandId == Guid.Empty) throw new ArgumentException("A command id is required.", nameof(commandId));
        ValidateRequired(requestHash, HashMaxLength, nameof(requestHash));
        ValidateRequired(action, ActionMaxLength, nameof(action));
        if (action is not (CancelBeforeDepartureAction or EmergencyStopActiveAction))
            throw new ArgumentOutOfRangeException(nameof(action));
        ValidateRequired(reasonCategory, ReasonCategoryMaxLength, nameof(reasonCategory));
        ValidateRequired(reasonDetails, ReasonDetailsMaxLength, nameof(reasonDetails));
        if (action == EmergencyStopActiveAction)
            ValidateRequired(emergencyContact ?? string.Empty, ContactMaxLength, nameof(emergencyContact));
        else
            ValidateOptional(emergencyContact, ContactMaxLength, nameof(emergencyContact));
        ValidateOptional(immediateInstructions, InstructionsMaxLength, nameof(immediateInstructions));
        ValidateOptional(evidenceReference, EvidenceMaxLength, nameof(evidenceReference));
        ValidateOptional(evidenceUrl, EvidenceUrlMaxLength, nameof(evidenceUrl));
        ValidateOptional(authorityReference, AuthorityReferenceMaxLength, nameof(authorityReference));
        ValidateOptional(evidenceProvenance, EvidenceProvenanceMaxLength, nameof(evidenceProvenance));
        if (affectedBookingCount < 0)
            throw new ArgumentOutOfRangeException(nameof(affectedBookingCount));

        return new EmergencyTourCancellationOperation
        {
            TourScheduleId = tourScheduleId,
            ActorUserId = actorUserId,
            CommandId = commandId,
            RequestHash = requestHash.Trim(),
            Action = action.Trim(),
            ReasonCategory = reasonCategory.Trim(),
            ReasonDetails = reasonDetails.Trim(),
            ImmediateInstructions = Normalize(immediateInstructions),
            EmergencyContact = Normalize(emergencyContact),
            EvidenceReference = Normalize(evidenceReference),
            EvidenceUrl = Normalize(evidenceUrl),
            AuthorityReference = Normalize(authorityReference),
            EvidenceProvenance = Normalize(evidenceProvenance) ?? DefaultProvenance,
            EvidenceAddedLater = evidenceAddedLater,
            AffectedBookingCount = affectedBookingCount,
            CommittedAtUtc = committedAtUtc.ToUniversalTime(),
        };
    }

    public bool AddOperatorEvidence(
        string? evidenceReference,
        string? evidenceUrl,
        string? authorityReference)
    {
        string? normalizedReference = Normalize(evidenceReference);
        string? normalizedUrl = Normalize(evidenceUrl);
        string? normalizedAuthority = Normalize(authorityReference);
        if (normalizedReference is null && normalizedUrl is null && normalizedAuthority is null)
            throw new ArgumentException("At least one evidence value is required.");
        ValidateOptional(normalizedReference, EvidenceMaxLength, nameof(evidenceReference));
        ValidateOptional(normalizedUrl, EvidenceUrlMaxLength, nameof(evidenceUrl));
        ValidateOptional(normalizedAuthority, AuthorityReferenceMaxLength, nameof(authorityReference));

        bool same = EvidenceReference == normalizedReference
            && EvidenceUrl == normalizedUrl
            && AuthorityReference == normalizedAuthority
            && !EvidenceAddedLater;
        if (same) return false;

        EvidenceReference = normalizedReference;
        EvidenceUrl = normalizedUrl;
        AuthorityReference = normalizedAuthority;
        EvidenceProvenance = DefaultProvenance;
        EvidenceAddedLater = false;
        return true;
    }

    public bool WithdrawOperatorEvidence()
    {
        bool same = EvidenceReference is null
            && EvidenceUrl is null
            && AuthorityReference is null
            && !EvidenceAddedLater;
        if (same) return false;

        EvidenceReference = null;
        EvidenceUrl = null;
        AuthorityReference = null;
        EvidenceProvenance = DefaultProvenance;
        EvidenceAddedLater = false;
        return true;
    }

    private static void ValidateRequired(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Trim().Length > maxLength) throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateOptional(string? value, int maxLength, string parameterName)
    {
        if (value?.Trim().Length > maxLength) throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}