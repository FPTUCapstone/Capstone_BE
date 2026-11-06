using TripMate.Application.Features.Weather.Emergency;

namespace TripMate.Application.UnitTests.Features.Weather;

public sealed class NormalizedWeatherEventTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(8);
    private static readonly WeatherPoint DaNang = new(16.0471m, 108.2068m);
    private static readonly WeatherPolygon DaNangArea = new(
        [new(15m, 107m), new(17m, 107m), new(17m, 109m), new(15m, 109m)]);

    [Fact]
    public void VerifiedDirective_PreStart_RequiresOperatorReview()
    {
        var evaluation = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent(), Departure(), Start.AddHours(-1));

        Assert.Equal(WeatherDecisionOutcome.SafetyReview, evaluation.Outcome);
        Assert.Equal("weather.verified_directive_operator_review", evaluation.ReasonCode);
    }

    [Fact]
    public void VerifiedDirective_ActiveDeparture_CreatesSafetyHold()
    {
        var departure = Departure() with
        {
            RequiredStops = [new WeatherTourStop(DaNang, true, Start.AddHours(3), false)],
        };

        var evaluation = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent(), departure, Start.AddHours(1));

        Assert.Equal(WeatherDecisionOutcome.ActiveSafetyHold, evaluation.Outcome);
    }

    [Fact]
    public void OfficialRiskLevelThree_WithoutDirective_RequiresSafetyReview()
    {
        var weatherEvent = ValidBaseEvent() with
        {
            OfficialVietnamRiskLevel = 3,
            OriginalAuthority = VietnameseWeatherAuthorities.Nchmf,
        };

        var evaluation = WeatherEmergencyDecision.Evaluate(
            weatherEvent, Departure(), Start.AddHours(-1));

        Assert.Equal(WeatherDecisionOutcome.SafetyReview, evaluation.Outcome);
    }

    [Fact]
    public void ProviderAdvisory_RequiresOperatorReview()
    {
        var weatherEvent = ValidBaseEvent() with
        {
            ProviderSeverity = "Extreme",
        };

        var evaluation = WeatherEmergencyDecision.Evaluate(
            weatherEvent, Departure(), Start.AddHours(-1));

        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded, evaluation.Outcome);
    }

    [Fact]
    public void CustomOrSampleEvent_RequiresOperatorReview()
    {
        var custom = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent() with { IsCustomEvent = true }, Departure(), Start.AddHours(-1));
        var sample = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent() with { IsSamplePayload = true }, Departure(), Start.AddHours(-1));

        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded, custom.Outcome);
        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded, sample.Outcome);
    }

    [Fact]
    public void NonAllowlistedAuthority_RequiresOperatorReview()
    {
        var evaluation = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent() with { OriginalAuthority = "Unknown Foreign Agency" },
            Departure(), Start.AddHours(-1));

        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded, evaluation.Outcome);
        Assert.Equal("weather.authority_not_allowlisted", evaluation.ReasonCode);
    }

    [Fact]
    public void InvalidWithdrawnOrNonIntersectingEvent_TakesNoAction()
    {
        var missingId = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent() with { ProviderEventId = "" }, Departure(), Start.AddHours(-1));
        var withdrawn = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent() with { IsWithdrawn = true }, Departure(), Start.AddHours(-1));
        var distant = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent() with
            {
                Areas = [new WeatherPolygon(
                    [new(21m, 105m), new(22m, 105m), new(22m, 106m), new(21m, 106m)])],
            }, Departure(), Start.AddHours(-1));

        Assert.Equal(WeatherDecisionOutcome.NoAction, missingId.Outcome);
        Assert.Equal(WeatherDecisionOutcome.NoAction, withdrawn.Outcome);
        Assert.Equal(WeatherDecisionOutcome.NoAction, distant.Outcome);
    }

    [Fact]
    public void UnreviewedDepartureData_RequiresOperatorReview()
    {
        var evaluation = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent(), Departure() with { DataReviewed = false }, Start.AddHours(-1));

        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded, evaluation.Outcome);
    }

    [Fact]
    public void PolygonEdge_IsIncludedInImpactMatching()
    {
        var edgeDeparture = Departure() with
        {
            MeetingPoint = new WeatherPoint(15m, 108.2068m),
        };

        var evaluation = WeatherEmergencyDecision.Evaluate(
            ValidHardEvent(), edgeDeparture, Start.AddMinutes(-5));

        Assert.Equal(WeatherDecisionOutcome.SafetyReview, evaluation.Outcome);
    }

    private static NormalizedWeatherEvent ValidHardEvent() => new(
        WeatherProviders.TomorrowIo,
        "tomorrow-evt-001",
        Start.AddHours(-3),
        Start.AddHours(-2),
        End.AddHours(2),
        [DaNangArea],
        Start.AddHours(-2))
    {
        OriginalAuthority = VietnameseWeatherAuthorities.Nchmf,
        OfficialVietnamRiskLevel = 4,
        VerifiedDirective = WeatherVerifiedDirective.TravelProhibition,
        Category = "Typhoon",
    };

    private static NormalizedWeatherEvent ValidBaseEvent() => new(
        WeatherProviders.TomorrowIo,
        "tomorrow-evt-002",
        Start.AddHours(-3),
        Start.AddHours(-2),
        End.AddHours(2),
        [DaNangArea],
        Start.AddHours(-2))
    {
        OriginalAuthority = VietnameseWeatherAuthorities.Nchmf,
        Category = "TropicalStorm",
    };

    private static WeatherDepartureImpact Departure() => new(
        Start, End, DaNang, [], true);
}
