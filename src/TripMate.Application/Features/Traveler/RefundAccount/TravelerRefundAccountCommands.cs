using System.Security.Cryptography;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.RefundAccount;

public sealed record TravelerRefundAccountResponse(string BankCode, string AccountNumber, string AccountHolder);
public sealed record GetTravelerRefundAccountQuery : IRequest<Result<TravelerRefundAccountResponse?>>;
public sealed record SaveTravelerRefundAccountCommand(string BankCode, string AccountNumber, string AccountHolder)
    : IRequest<Result<TravelerRefundAccountResponse>>;

public static class TravelerRefundAccountStore
{
    public static bool IsValid(string? bankCode, string? accountNumber, string? holder) =>
        !string.IsNullOrWhiteSpace(bankCode) && bankCode.Trim().Length <= DirectRefundCase.RecipientBankCodeMaxLength &&
        !string.IsNullOrWhiteSpace(accountNumber) && accountNumber.Trim().Length <= DirectRefundCase.RecipientAccountNumberMaxLength &&
        !string.IsNullOrWhiteSpace(holder) && holder.Trim().Length <= DirectRefundCase.RecipientAccountHolderMaxLength;

    public static async Task SaveAsync(IApplicationDbContext db, IPiiProtector protector, long travelerId,
        string bankCode, string accountNumber, string holder, DateTimeOffset now, CancellationToken token)
    {
        var saved = await db.TravelerRefundAccounts.SingleOrDefaultAsync(item => item.Id == travelerId, token);
        var protectedNumber = protector.Protect(accountNumber.Trim());
        var protectedHolder = protector.Protect(holder.Trim());
        if (saved is null) db.TravelerRefundAccounts.Add(TravelerRefundAccount.Create(travelerId, bankCode.Trim(), protectedNumber, protectedHolder, now));
        else saved.Update(bankCode.Trim(), protectedNumber, protectedHolder, now);
    }

    public static TravelerRefundAccountResponse Read(TravelerRefundAccount saved, IPiiProtector protector) =>
        new(saved.BankCode, protector.Unprotect(saved.AccountNumberProtected), protector.Unprotect(saved.AccountHolderProtected));
}

public sealed class GetTravelerRefundAccountQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser, IPiiProtector protector)
    : IRequestHandler<GetTravelerRefundAccountQuery, Result<TravelerRefundAccountResponse?>>
{
    public async Task<Result<TravelerRefundAccountResponse?>> Handle(GetTravelerRefundAccountQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not > 0 || currentUser.Role != nameof(UserRole.Traveler))
            return Result.Failure<TravelerRefundAccountResponse?>(DirectRefundErrorCodes.AccessDenied, "Traveler access is required.");
        var saved = await db.TravelerRefundAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == currentUser.UserId, cancellationToken);
        try { return Result.Success<TravelerRefundAccountResponse?>(saved is null ? null : TravelerRefundAccountStore.Read(saved, protector)); }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return Result.Failure<TravelerRefundAccountResponse?>(DirectRefundErrorCodes.StorageUnavailable, "The saved recipient could not be read.");
        }
    }
}

public sealed class SaveTravelerRefundAccountCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser, IDateTimeProvider clock, IPiiProtector protector)
    : IRequestHandler<SaveTravelerRefundAccountCommand, Result<TravelerRefundAccountResponse>>
{
    public async Task<Result<TravelerRefundAccountResponse>> Handle(SaveTravelerRefundAccountCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || travelerId <= 0 || currentUser.Role != nameof(UserRole.Traveler))
            return Result.Failure<TravelerRefundAccountResponse>(DirectRefundErrorCodes.AccessDenied, "Traveler access is required.");
        if (!TravelerRefundAccountStore.IsValid(request.BankCode, request.AccountNumber, request.AccountHolder))
            return Result.Failure<TravelerRefundAccountResponse>(DirectRefundErrorCodes.ValidationFailed, "Bank, account number and account holder are required and must fit the supported lengths.");
        return await db.ExecuteInSerializableTransactionAsync(async token =>
        {
            await TravelerRefundAccountStore.SaveAsync(db, protector, travelerId, request.BankCode, request.AccountNumber, request.AccountHolder, clock.UtcNow, token);
            var awaitingRecipients = await db.DirectRefundCases
                .Where(item => item.Status == DirectRefundCase.StatusAwaitingRecipient
                    && db.Bookings.Any(booking => booking.Id == item.BookingId && booking.TravelerUserId == travelerId))
                .ToListAsync(token);
            foreach (var directCase in awaitingRecipients)
                directCase.ConfirmRecipient(request.BankCode.Trim(), request.AccountNumber.Trim(), request.AccountHolder.Trim(), clock.UtcNow);
            await db.SaveChangesAsync(token);
            return Result.Success(new TravelerRefundAccountResponse(request.BankCode.Trim(), request.AccountNumber.Trim(), request.AccountHolder.Trim()));
        }, cancellationToken);
    }
}