using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.RegisterOperator;

namespace TripMate.Application.Features.Operator.Application.Resubmit;

public sealed record ResubmitOperatorApplicationCommand(
    string CompanyName,
    string BusinessLicenseNo,
    string TaxCode,
    string ContactPerson,
    string? BusinessAddress,
    string? ContactPhone,
    OperatorRegistrationDocument? BusinessLicenseDocument,
    IReadOnlyList<OperatorRegistrationDocument>? SupportingDocuments)
    : IRequest<Result<ResubmitOperatorApplicationResponse>>;

public sealed record ResubmitOperatorApplicationResponse(
    long UserId,
    string UserStatus,
    string ApprovalStatus,
    DateTimeOffset UpdatedAtUtc,
    string MessageCode,
    string Message,
    int ResubmissionCount);