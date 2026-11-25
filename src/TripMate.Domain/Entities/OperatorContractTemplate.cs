using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class OperatorContractTemplate : BaseEntity
{
    public const string OperatorPartnership = "OperatorPartnership";
    public const string Vietnamese = "vi";
    public const int TitleMaxLength = 200;
    public const int StorageKeyMaxLength = 500;
    public const int FileNameMaxLength = 255;
    public const int HashLength = 64;
    private OperatorContractTemplate() { }

    public string TemplateType { get; private set; } = OperatorPartnership;
    public string Language { get; private set; } = Vietnamese;
    public string Title { get; private set; } = string.Empty;
    public int? Version { get; private set; }
    public int DraftRevision { get; private set; }
    public ContractTemplateStatus Status { get; private set; }
    public string SourceStorageKey { get; private set; } = string.Empty;
    public string SourceSha256 { get; private set; } = string.Empty;
    public string SourceFileName { get; private set; } = string.Empty;
    public string VariablesManifestJson { get; private set; } = "[]";
    public bool ValidationValid { get; private set; }
    public ContractTemplateRenderState RenderState { get; private set; }
    public string? PreviewStorageKey { get; private set; }
    public string? PreviewSha256 { get; private set; }
    public int? PreviewRevision { get; private set; }
    public int? PreviewAcknowledgedRevision { get; private set; }
    public string? RenderErrorCode { get; private set; }
    public long CreatedByUserId { get; private set; }
    public long UpdatedByUserId { get; private set; }
    public long? PublishedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? PublishedAtUtc { get; private set; }
    public DateTimeOffset? ArchivedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public Guid? PublicationIdempotencyKey { get; private set; }
    public long? PublicationExpectedGeneration { get; private set; }
    public long? PublicationExpectedCurrentId { get; private set; }

    public static OperatorContractTemplate CreateDraft(string title, string sourceKey, string sourceSha256,
        string variablesJson, bool validationValid, long actorId, DateTimeOffset now, string sourceFileName = "template.docx")
    {
        Validate(title, sourceKey, sourceSha256, variablesJson, actorId, sourceFileName);
        return new OperatorContractTemplate
        {
            Title = title.Trim(),
            SourceStorageKey = sourceKey,
            SourceSha256 = sourceSha256.ToLowerInvariant(),
            SourceFileName = sourceFileName,
            VariablesManifestJson = variablesJson,
            ValidationValid = validationValid,
            DraftRevision = 1,
            Status = ContractTemplateStatus.Draft,
            RenderState = ContractTemplateRenderState.NotRequested,
            CreatedByUserId = actorId,
            UpdatedByUserId = actorId,
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime(),
        };
    }

    public bool TryUpdateDraft(int expectedRevision, string title, string sourceKey, string sourceSha256,
        string variablesJson, bool validationValid, long actorId, DateTimeOffset now, string sourceFileName = "template.docx")
    {
        if (Status != ContractTemplateStatus.Draft || DraftRevision != expectedRevision) return false;
        Validate(title, sourceKey, sourceSha256, variablesJson, actorId, sourceFileName);
        Title = title.Trim(); SourceStorageKey = sourceKey; SourceSha256 = sourceSha256.ToLowerInvariant();
        SourceFileName = sourceFileName; VariablesManifestJson = variablesJson; ValidationValid = validationValid;
        DraftRevision++; UpdatedByUserId = actorId; UpdatedAtUtc = now.ToUniversalTime();
        RenderState = ContractTemplateRenderState.NotRequested; PreviewStorageKey = null; PreviewSha256 = null;
        PreviewRevision = null; PreviewAcknowledgedRevision = null; RenderErrorCode = null;
        return true;
    }

    public bool RequestPreview(int expectedRevision)
    {
        if (Status != ContractTemplateStatus.Draft || DraftRevision != expectedRevision || !ValidationValid
            || RenderState == ContractTemplateRenderState.Processing) return false;
        RenderState = ContractTemplateRenderState.Processing; RenderErrorCode = null;
        PreviewStorageKey = null; PreviewSha256 = null; PreviewRevision = null; PreviewAcknowledgedRevision = null;
        return true;
    }

    public bool TryCompletePreview(int expectedRevision, string sourceSha256, string previewKey, string previewSha256, DateTimeOffset now)
    {
        if (Status != ContractTemplateStatus.Draft || DraftRevision != expectedRevision
            || RenderState != ContractTemplateRenderState.Processing || !string.Equals(SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)) return false;
        ValidateKey(previewKey); ValidateHash(previewSha256);
        PreviewStorageKey = previewKey; PreviewSha256 = previewSha256.ToLowerInvariant(); PreviewRevision = expectedRevision;
        RenderState = ContractTemplateRenderState.Ready; RenderErrorCode = null; UpdatedAtUtc = now.ToUniversalTime();
        return true;
    }

    public bool TryFailPreview(int expectedRevision, string errorCode)
    {
        if (Status != ContractTemplateStatus.Draft || DraftRevision != expectedRevision || RenderState != ContractTemplateRenderState.Processing) return false;
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (errorCode.Length > 100) throw new ArgumentOutOfRangeException(nameof(errorCode));
        RenderState = ContractTemplateRenderState.Failed; RenderErrorCode = errorCode;
        return true;
    }

    public bool TryPublish(int expectedRevision, int version, long actorId, DateTimeOffset now,
        Guid? idempotencyKey = null, long? expectedGeneration = null, long? expectedCurrentId = null)
    {
        if (Status != ContractTemplateStatus.Draft || DraftRevision != expectedRevision || !ValidationValid
            || RenderState != ContractTemplateRenderState.Ready || PreviewRevision != expectedRevision
            || PreviewStorageKey is null || PreviewSha256 is null) return false;
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        if (actorId <= 0) throw new ArgumentOutOfRangeException(nameof(actorId));
        PreviewAcknowledgedRevision = expectedRevision;
        PublicationIdempotencyKey = idempotencyKey;
        PublicationExpectedGeneration = expectedGeneration;
        PublicationExpectedCurrentId = expectedCurrentId;
        Version = version; Status = ContractTemplateStatus.Published; PublishedByUserId = actorId;
        PublishedAtUtc = now.ToUniversalTime(); UpdatedByUserId = actorId; UpdatedAtUtc = now.ToUniversalTime();
        return true;
    }

    public bool TryArchive(DateTimeOffset now)
    {
        if (Status != ContractTemplateStatus.Published) return false;
        Status = ContractTemplateStatus.Archived; ArchivedAtUtc = now.ToUniversalTime();
        return true;
    }

    private static void Validate(string title, string key, string hash, string variables, long actor, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Trim().Length > TitleMaxLength) throw new ArgumentOutOfRangeException(nameof(title));
        ValidateKey(key); ValidateHash(hash); ArgumentException.ThrowIfNullOrWhiteSpace(variables);
        if (actor <= 0) throw new ArgumentOutOfRangeException(nameof(actor));
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Length > FileNameMaxLength) throw new ArgumentOutOfRangeException(nameof(fileName));
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > StorageKeyMaxLength) throw new ArgumentOutOfRangeException(nameof(key));
    }

    private static void ValidateHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length != HashLength || hash.Any(character => !Uri.IsHexDigit(character))) throw new ArgumentException("Invalid SHA-256.", nameof(hash));
    }
}