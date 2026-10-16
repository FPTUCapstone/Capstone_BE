using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Preferences.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Preferences.Update;

/// <summary>
/// UC-09 full replacement (spec §6.3–§6.4): validates everything before writing, then inserts or
/// updates the single TravelerProfiles row with one SaveChanges call.
/// </summary>
public sealed class UpdateTravelerPreferencesCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<UpdateTravelerPreferencesCommand, Result<TravelerPreferencesDto>>
{
    private const int SqlServerUniqueIndexViolation = 2601;
    private const int SqlServerPrimaryKeyViolation = 2627;

    public async Task<Result<TravelerPreferencesDto>> Handle(
        UpdateTravelerPreferencesCommand request,
        CancellationToken cancellationToken)
    {
        if (!await TravelerPreferenceAccess.IsActiveTravelerAsync(
                dbContext,
                request.TravelerUserId,
                cancellationToken))
        {
            return Result.Failure<TravelerPreferencesDto>(
                TravelerPreferenceErrors.Forbidden,
                "Only an active Traveler can update travel preferences.");
        }

        var catalog = InterestOptionCatalog.Fixed;

        var interests = new List<string>();
        var unknownInterests = new List<string>();
        foreach (var value in request.Interests ?? [])
        {
            var key = catalog.Canonicalize(value);
            if (key is null)
            {
                unknownInterests.Add(value?.Trim() ?? string.Empty);
            }
            else if (!interests.Contains(key, StringComparer.Ordinal))
            {
                interests.Add(key);
            }
        }

        if (unknownInterests.Count > 0)
        {
            return FieldFailure(
                TravelerPreferenceErrors.InvalidOption,
                TravelerPreferenceErrors.InterestsField,
                unknownInterests);
        }

        var food = (request.FoodPreferences ?? [])
            .Select(TravelerPreferenceKeys.CanonicalFoodKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var interestsJson = TravelerPreferenceMapping.SerializeKeys(interests);
        if (interestsJson?.Length > TravelerProfile.PreferenceJsonMaxLength)
        {
            return FieldFailure(
                TravelerPreferenceErrors.ValueTooLong,
                TravelerPreferenceErrors.InterestsField,
                []);
        }

        var foodJson = TravelerPreferenceMapping.SerializeKeys(food);
        if (foodJson?.Length > TravelerProfile.PreferenceJsonMaxLength)
        {
            return FieldFailure(
                TravelerPreferenceErrors.ValueTooLong,
                TravelerPreferenceErrors.FoodPreferencesField,
                []);
        }

        // The validator has already rejected unknown names, so parsing cannot fail here.
        TravelerPreferenceKeys.TryParseKey<TransportMode>(request.PreferredTransportMode, out var transport);
        TravelerPreferenceKeys.TryParseKey<TravelerPace>(request.TravelPace, out var pace);
        TravelerPreferenceKeys.TryParseKey<RiskToleranceLevel>(request.RiskTolerance, out var risk);

        var values = new PreferenceValues(
            interestsJson,
            transport,
            pace,
            risk,
            foodJson);

        var profile = await SaveAsync(request.TravelerUserId, values, cancellationToken);

        return Result.Success(TravelerPreferenceMapping.ToDto(profile, catalog));
    }

    private async Task<TravelerProfile> SaveAsync(
        long userId,
        PreferenceValues values,
        CancellationToken cancellationToken)
    {
        var profile = await ApplyAsync(userId, values, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return profile;
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            // A concurrent first PUT inserted the row between our read and our insert (spec
            // §6.4). Only an insert can collide on the user_id key. Retry exactly once as an
            // update; last write wins. A second failure propagates as an unexpected error.
            dbContext.ClearTrackedEntities();
            var existing = await ApplyAsync(userId, values, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return existing;
        }
    }

    private async Task<TravelerProfile> ApplyAsync(
        long userId,
        PreferenceValues values,
        CancellationToken cancellationToken)
    {
        var now = dateTimeProvider.UtcNow;
        var profile = await dbContext.TravelerProfiles
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        if (profile is null)
        {
            profile = TravelerProfile.CreateEmpty(userId, now);
            dbContext.TravelerProfiles.Add(profile);
        }

        profile.ReplacePreferences(
            values.InterestsJson,
            values.Transport,
            values.Pace,
            values.Risk,
            values.FoodJson,
            now);
        return profile;
    }

    private static Result<TravelerPreferencesDto> FieldFailure(
        string errorCode,
        string field,
        IReadOnlyList<string> invalidValues) =>
        Result.Failure<TravelerPreferencesDto>(
            errorCode,
            errorCode,
            new Dictionary<string, object?>
            {
                [TravelerPreferenceErrors.FieldMetadataKey] = field,
                [TravelerPreferenceErrors.InvalidValuesMetadataKey] = invalidValues,
            });

    /// <summary>
    /// Application must not reference the SQL Server provider, so the provider exception is
    /// recognized by type name and error number, as elsewhere in this layer.
    /// </summary>
    private static bool IsDuplicateKey(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (string.Equals(current.GetType().Name, "SqlException", StringComparison.Ordinal)
                && current.GetType().GetProperty("Number")?.GetValue(current)
                    is SqlServerUniqueIndexViolation or SqlServerPrimaryKeyViolation)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record PreferenceValues(
        string? InterestsJson,
        TransportMode? Transport,
        TravelerPace? Pace,
        RiskToleranceLevel? Risk,
        string? FoodJson);
}
