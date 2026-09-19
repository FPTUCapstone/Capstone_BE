using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.SystemConfigs.Common;

namespace TripMate.Application.Features.Admin.SystemConfigs.GetAlgorithmParameters;

public class GetAlgorithmParametersQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetAlgorithmParametersQuery, Result<AlgorithmParametersDto>>
{
    private static readonly TimeSpan VietnamUtcOffset = TimeSpan.FromHours(7);

    public async Task<Result<AlgorithmParametersDto>> Handle(
        GetAlgorithmParametersQuery request,
        CancellationToken cancellationToken)
    {
        // 1. Authorization check (SRS 3.1.3 / CR-11, locked MSG126)
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<AlgorithmParametersDto>(
                AlgorithmConfigErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        // 2. Read only the four managed keys; missing rows fall back to seeded defaults.
        var rows = await dbContext.SystemConfigs
            .AsNoTracking()
            .Where(config => AlgorithmParameterDefinitions.ManagedKeys.Contains(config.ConfigKey))
            .ToDictionaryAsync(config => config.ConfigKey, cancellationToken);

        string? ValueOf(string key) =>
            rows.TryGetValue(key, out var row) ? row.ConfigValue : null;

        var bufferTimeMinutes = AlgorithmParameterDefinitions.ParseBufferTimeMinutes(ValueOf(AlgorithmParameterDefinitions.BufferTimeMinutesKey));
        var travelSpeed = AlgorithmParameterDefinitions.ParseTravelSpeed(ValueOf(AlgorithmParameterDefinitions.DefaultTravelSpeedKmhKey));
        var searchRadius = AlgorithmParameterDefinitions.ParseSearchRadius(ValueOf(AlgorithmParameterDefinitions.ReroutingSearchRadiusKmKey));
        var severity = AlgorithmParameterDefinitions.ParseSeverity(ValueOf(AlgorithmParameterDefinitions.WeatherAlertThresholdSeverityKey));

        DateTimeOffset? latestUpdate = rows.Count == 0
            ? null
            : rows.Values.Max(config => config.UpdatedAtUtc);

        return Result.Success(new AlgorithmParametersDto(
            bufferTimeMinutes,
            travelSpeed,
            searchRadius,
            severity,
            latestUpdate,
            latestUpdate?.ToOffset(VietnamUtcOffset).ToString("dd/MM/yyyy HH:mm:ss")));
    }
}