using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Create;

public sealed class CreatePoiCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<CreatePoiCommand, Result<PoiResponseDto>>
{
    private static readonly JsonSerializerOptions AuditJsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<Result<PoiResponseDto>> Handle(
        CreatePoiCommand request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return AdminAccessRequired();
        }

        var administrator = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(
                user =>
                    user.Id == currentUserService.UserId.Value
                    && user.Role == UserRole.Administrator
                    && user.Status == AccountStatus.Active,
                cancellationToken);

        if (administrator is null)
        {
            return AdminAccessRequired();
        }

        var category = await dbContext.PoiCategories.FirstOrDefaultAsync(
            item => item.Id == request.CategoryId,
            cancellationToken);

        if (category is null)
        {
            return Result.Failure<PoiResponseDto>(
                PoiErrorCodes.ReferenceNotFound,
                "The selected POI category does not exist.");
        }

        var requestedTagIds = request.TagIds?.Distinct().ToArray() ?? [];
        var tags = requestedTagIds.Length == 0
            ? []
            : await dbContext.Tags
                .Where(tag => requestedTagIds.Contains(tag.Id))
                .ToListAsync(cancellationToken);

        if (tags.Count != requestedTagIds.Length)
        {
            return Result.Failure<PoiResponseDto>(
                PoiErrorCodes.ReferenceNotFound,
                "One or more selected POI tags do not exist.");
        }

        var normalizedName = request.Name.Trim();
        var normalizedNameUpper = normalizedName.ToUpperInvariant();
        var normalizedLatitude = NormalizeCoordinate(request.Latitude!.Value);
        var normalizedLongitude = NormalizeCoordinate(request.Longitude!.Value);

        var existingPoiId = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(poi =>
                poi.Status == PointOfInterestStatus.Active
                && poi.Latitude == normalizedLatitude
                && poi.Longitude == normalizedLongitude
                && poi.Name.ToUpper() == normalizedNameUpper)
            .Select(poi => (long?)poi.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingPoiId.HasValue && !request.ConfirmDuplicate)
        {
            return Result.Failure<PoiResponseDto>(
                PoiErrorCodes.PossibleDuplicate,
                "An active POI with the same name and location already exists.",
                new Dictionary<string, object?>
                {
                    ["existingPoiId"] = existingPoiId.Value,
                });
        }

        var now = dateTimeProvider.UtcNow;
        var pointOfInterest = PointOfInterest.Create(
            category,
            normalizedName,
            normalizedLatitude,
            normalizedLongitude,
            administrator.Id,
            now,
            request.Address,
            request.Description,
            request.IndoorOutdoor ?? IndoorOutdoorType.Outdoor,
            request.AverageVisitDurationMinutes
                ?? PointOfInterest.DefaultAverageVisitDurationMinutes,
            request.HasShelter ?? false);

        foreach (var hours in request.OpeningHours ?? [])
        {
            pointOfInterest.AddOpeningHour(PoiOpeningHour.Create(
                checked((byte)hours.DayOfWeek!.Value),
                hours.OpenTime,
                hours.CloseTime,
                hours.IsClosed));
        }

        foreach (var tag in tags)
        {
            pointOfInterest.AddTag(tag);
        }

        var response = await dbContext.ExecuteInTransactionAsync(
            async transactionCancellationToken =>
            {
                dbContext.PointsOfInterest.Add(pointOfInterest);
                await dbContext.SaveChangesAsync(transactionCancellationToken);

                var persistedDto = ToResponse(pointOfInterest);
                var audit = AuditLog.CreatePoiCreated(
                    administrator.Id,
                    pointOfInterest.Id,
                    JsonSerializer.Serialize(persistedDto, AuditJsonOptions),
                    now);

                dbContext.AuditLogs.Add(audit);
                await dbContext.SaveChangesAsync(transactionCancellationToken);

                return persistedDto;
            },
            cancellationToken);

        return Result.Success(response);
    }

    private static Result<PoiResponseDto> AdminAccessRequired() =>
        Result.Failure<PoiResponseDto>(
            PoiErrorCodes.AdminAccessRequired,
            PoiErrorMessages.AdminAccessRequired);

    private static decimal NormalizeCoordinate(decimal value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static PoiResponseDto ToResponse(PointOfInterest poi) =>
        new(
            poi.Id,
            poi.CategoryId,
            poi.Name,
            poi.Description,
            poi.Latitude,
            poi.Longitude,
            poi.Address,
            poi.IndoorOutdoor,
            poi.ScenicScore,
            poi.PhotoRating,
            poi.AverageVisitDurationMinutes,
            poi.HasShelter,
            poi.Status,
            poi.CreatedById
                ?? throw new InvalidOperationException("A persisted POI response must have a creator."),
            poi.CreatedAtUtc,
            poi.UpdatedAtUtc,
            poi.OpeningHours
                .OrderBy(hours => hours.DayOfWeek)
                .Select(hours => new PoiOpeningHourDto(
                    hours.DayOfWeek,
                    hours.OpenTime,
                    hours.CloseTime,
                    hours.IsClosed))
                .ToArray(),
            poi.PoiTags
                .Select(mapping => mapping.TagId)
                .OrderBy(tagId => tagId)
                .ToArray());
}