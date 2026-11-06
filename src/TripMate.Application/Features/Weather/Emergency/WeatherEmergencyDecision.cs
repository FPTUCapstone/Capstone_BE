namespace TripMate.Application.Features.Weather.Emergency;

public enum WeatherDecisionOutcome
{
    NoImpact = 0,
    NoAction = 1,
    ReviewNeeded = 2,
    SafetyReview = 3,
    ActiveSafetyHold = 4,
}

public enum WeatherAlertSource
{
    Unknown,
    OneCallForecast,
    GlobalWeatherAlerts,
    OfficialVietnam,
}

public sealed record WeatherPoint(decimal Latitude, decimal Longitude)
{
    /// <summary>
    /// The point rounded to 4 decimals (about 11 m). One monitored Tomorrow.io Location stands for
    /// every TripMate point in its cell, so matching and the location key use the same rounding.
    /// </summary>
    public WeatherPoint MonitoringCell => new(
        Math.Round(Latitude, 4, MidpointRounding.AwayFromZero),
        Math.Round(Longitude, 4, MidpointRounding.AwayFromZero));
}

public sealed record WeatherPolygon(IReadOnlyList<WeatherPoint> Outer)
{
    public IReadOnlyList<IReadOnlyList<WeatherPoint>> Holes { get; init; } = [];

    public bool Contains(WeatherPoint point) =>
        IsInsideRing(Outer, point) && !Holes.Any(hole => IsInsideRing(hole, point));

    private static bool IsInsideRing(IReadOnlyList<WeatherPoint> ring, WeatherPoint point)
    {
        if (ring.Count < 3) return false;
        bool inside = false;
        for (int index = 0, previous = ring.Count - 1; index < ring.Count; previous = index++)
        {
            var a = ring[previous];
            var b = ring[index];
            if (IsOnSegment(a, b, point)) return true;
            bool crossesLatitude = (a.Latitude > point.Latitude) != (b.Latitude > point.Latitude);
            if (crossesLatitude)
            {
                decimal crossingLongitude = a.Longitude
                    + (point.Latitude - a.Latitude) * (b.Longitude - a.Longitude)
                    / (b.Latitude - a.Latitude);
                if (point.Longitude < crossingLongitude) inside = !inside;
            }
        }

        return inside;
    }

    private static bool IsOnSegment(WeatherPoint a, WeatherPoint b, WeatherPoint point)
    {
        decimal cross = (point.Longitude - a.Longitude) * (b.Latitude - a.Latitude)
            - (point.Latitude - a.Latitude) * (b.Longitude - a.Longitude);
        return cross == 0
            && point.Latitude >= Math.Min(a.Latitude, b.Latitude)
            && point.Latitude <= Math.Max(a.Latitude, b.Latitude)
            && point.Longitude >= Math.Min(a.Longitude, b.Longitude)
            && point.Longitude <= Math.Max(a.Longitude, b.Longitude);
    }
}

public sealed record WeatherAlertEvidence(
    string AlertId,
    bool TrustedIssuer,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    IReadOnlyList<WeatherPolygon> Areas,
    string? Severity,
    string? Certainty,
    string? Urgency,
    int? OfficialVietnamRiskLevel,
    bool HasVerifiedTravelProhibition)
{
    public WeatherAlertSource Source { get; init; } = WeatherAlertSource.Unknown;
}

public sealed record WeatherTourStop(WeatherPoint Point, bool? Required,
    DateTimeOffset? PlannedVisitAtUtc, bool Passed);

public sealed record WeatherDepartureImpact(
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    WeatherPoint? MeetingPoint,
    IReadOnlyList<WeatherTourStop> RequiredStops,
    bool DataReviewed);

public static class WeatherEmergencyDecision
{
    // Legacy evaluated method for WeatherAlertEvidence
    public static WeatherDecisionOutcome Evaluate(
        WeatherAlertEvidence alert,
        WeatherDepartureImpact departure,
        DateTimeOffset nowUtc,
        int minimumOfficialRiskLevel = 3)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(departure);
        if (minimumOfficialRiskLevel is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(minimumOfficialRiskLevel));

        if (nowUtc >= departure.EndAtUtc || alert.EndsAtUtc <= nowUtc
            || alert.EndsAtUtc <= alert.StartsAtUtc)
            return WeatherDecisionOutcome.NoImpact;
        if (!alert.TrustedIssuer || string.IsNullOrWhiteSpace(alert.AlertId)
            || alert.Areas.Count == 0 || !departure.DataReviewed)
            return WeatherDecisionOutcome.ReviewNeeded;

        bool active = nowUtc >= departure.StartAtUtc;
        bool impacted = !active && departure.MeetingPoint is { } meeting
            && AtPoint(meeting, departure.StartAtUtc, alert);
        bool unknownStop = false;
        foreach (var stop in departure.RequiredStops)
        {
            if (active && stop.Passed) continue;
            bool inArea = alert.Areas.Any(area => area.Contains(stop.Point));
            bool inTime = stop.PlannedVisitAtUtc is { } visit
                ? alert.StartsAtUtc <= visit && visit <= alert.EndsAtUtc
                : alert.StartsAtUtc < departure.EndAtUtc && alert.EndsAtUtc > nowUtc;
            if (!inArea || !inTime) continue;
            if (!stop.Required.HasValue) unknownStop = true;
            else if (stop.Required.Value) impacted = true;
        }

        if (unknownStop) return WeatherDecisionOutcome.ReviewNeeded;
        if (!impacted) return WeatherDecisionOutcome.NoImpact;
        return WeatherDecisionOutcome.ReviewNeeded;
    }

    public static WeatherDecisionEvaluation Evaluate(
        NormalizedWeatherEvent weatherEvent,
        WeatherDepartureImpact departure,
        DateTimeOffset nowUtc,
        int minimumOfficialRiskLevel = 3,
        IReadOnlySet<string>? authorityAllowlist = null)
    {
        ArgumentNullException.ThrowIfNull(weatherEvent);
        ArgumentNullException.ThrowIfNull(departure);
        if (minimumOfficialRiskLevel is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(minimumOfficialRiskLevel));

        if (string.IsNullOrWhiteSpace(weatherEvent.Provider) || string.IsNullOrWhiteSpace(weatherEvent.ProviderEventId))
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.NoAction,
                "weather.invalid_identity",
                "Weather event lacks stable provider identity.");
        }

        if (weatherEvent.ValidToUtc <= weatherEvent.ValidFromUtc)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.NoAction,
                "weather.invalid_time_interval",
                "Weather event valid interval is invalid.");
        }

        if (!weatherEvent.HasSpatialScope)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.NoAction,
                "weather.missing_geometry",
                "Weather event contains no spatial areas.");
        }

        if (weatherEvent.IsWithdrawn)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.NoAction,
                "weather.event_withdrawn",
                "Weather bulletin has been withdrawn by the issuing authority.");
        }

        if (weatherEvent.ValidToUtc <= nowUtc)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.NoAction,
                "weather.stale_or_expired",
                "Weather bulletin validity has expired.");
        }

        if (nowUtc >= departure.EndAtUtc)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.NoAction,
                "weather.departure_already_ended",
                "Tour departure has already completed.");
        }

        if (!departure.DataReviewed)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.ReviewNeeded,
                "weather.departure_data_unreviewed",
                "Departure data has not been marked as reviewed.");
        }

        bool isActive = nowUtc >= departure.StartAtUtc;

        // Check spatial & temporal intersection
        bool impacted = !isActive && departure.MeetingPoint is { } meeting
            && AtPoint(meeting, departure.StartAtUtc, weatherEvent);

        bool unknownStop = false;
        foreach (var stop in departure.RequiredStops)
        {
            if (isActive && stop.Passed) continue;

            bool inArea = weatherEvent.Covers(stop.Point);
            bool inTime = stop.PlannedVisitAtUtc is { } visit
                ? weatherEvent.ValidFromUtc <= visit && visit <= weatherEvent.ValidToUtc
                : weatherEvent.ValidFromUtc < departure.EndAtUtc && weatherEvent.ValidToUtc > (isActive ? nowUtc : departure.StartAtUtc);

            if (!inArea || !inTime) continue;

            if (!stop.Required.HasValue) unknownStop = true;
            else if (stop.Required.Value) impacted = true;
        }

        if (unknownStop)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.ReviewNeeded,
                "weather.unknown_stop_classification",
                "Departure has an unclassified stop intersecting the weather event.");
        }

        if (!impacted)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.NoAction,
                "weather.no_spatial_temporal_match",
                "Weather event does not intersect departure meeting point or required stops.");
        }

        // Custom Events and samples are advisory evidence for an Operator.
        if (weatherEvent.IsCustomEvent || weatherEvent.IsSamplePayload)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.ReviewNeeded,
                "weather.custom_or_sample_signal",
                "Custom vector event or sample payload requires Operator review.");
        }

        // Authority validation
        bool isAuthorityAllowlisted = VietnameseWeatherAuthorities.IsAllowlisted(
            weatherEvent.OriginalAuthority, authorityAllowlist);

        if (!isAuthorityAllowlisted)
        {
            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.ReviewNeeded,
                "weather.authority_not_allowlisted",
                $"Authority '{weatherEvent.OriginalAuthority}' is not in the Vietnamese authority allowlist.");
        }

        // Hard directive evaluation
        bool hasHardDirective = weatherEvent.VerifiedDirective is WeatherVerifiedDirective.TravelProhibition
            or WeatherVerifiedDirective.Evacuation
            or WeatherVerifiedDirective.Closure;

        if (hasHardDirective)
        {
            if (isActive)
            {
                return new WeatherDecisionEvaluation(
                    WeatherDecisionOutcome.ActiveSafetyHold,
                    "weather.verified_directive_active_safety_hold",
                    "Active departure is subject to a verified emergency directive; safety hold instituted.");
            }

            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.SafetyReview,
                "weather.verified_directive_operator_review",
                "Verified emergency directive requires Operator review and manual action.");
        }

        // Precautionary risk level >= 3 (without hard directive)
        if (weatherEvent.OfficialVietnamRiskLevel.HasValue
            && weatherEvent.OfficialVietnamRiskLevel.Value >= minimumOfficialRiskLevel)
        {
            if (isActive)
            {
                return new WeatherDecisionEvaluation(
                    WeatherDecisionOutcome.ActiveSafetyHold,
                    "weather.risk_level_active_safety_hold",
                    $"Active departure impacted by official Vietnam risk level {weatherEvent.OfficialVietnamRiskLevel.Value}.");
            }

            return new WeatherDecisionEvaluation(
                WeatherDecisionOutcome.SafetyReview,
                "weather.risk_level_precautionary_review",
                $"Official Vietnam disaster risk level {weatherEvent.OfficialVietnamRiskLevel.Value} requires safety review.");
        }

        // Advisory / forecast / lower risk level
        return new WeatherDecisionEvaluation(
            WeatherDecisionOutcome.ReviewNeeded,
            "weather.provider_advisory_review",
            "Provider advisory or sub-threshold weather condition requires review.");
    }

    private static bool AtPoint(
        WeatherPoint point,
        DateTimeOffset visitAtUtc,
        NormalizedWeatherEvent weatherEvent) =>
        weatherEvent.ValidFromUtc <= visitAtUtc && visitAtUtc <= weatherEvent.ValidToUtc
        && weatherEvent.Covers(point);

    private static bool AtPoint(WeatherPoint point, DateTimeOffset visitAtUtc,
        WeatherAlertEvidence alert) =>
        alert.StartsAtUtc <= visitAtUtc && visitAtUtc <= alert.EndsAtUtc
        && alert.Areas.Any(area => area.Contains(point));
}
