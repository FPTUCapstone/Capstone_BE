using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class ContractTemplateRenderJob : BaseEntity
{
    private ContractTemplateRenderJob() { }
    public long TemplateId { get; private set; }
    public OperatorContractTemplate Template { get; private set; } = null!;
    public int Revision { get; private set; }
    public string SourceSha256 { get; private set; } = string.Empty;
    public ContractTemplateRenderJobStatus Status { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public Guid? ClaimToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public int AttemptCount { get; private set; }
    public string? FailureCode { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static ContractTemplateRenderJob Create(OperatorContractTemplate template, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new ContractTemplateRenderJob
        {
            Template = template,
            TemplateId = template.Id,
            Revision = template.DraftRevision,
            SourceSha256 = template.SourceSha256,
            Status = ContractTemplateRenderJobStatus.Queued,
            RequestedAtUtc = now.ToUniversalTime(),
        };
    }

    public bool TryClaim(Guid token, DateTimeOffset now, TimeSpan lease)
    {
        if (Status is ContractTemplateRenderJobStatus.Completed or ContractTemplateRenderJobStatus.Failed
            || (Status == ContractTemplateRenderJobStatus.Processing && LeaseExpiresAtUtc > now)) return false;
        if (token == Guid.Empty) throw new ArgumentException("Claim token required.", nameof(token));
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        Status = ContractTemplateRenderJobStatus.Processing; ClaimToken = token;
        LeaseExpiresAtUtc = now.ToUniversalTime().Add(lease); AttemptCount++;
        return true;
    }

    public bool TryFinish(Guid token, bool success, string? failureCode, DateTimeOffset now)
    {
        if (Status != ContractTemplateRenderJobStatus.Processing || ClaimToken != token || LeaseExpiresAtUtc <= now) return false;
        if (!success && (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 100)) throw new ArgumentException("Failure code required.", nameof(failureCode));
        Status = success ? ContractTemplateRenderJobStatus.Completed : ContractTemplateRenderJobStatus.Failed;
        FailureCode = success ? null : failureCode; CompletedAtUtc = now.ToUniversalTime();
        ClaimToken = null; LeaseExpiresAtUtc = null;
        return true;
    }

    public bool TryRequeue()
    {
        if (Status is not (ContractTemplateRenderJobStatus.Completed or ContractTemplateRenderJobStatus.Failed)) return false;
        Status = ContractTemplateRenderJobStatus.Queued;
        CompletedAtUtc = null; FailureCode = null;
        return true;
    }

    public bool TryFailExhausted(DateTimeOffset now, int maxAttempts, string failureCode)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (Status != ContractTemplateRenderJobStatus.Processing || AttemptCount < maxAttempts
            || LeaseExpiresAtUtc is null || LeaseExpiresAtUtc > now) return false;
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > 100) throw new ArgumentOutOfRangeException(nameof(failureCode));
        Status = ContractTemplateRenderJobStatus.Failed;
        FailureCode = failureCode;
        CompletedAtUtc = now.ToUniversalTime();
        ClaimToken = null;
        LeaseExpiresAtUtc = null;
        return true;
    }
}