using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Payouts.BankAccount;

public sealed class GetBeneficiaryBankAccountQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetBeneficiaryBankAccountQuery, Result<BeneficiaryBankAccountDto>>
{
    public async Task<Result<BeneficiaryBankAccountDto>> Handle(GetBeneficiaryBankAccountQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<BeneficiaryBankAccountDto>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        var profile = await db.OperatorProfiles.AsNoTracking()
            .Where(p => p.UserId == operatorId && p.ApprovalStatus == OperatorApprovalStatus.Approved && p.User.Status == AccountStatus.Active)
            .Select(p => new { p.BankName, p.AccountHolderName, p.AccountNumberMasked, p.BankAccountUpdatedAtUtc })
            .FirstOrDefaultAsync(ct);

        if (profile is null)
        {
            return Result.Failure<BeneficiaryBankAccountDto>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        // The full account number is never read or returned here; only the masked value stored
        // alongside the protected one is projected (UC-46 FE spec: no full number to the browser).
        return Result.Success(new BeneficiaryBankAccountDto(
            profile.BankName, profile.AccountHolderName, profile.AccountNumberMasked, profile.BankAccountUpdatedAtUtc));
    }
}