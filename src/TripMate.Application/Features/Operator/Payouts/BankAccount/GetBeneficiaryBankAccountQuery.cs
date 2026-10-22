using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Payouts.BankAccount;

public sealed record GetBeneficiaryBankAccountQuery : IRequest<Result<BeneficiaryBankAccountDto>>;

public sealed record BeneficiaryBankAccountDto(
    string? BankName,
    string? AccountHolderName,
    string? MaskedAccountNumber,
    DateTimeOffset? UpdatedAtUtc);
