using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>One optimistic publication head, including the initially empty state.</summary>
public sealed class ContractTemplatePublicationHead : BaseEntity
{
    private ContractTemplatePublicationHead() { }
    public string TemplateType { get; private set; } = OperatorContractTemplate.OperatorPartnership;
    public string Language { get; private set; } = OperatorContractTemplate.Vietnamese;
    public long? CurrentPublishedTemplateId { get; private set; }
    public int CurrentVersion { get; private set; }
    public long Generation { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static ContractTemplatePublicationHead Create() => new() { Id = 1 };

    public bool TryAdvance(long expectedGeneration, long? expectedCurrentId, long newTemplateId)
    {
        if (Generation != expectedGeneration || CurrentPublishedTemplateId != expectedCurrentId) return false;
        if (newTemplateId <= 0) throw new ArgumentOutOfRangeException(nameof(newTemplateId));
        if (newTemplateId == CurrentPublishedTemplateId) return false;
        CurrentPublishedTemplateId = newTemplateId;
        CurrentVersion = checked(CurrentVersion + 1);
        Generation = checked(Generation + 1);
        return true;
    }
}