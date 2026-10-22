using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Payouts.BankAccount;

public sealed class UpsertBeneficiaryBankAccountCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IPiiProtector piiProtector)
    : IRequestHandler<UpsertBeneficiaryBankAccountCommand, Result<BeneficiaryBankAccountDto>>
{
    public async Task<Result<BeneficiaryBankAccountDto>> Handle(UpsertBeneficiaryBankAccountCommand request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<BeneficiaryBankAccountDto>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        if (string.IsNullOrWhiteSpace(request.BankName) || string.IsNullOrWhiteSpace(request.AccountHolderName)
            || string.IsNullOrWhiteSpace(request.AccountNumber))
        {
            return Result.Failure<BeneficiaryBankAccountDto>(
                OperatorPayoutErrorCodes.ValidationRequiredField, "Bank name, account holder name, and account number are required.");
        }

        var profile = await db.OperatorProfiles.FirstOrDefaultAsync(p => p.UserId == operatorId
            && p.ApprovalStatus == OperatorApprovalStatus.Approved && p.User.Status == AccountStatus.Active, ct);
        if (profile is null)
        {
            return Result.Failure<BeneficiaryBankAccountDto>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        var maskedAccountNumber = BankAccountMasking.Mask(request.AccountNumber);
        var protectedAccountNumber = piiProtector.Protect(request.AccountNumber);
        var nowUtc = clock.UtcNow;

        profile.UpdateBeneficiaryBankAccount(
            request.BankName, request.AccountHolderName, maskedAccountNumber, protectedAccountNumber, nowUtc);

        await db.SaveChangesAsync(ct);

        return Result.Success(new BeneficiaryBankAccountDto(
            profile.BankName, profile.AccountHolderName, profile.AccountNumberMasked, profile.BankAccountUpdatedAtUtc));
    }
}