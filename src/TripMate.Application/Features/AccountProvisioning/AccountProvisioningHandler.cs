using System.Security.Cryptography;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.AccountProvisioning;

public sealed class AccountProvisioningHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IPasswordHasherService passwordHasher,
    IAccountProvisioningEmailSender emailSender,
    IAccountProvisioningLock provisioningLock,
    IAccountProvisioningPersistenceErrors persistenceErrors,
    IDateTimeProvider clock,
    ILogger<AccountProvisioningHandler> logger)
    : IRequestHandler<CreateStaffAccountCommand, Result<ProvisionedAccountDto>>,
      IRequestHandler<CreateServiceProviderAccountCommand, Result<ProvisionedAccountDto>>,
      IRequestHandler<GetAvailableProviderPoisQuery, Result<IReadOnlyList<AvailableProviderPoiDto>>>
{
    public const string VehicleRentalCategory = "Vehicle Rental";
    public static readonly string[] CommercialPoiCategories =
        [CommercialService.CategoryHotel, VehicleRentalCategory, CommercialService.CategoryRestaurant];
    public const string StaffCreatedAction = "Account.StaffCreated";
    public const string ProviderCreatedAction = "Account.ServiceProviderCreated";
    public const string AccountEntity = "User";

    public Task<Result<ProvisionedAccountDto>> Handle(CreateStaffAccountCommand request, CancellationToken cancellationToken) =>
        CreateAsync(request.FullName, request.Email, request.PhoneNumber, null, null, [],
            UserRole.Staff, UserRole.Administrator, cancellationToken);

    public Task<Result<ProvisionedAccountDto>> Handle(CreateServiceProviderAccountCommand request, CancellationToken cancellationToken) =>
        CreateAsync(request.FullName, request.Email, request.PhoneNumber, request.BusinessName, request.Category,
            request.LinkedPoiIds, UserRole.ServiceProvider, UserRole.Staff, cancellationToken);

    public async Task<Result<IReadOnlyList<AvailableProviderPoiDto>>> Handle(GetAvailableProviderPoisQuery request,
        CancellationToken cancellationToken)
    {
        if (!await CanProvisionAsync(UserRole.Staff, cancellationToken))
        {
            return Result.Failure<IReadOnlyList<AvailableProviderPoiDto>>(ProvisioningErrors.Forbidden, "Staff access is required.");
        }

        var category = NormalizeCategory(request.Category);
        if (request.Category is not null && category is null)
        {
            return Result.Failure<IReadOnlyList<AvailableProviderPoiDto>>(ProvisioningErrors.Invalid, "Category is invalid.");
        }

        var catalogCategory = category == CommercialService.CategoryVehicle ? VehicleRentalCategory : category;
        var rows = await db.PointsOfInterest.AsNoTracking()
            .Where(poi => poi.Status == PointOfInterestStatus.Active
                && CommercialPoiCategories.Contains(poi.Category.Name)
                && (catalogCategory == null || poi.Category.Name == catalogCategory)
                && !db.ServiceProviders.SelectMany(provider => provider.PoiLinks)
                    .Any(link => link.PointOfInterestId == poi.Id))
            .OrderBy(poi => poi.Name).ThenBy(poi => poi.Id)
            .Select(poi => new AvailableProviderPoiDto(poi.Id, poi.Name, poi.Category.Name))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<AvailableProviderPoiDto>>(rows);
    }

    private async Task<Result<ProvisionedAccountDto>> CreateAsync(string fullName, string email, string phone,
        string? businessName, string? category, IReadOnlyList<long>? poiIds, UserRole role, UserRole actorRole,
        CancellationToken cancellationToken)
    {
        if (!await CanProvisionAsync(actorRole, cancellationToken))
        {
            return Failure(ProvisioningErrors.Forbidden, "The acting account cannot provision this role.");
        }

        var validation = role == UserRole.Staff
            ? new CreateStaffAccountValidator().Validate(new CreateStaffAccountCommand(fullName, email, phone))
            : new CreateServiceProviderAccountValidator().Validate(new CreateServiceProviderAccountCommand(fullName, email, phone,
                businessName!, category!, poiIds!));
        if (!validation.IsValid)
        {
            var error = validation.Errors[0];
            return Failure(error.ErrorCode, error.ErrorMessage);
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedPhone = phone.Trim();
        var selectedIds = (poiIds ?? []).Distinct().Order().ToArray();
        var normalizedCategory = NormalizeCategory(category);
        // A fixed prefix guarantees the existing password policy's four character classes.
        // The remaining 192 random bits come from the OS CSPRNG. This value stays in memory.
        var temporaryPassword = "Tm1!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var passwordHash = passwordHasher.Hash(temporaryPassword);

        Result<ProvisionedAccountDto> created;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                created = await CreateInTransactionAsync();
                break;
            }
            catch (Exception exception) when (attempt < 3 && db.IsDeadlock(exception))
            {
                db.ClearTrackedEntities();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
            catch (Exception exception) when (db.IsDeadlock(exception))
            {
                db.ClearTrackedEntities();
                return Failure(ProvisioningErrors.Unavailable, "Account creation is temporarily unavailable. Please try again.");
            }
            catch (DbUpdateException exception) when (persistenceErrors.GetUniqueConflictCode(exception) is { } conflictCode)
            {
                db.ClearTrackedEntities();
                var collisionExists = conflictCode switch
                {
                    ProvisioningErrors.DuplicateEmail => await db.Users.AsNoTracking().AnyAsync(
                        user => user.Email != null && user.Email.Trim().ToLower() == normalizedEmail, cancellationToken),
                    ProvisioningErrors.DuplicatePhone => await db.Users.AsNoTracking().AnyAsync(
                        user => user.PhoneNumber == normalizedPhone, cancellationToken),
                    ProvisioningErrors.PoiAlreadyLinked => await db.ServiceProviders.AsNoTracking().SelectMany(provider => provider.PoiLinks)
                        .AnyAsync(link => selectedIds.Contains(link.PointOfInterestId), cancellationToken),
                    _ => false,
                };
                if (!collisionExists) throw;
                return Failure(conflictCode, "An account identifier or selected point of interest is already assigned.");
            }
        }

        if (created.IsFailure) return created;

        // SMTP cannot participate in the SQL transaction. Never retry creation after this point,
        // and never put the transient password into an outbox or durable audit payload.
        EmailDeliveryResult delivery;
        try
        {
            delivery = await emailSender.SendTemporaryPasswordAsync(normalizedEmail, temporaryPassword, cancellationToken);
        }
        catch (Exception)
        {
            // Even cancellation here follows a committed account. Return metadata so callers
            // can recover that account instead of treating creation as rolled back.
            logger.LogWarning("Provisioned account {UserId} email delivery outcome is unknown.", created.Value.UserId);
            delivery = EmailDeliveryResult.Unknown;
        }

        return Result.Success(created.Value with { EmailDeliveryStatus = delivery.Status });

        Task<Result<ProvisionedAccountDto>> CreateInTransactionAsync() => db.ExecuteInSerializableTransactionAsync(async ct =>
        {
            await provisioningLock.AcquireAsync(normalizedEmail, normalizedPhone, selectedIds, ct);
            // Recheck authoritative acting state under the transaction's locks.
            if (!await CanProvisionAsync(actorRole, ct))
            {
                return Failure(ProvisioningErrors.Forbidden, "The acting account cannot provision this role.");
            }

            if (await db.Users.AnyAsync(user => user.Email != null && user.Email.Trim().ToLower() == normalizedEmail, ct))
            {
                return Failure(ProvisioningErrors.DuplicateEmail, "The email address is already registered.");
            }

            if (await db.Users.AnyAsync(user => user.PhoneNumber == normalizedPhone, ct))
            {
                return Failure(ProvisioningErrors.DuplicatePhone, "The phone number is already registered.");
            }

            List<PointOfInterest> pois = [];
            if (role == UserRole.ServiceProvider)
            {
                pois = await db.PointsOfInterest.Where(poi => selectedIds.Contains(poi.Id)
                    && poi.Status == PointOfInterestStatus.Active
                    && CommercialPoiCategories.Contains(poi.Category.Name)).ToListAsync(ct);
                if (pois.Count != selectedIds.Length)
                {
                    return Failure(ProvisioningErrors.InvalidPoi, "Select Active commercial points of interest.");
                }

                if (await db.ServiceProviders.SelectMany(provider => provider.PoiLinks)
                    .AnyAsync(link => selectedIds.Contains(link.PointOfInterestId), ct))
                {
                    return Failure(ProvisioningErrors.PoiAlreadyLinked, "A point of interest is already linked to another account.");
                }
            }

            var now = clock.UtcNow;
            var user = new User
            {
                FullName = fullName.Trim(),
                Email = normalizedEmail,
                PhoneNumber = normalizedPhone,
                Role = role,
                Status = AccountStatus.Active,
                MustChangePassword = true,
                PasswordHash = passwordHash,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.Users.Add(user);
            ServiceProvider? provider = null;
            if (role == UserRole.ServiceProvider)
            {
                provider = ServiceProvider.Create(businessName!, normalizedCategory!, ServiceProvider.StatusActive,
                    normalizedEmail, normalizedPhone);
                provider.AssignAccount(user);
                foreach (var poi in pois)
                {
                    provider.PoiLinks.Add(ProviderPoiLink.Create(provider, poi));
                }

                db.ServiceProviders.Add(provider);
            }

            await db.SaveChangesAsync(ct);
            db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(currentUser.UserId,
                role == UserRole.Staff ? StaffCreatedAction : ProviderCreatedAction,
                AccountEntity, user.Id, now, AuditOutcome.Success,
                afterData: JsonSerializer.Serialize(new { user.Id, user.Role, ServiceProviderId = provider?.Id, LinkedPoiIds = selectedIds })));
            await db.SaveChangesAsync(ct);
            return Result.Success(new ProvisionedAccountDto(user.Id, user.FullName, normalizedEmail, normalizedPhone,
                role, user.Status, true, provider?.Id, selectedIds, EmailDeliveryStatus.Unknown));
        }, cancellationToken);
    }

    private Task<bool> CanProvisionAsync(UserRole role, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(user => user.Id == currentUser.UserId && user.Role == role
            && user.Status == AccountStatus.Active && !user.MustChangePassword, cancellationToken);

    public static string? NormalizeCategory(string? category) => category?.Trim() switch
    {
        VehicleRentalCategory => CommercialService.CategoryVehicle,
        CommercialService.CategoryVehicle => CommercialService.CategoryVehicle,
        CommercialService.CategoryHotel => CommercialService.CategoryHotel,
        CommercialService.CategoryRestaurant => CommercialService.CategoryRestaurant,
        _ => null,
    };

    private static Result<ProvisionedAccountDto> Failure(string code, string message) =>
        Result.Failure<ProvisionedAccountDto>(code, message);
}