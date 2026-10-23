using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.RegisterOperator;

public sealed record OperatorRegistrationDocument(
    string FileName,
    string ContentType,
    byte[] Bytes);

public sealed record RegisterOperatorCommand(
    string FirebaseIdToken,
    string Email,
    string Password,
    string ConfirmPassword,
    string CompanyName,
    string BusinessLicenseNo,
    string TaxCode,
    string ContactPerson,
    string? BusinessAddress,
    string? ContactPhone,
    OperatorRegistrationDocument? BusinessLicenseDocument,
    IReadOnlyList<OperatorRegistrationDocument>? SupportingDocuments,
    bool AcceptTerms)
    : IRequest<Result<RegisterOperatorResponse>>;