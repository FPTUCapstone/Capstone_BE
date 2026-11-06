using TripMate.Application.Features.Weather.Emergency;

namespace TripMate.Application.UnitTests.Features.Weather;

public sealed class EmergencyWeatherDecisionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(8);
    private static readonly WeatherPoint DaNang = new(16.0471m, 108.2068m);
    private static readonly WeatherPolygon Area = new(
        [new(15m, 107m), new(17m, 107m), new(17m, 109m), new(15m, 109m)]);

    [Fact]
    public void ExtremeObservedImmediate_AtRequiredStop_RequiresOperatorReview()
    {
        var result = WeatherEmergencyDecision.Evaluate(
            Alert() with { Severity = "Extreme", Certainty = "Observed", Urgency = "Immediate" },
            Departure() with { RequiredStops = [new WeatherTourStop(DaNang, true, Start.AddHours(2), false)] },
            Start.AddHours(1));

        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded, result);
    }

    [Theory]
    [InlineData("Severe", "Observed", "Immediate")]
    [InlineData("Extreme", "Possible", "Immediate")]
    [InlineData("Extreme", "Observed", "Future")]
    public void LowerConfidenceOrSeverity_RequiresReview(string severity, string certainty, string urgency)
    {
        var result = WeatherEmergencyDecision.Evaluate(
            Alert() with { Severity = severity, Certainty = certainty, Urgency = urgency },
            Departure(), Start.AddHours(-1));

        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded, result);
    }

    [Fact]
    public void OfficialGradeThree_AtMeetingPoint_RequiresOperatorReview()
    {
        var result = WeatherEmergencyDecision.Evaluate(
            Alert() with { Source = WeatherAlertSource.OfficialVietnam, OfficialVietnamRiskLevel = 3 },
            Departure(), Start.AddHours(-1));

        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded, result);
    }

    [Theory]
    [InlineData(WeatherAlertSource.OneCallForecast)]
    [InlineData(WeatherAlertSource.Unknown)]
    public void ForecastOrUnknownSource_RequiresOperatorReview(WeatherAlertSource source)
    {
        var alert = Alert() with
        {
            Source = source,
            OfficialVietnamRiskLevel = 5,
            HasVerifiedTravelProhibition = true,
        };

        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded,
            WeatherEmergencyDecision.Evaluate(alert, Departure(), Start.AddHours(-1)));
    }

    [Fact]
    public void PassedStopOrWrongArea_DoesNotCancelAnActiveTour()
    {
        var departure = Departure() with
        {
            RequiredStops = [new WeatherTourStop(DaNang, true, Start.AddHours(1), true)],
        };

        Assert.Equal(WeatherDecisionOutcome.NoImpact,
            WeatherEmergencyDecision.Evaluate(Alert(), departure, Start.AddHours(2)));
        Assert.Equal(WeatherDecisionOutcome.NoImpact,
            WeatherEmergencyDecision.Evaluate(Alert() with
            {
                Areas = [new WeatherPolygon([new(20m, 105m), new(21m, 105m),
                    new(21m, 106m), new(20m, 106m)])],
            }, Departure(), Start.AddHours(-1)));
    }

    [Fact]
    public void UnknownStopClassification_RequiresOperatorReview()
    {
        var result = WeatherEmergencyDecision.Evaluate(Alert(), Departure() with
        {
            RequiredStops = [new WeatherTourStop(DaNang, null, Start.AddHours(2), false)],
        }, Start.AddHours(1));

        Assert.Equal(WeatherDecisionOutcome.ReviewNeeded, result);
    }

    [Fact]
    public void PolygonHole_DoesNotMatchPointInsideHole()
    {
        var areaWithHole = Area with
        {
            Holes = [[new(15.5m, 107.5m), new(16.5m, 107.5m),
                new(16.5m, 108.5m), new(15.5m, 108.5m)]],
        };

        Assert.False(areaWithHole.Contains(DaNang));
    }

    private static WeatherAlertEvidence Alert() => new(
        "alert-1", true, Start.AddHours(-2), End, [Area],
        "Extreme", "Observed", "Immediate", null, false)
    { Source = WeatherAlertSource.GlobalWeatherAlerts };

    private static WeatherDepartureImpact Departure() => new(
        Start, End, DaNang, [], true);
}