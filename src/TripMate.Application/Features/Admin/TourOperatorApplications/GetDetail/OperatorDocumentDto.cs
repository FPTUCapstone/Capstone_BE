using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.GetDetail;

public record OperatorDocumentDto(
    long DocumentId,
    OperatorDocumentType DocumentType,
    string FileUrl,
    DocumentStatus Status,
    DateTimeOffset UploadedAt);
