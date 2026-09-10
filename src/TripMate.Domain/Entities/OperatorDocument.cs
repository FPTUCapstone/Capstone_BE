using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.OperatorDocuments in database/tripmate_schema_v7.sql.
/// </summary>
public class OperatorDocument : BaseEntity
{
    public long OperatorUserId { get; set; }

    public OperatorProfile OperatorProfile { get; set; } = null!;

    public OperatorDocumentType DocumentType { get; set; }

    public string FileUrl { get; set; } = string.Empty;

    public DocumentStatus Status { get; set; } = DocumentStatus.Submitted;

    public DateTimeOffset UploadedAtUtc { get; set; }
}
