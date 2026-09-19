using System.Globalization;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.SystemConfigs.Common;
using TripMate.Application.Features.Admin.SystemConfigs.GetAlgorithmParameters;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.SystemConfigs.UpdateAlgorithmParameters;

public record UpdateAlgorithmParametersCommand(
    int BufferTimeMinutes,
    double DefaultTravelSpeedKmh,
    double ReroutingSearchRadiusKm,
    string WeatherAlertThresholdSeverity)
    : IRequest<Result<AlgorithmParametersDto>>;

public class UpdateAlgorithmParametersCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<UpdateAlgorithmParametersCommand, Result<AlgorithmParametersDto>>
{
    private static readonly TimeSpan VietnamUtcOffset = TimeSpan.FromHours(7);

    public async Task<Result<AlgorithmParametersDto>> Handle(
        UpdateAlgorithmParametersCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Authorization check (SRS 3.1.3 / CR-11, locked MSG126)
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<AlgorithmParametersDto>(
                AlgorithmConfigErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        // 2. Range/enum validation inside the handler (locked MSG118, 422) — deliberately
        //    not FluentValidation rules, which ValidationBehaviour would surface as 400.
        if (request.BufferTimeMinutes is < 5 or > 60 ||
            request.DefaultTravelSpeedKmh is < 10 or > 120 ||
            request.ReroutingSearchRadiusKm is < 1 or > 50 ||
            !AlgorithmParameterDefinitions.TryNormalizeSeverity(
                request.WeatherAlertThresholdSeverity, out var severity))
        {
            return Result.Failure<AlgorithmParametersDto>(
                AlgorithmConfigErrorCodes.InvalidValue,
                AlgorithmParameterDefinitions.InvalidValueMessage);
        }

        // 3. Load the managed rows with change tracking — they are about to be modified.
        var managedRows = await dbContext.SystemConfigs
            .Where(config => AlgorithmParameterDefinitions.ManagedKeys.Contains(config.ConfigKey))
            .ToDictionaryAsync(config => config.ConfigKey, cancellationToken);

        var now = dateTimeProvider.UtcNow;
        var newValues = new Dictionary<string, string>
        {
            [AlgorithmParameterDefinitions.BufferTimeMinutesKey] =
                request.BufferTimeMinutes.ToString(CultureInfo.InvariantCulture),
            [AlgorithmParameterDefinitions.DefaultTravelSpeedKmhKey] =
                request.DefaultTravelSpeedKmh.ToString(CultureInfo.InvariantCulture),
            [AlgorithmParameterDefinitions.ReroutingSearchRadiusKmKey] =
                request.ReroutingSearchRadiusKm.ToString(CultureInfo.InvariantCulture),
            [AlgorithmParameterDefinitions.WeatherAlertThresholdSeverityKey] = severity,
        };

        var beforeData = JsonSerializer.Serialize(
            managedRows.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ConfigValue));

        // 4. Upsert the four managed rows; foreign config rows are never touched.
        foreach (var (key, value) in newValues)
        {
            if (managedRows.TryGetValue(key, out var existing))
            {
                existing.UpdateValue(value, currentUserService.UserId, now);
            }
            else
            {
                dbContext.SystemConfigs.Add(new SystemConfig(
                    key, value, description: null, updatedBy: currentUserService.UserId, now));
            }
        }

        // 5. One immutable audit entry (CR-14 / BR-130) recorded with an explicit Success
        //    outcome in the same save as the config change (Result/Reason amendment).
        var auditEntry = AuditLog.CreateRecordedOutcome(
            actorUserId: currentUserService.UserId,
            actionType: AuditActionTypes.AlgorithmParametersUpdate,
            affectedEntity: AuditEntityTypes.SystemConfig,
            affectedEntityId: null,
            createdAtUtc: now,
            result: AuditOutcome.Success,
            beforeData: beforeData,
            afterData: JsonSerializer.Serialize(newValues));

        dbContext.AuditLogs.Add(auditEntry);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new AlgorithmParametersDto(
            request.BufferTimeMinutes,
            request.DefaultTravelSpeedKmh,
            request.ReroutingSearchRadiusKm,
            severity,
            now,
            now.ToOffset(VietnamUtcOffset).ToString("dd/MM/yyyy HH:mm:ss")));
    }
}