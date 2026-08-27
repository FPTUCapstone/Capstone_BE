namespace TripMate.Domain.Common;

public abstract class BaseAuditableEntity : BaseEntity
{
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? LastModifiedAtUtc { get; set; }

    public Guid? LastModifiedBy { get; set; }
}
