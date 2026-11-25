using FluentAssertions;

using TripMate.Application.Features.Itineraries.WeatherTips;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class WeatherTipRulesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 25, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(40, 0, true)]
    [InlineData(39.9, 0, false)]
    [InlineData(0, 0.2, true)]
    [InlineData(0, 0.19, false)]
    public void Evaluate_Rain_UsesProbabilityOrIntensityThreshold(double probability, double rain, bool expected)
    {
        var (_, tips) = WeatherTipRules.Evaluate([Hour(probability: probability, rain: rain)]);

        tips.Any(tip => tip.Code == "weather_tip.rain").Should().Be(expected);
        if (expected)
        {
            tips.Single(tip => tip.Code == "weather_tip.rain").Should()
                .BeEquivalentTo(new WeatherTipDto("weather_tip.rain", "Info", "Mang ô hoặc áo mưa"));
        }
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(3.9, false)]
    public void Evaluate_Storm_AtFourMillimetresPerHour(double rain, bool expected)
    {
        var (_, tips) = WeatherTipRules.Evaluate([Hour(rain: rain)]);

        tips.Any(tip => tip.Code == "weather_tip.storm").Should().Be(expected);
    }

    [Fact]
    public void Evaluate_Thunderstorm_AddsStormWithWarning()
    {
        var (_, tips) = WeatherTipRules.Evaluate([Hour(thunderstorm: true)]);

        tips.Single(tip => tip.Code == "weather_tip.storm").Should().BeEquivalentTo(new WeatherTipDto(
            "weather_tip.storm", "Warning", "Có dông hoặc mưa to, cân nhắc đổi sang điểm trong nhà"));
    }

    [Theory]
    [InlineData(33, true)]
    [InlineData(32.9, false)]
    public void Evaluate_Heat_UsesFeltTemperature(double apparent, bool expected)
    {
        // The air temperature is mild on purpose: only the felt temperature may trigger the tip.
        var (_, tips) = WeatherTipRules.Evaluate([Hour(temperature: 28, apparent: apparent)]);

        tips.Any(tip => tip.Code == "weather_tip.heat").Should().Be(expected);
        if (expected)
        {
            tips.Single(tip => tip.Code == "weather_tip.heat").Text.Should().Be("Mang đủ nước uống và mũ nón");
        }
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(5.9, false)]
    public void Evaluate_Uv_AtSix(double uv, bool expected)
    {
        var (_, tips) = WeatherTipRules.Evaluate([Hour(uv: uv)]);

        tips.Any(tip => tip.Code == "weather_tip.uv").Should().Be(expected);
        if (expected)
        {
            tips.Single(tip => tip.Code == "weather_tip.uv").Text.Should().Be("Mang kem chống nắng và kính râm");
        }
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(20.1, false)]
    public void Evaluate_Cool_UsesAirTemperature(double temperature, bool expected)
    {
        // The felt temperature is mild on purpose: only the air temperature may trigger the tip.
        var (_, tips) = WeatherTipRules.Evaluate([Hour(temperature: temperature, apparent: 25)]);

        tips.Any(tip => tip.Code == "weather_tip.cool").Should().Be(expected);
        if (expected)
        {
            tips.Single(tip => tip.Code == "weather_tip.cool").Text.Should().Be("Mang áo khoác mỏng");
        }
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(9.9, false)]
    public void Evaluate_Wind_AtTenMetresPerSecond(double wind, bool expected)
    {
        var (_, tips) = WeatherTipRules.Evaluate([Hour(wind: wind)]);

        tips.Any(tip => tip.Code == "weather_tip.wind").Should().Be(expected);
        if (expected)
        {
            tips.Single(tip => tip.Code == "weather_tip.wind").Text.Should()
                .Be("Gió khá mạnh, chú ý khi ra biển hoặc điểm trên cao");
        }
    }

    [Fact]
    public void Evaluate_ReadsEveryHour_NotJustTheFirst()
    {
        var (_, tips) = WeatherTipRules.Evaluate([Hour(), Hour(probability: 80), Hour(uv: 9)]);

        tips.Select(tip => tip.Code).Should().BeEquivalentTo("weather_tip.rain", "weather_tip.uv");
    }

    [Fact]
    public void Evaluate_OrdersWarningFirst_AndCapsAtFiveTips()
    {
        var (_, tips) = WeatherTipRules.Evaluate([Hour(
            temperature: 18, apparent: 36, probability: 90, rain: 6, uv: 9, wind: 14, thunderstorm: true)]);

        tips.Should().HaveCount(5);
        tips.Select(tip => tip.Code).Should().Equal(
            "weather_tip.storm", "weather_tip.rain", "weather_tip.heat", "weather_tip.uv", "weather_tip.cool");
        tips[0].Severity.Should().Be("Warning");
        tips.Skip(1).Should().OnlyContain(tip => tip.Severity == "Info");
    }

    [Fact]
    public void Evaluate_SummaryRoundsMinMaxTemperatureAndTakesMaxima()
    {
        var (summary, _) = WeatherTipRules.Evaluate([
            Hour(temperature: 21.4, probability: 20, uv: 3),
            Hour(temperature: 30.6, probability: 59.6, uv: 7.5),
        ]);

        summary.Should().Be(new WeatherTipsSummaryDto(MinTempC: 21, MaxTempC: 31, MaxRainProbabilityPercent: 60, MaxUvIndex: 8));
    }

    [Fact]
    public void Evaluate_CalmDay_ReturnsEmptyTipsButASummary()
    {
        var (summary, tips) = WeatherTipRules.Evaluate([Hour(temperature: 26, apparent: 27, probability: 10, uv: 2, wind: 3)]);

        tips.Should().BeEmpty();
        summary.MinTempC.Should().Be(26);
    }

    [Fact]
    public void HoursCovering_StopEndingOnTheHour_ExcludesNextHour()
    {
        var arrival = new DateTimeOffset(2026, 11, 25, 7, 0, 0, TimeSpan.Zero);

        var hours = WeatherTipRules.HourStartsCovering(arrival, arrival.AddHours(1));

        hours.Should().Equal(arrival);
    }

    [Fact]
    public void HoursCovering_StopCrossingHours_ListsEach()
    {
        var arrival = new DateTimeOffset(2026, 11, 25, 6, 30, 0, TimeSpan.Zero);

        var hours = WeatherTipRules.HourStartsCovering(arrival, new DateTimeOffset(2026, 11, 25, 8, 10, 0, TimeSpan.Zero));

        hours.Should().Equal(
            new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 25, 7, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void HoursCovering_ZeroLengthStop_ReturnsItsHour()
    {
        var at = new DateTimeOffset(2026, 11, 25, 6, 45, 0, TimeSpan.Zero);

        var hours = WeatherTipRules.HourStartsCovering(at, at);

        hours.Should().Equal(new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void HoursCovering_NormalisesAnyOffsetToWholeUtcHours()
    {
        var arrival = new DateTimeOffset(2026, 11, 25, 13, 20, 0, TimeSpan.FromHours(7));

        var hours = WeatherTipRules.HourStartsCovering(arrival, arrival.AddMinutes(20));

        hours.Should().Equal(new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.Zero));
    }

    private static HourlyForecast Hour(
        double temperature = 25,
        double apparent = 25,
        double probability = 0,
        double rain = 0,
        double uv = 0,
        double wind = 0,
        bool thunderstorm = false) =>
        new(Noon, temperature, apparent, probability, rain, uv, wind, thunderstorm);
}