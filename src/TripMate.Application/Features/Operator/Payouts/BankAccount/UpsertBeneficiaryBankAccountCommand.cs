using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Payouts.BankAccount;

public sealed record UpsertBeneficiaryBankAccountCommand(
    string BankName,
    string AccountHolderName,
    string AccountNumber) : IRequest<Result<BeneficiaryBankAccountDto>>;