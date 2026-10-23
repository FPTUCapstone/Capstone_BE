using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Common;

/// <summary>Stable blocker codes returned to clients; clients may map them to localized copy.</summary>
public static class TourReviewBlockers
{
    public const string PrimaryImageMissing = "primary_image_missing";
    public const string FutureDepartureMissing = "future_departure_missing";
    public const string CancellationPolicyMissing = "cancellation_policy_missing";
    public const string DestinationMissing = "destination_missing";
}

/// <summary>Facts about a package that decide whether it may be sent for review.</summary>
public sealed record TourReviewFacts(
    bool HasPrimaryImage,
    bool HasFuturePricedDeparture,
    bool HasCancellationPolicySnapshot,
    bool HasDestination);

/// <summary>
/// The single source of truth for the review prerequisites. The detail query and
/// <c>submit-review</c> both use it, so the UI hint can never disagree with the command.
/// </summary>
public static class TourReviewReadinessEvaluator
{
    public static IReadOnlyList<string> Evaluate(TourReviewFacts facts)
    {
        var blockers = new List<string>(4);
        if (!facts.HasPrimaryImage) blockers.Add(TourReviewBlockers.PrimaryImageMissing);
        if (!facts.HasFuturePricedDeparture) blockers.Add(TourReviewBlockers.FutureDepartureMissing);
        if (!facts.HasCancellationPolicySnapshot) blockers.Add(TourReviewBlockers.CancellationPolicyMissing);
        if (!facts.HasDestination) blockers.Add(TourReviewBlockers.DestinationMissing);
        return blockers;
    }

    public static bool IsEditable(TourStatus status) => status is TourStatus.Draft or TourStatus.Rejected;

    /// <summary>Reads the facts for one tour from the database and evaluates them at <paramref name="nowUtc"/>.</summary>
    public static async Task<TourReviewReadiness> LoadAsync(
        IApplicationDbContext dbContext,
        long tourId,
        long? cancellationPolicyTemplateId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var activeMedia = dbContext.TourMedia.Where(media =>
            media.TourId == tourId && media.LifecycleStatus == TourMediaLifecycleStatus.Active);
        int mediaCount = await activeMedia.CountAsync(cancellationToken);
        bool hasPrimaryImage = await activeMedia.AnyAsync(media => media.IsPrimary, cancellationToken);
        bool hasFutureDeparture = await dbContext.TourSchedules.AnyAsync(schedule =>
            schedule.TourId == tourId && schedule.StartAtUtc > nowUtc &&
            schedule.Status == TourScheduleStatus.Scheduled && schedule.AdultPrice > 0,
            cancellationToken);
        bool hasDestination = await dbContext.TourDestinations.AnyAsync(link => link.TourId == tourId,
            cancellationToken);
        bool hasPolicySnapshot = cancellationPolicyTemplateId.HasValue &&
            await dbContext.TourCancellationPolicyRules.AnyAsync(rule => rule.TourId == tourId,
                cancellationToken);

        var blockers = Evaluate(new TourReviewFacts(hasPrimaryImage, hasFutureDeparture, hasPolicySnapshot, hasDestination));
        return new TourReviewReadiness(mediaCount, hasPrimaryImage, blockers);
    }
}

public sealed record TourReviewReadiness(int MediaCount, bool HasPrimaryImage, IReadOnlyList<string> Blockers)
{
    public bool IsReady => Blockers.Count == 0;
}