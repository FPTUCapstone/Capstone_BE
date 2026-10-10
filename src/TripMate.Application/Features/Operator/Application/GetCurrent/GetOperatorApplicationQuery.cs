using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Application.GetCurrent;

public sealed record GetOperatorApplicationQuery : IRequest<Result<OperatorApplicationDto>>;

public sealed record OperatorApplicationDocumentDto(
    long DocumentId,
    string DocumentType,
    string Status,
    DateTimeOffset UploadedAtUtc,
    string? DownloadUrl,
    DateTimeOffset? DownloadUrlExpiresAtUtc);

public sealed record OperatorApplicationDto(
    long UserId,
    string UserStatus,
    string ApprovalStatus,
    string CompanyName,
    string BusinessLicenseNo,
    string TaxCode,
    string? BusinessAddress,
    string ContactPerson,
    string? ContactPhone,
    string? RejectionReason,
    DateTimeOffset? ReviewedAtUtc,
    int ResubmissionCount,
    IReadOnlyList<OperatorApplicationDocumentDto> Documents);