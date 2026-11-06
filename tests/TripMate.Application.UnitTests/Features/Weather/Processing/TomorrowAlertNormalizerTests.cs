using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Weather.Emergency;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Weather;
using TripMate.Tests.Shared;

namespace TripMate.Application.UnitTests.Features.Weather.Processing;

public sealed class TomorrowAlertNormalizerTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private static TomorrowAlertNormalizer Create(string alertId = TomorrowAlertFixtures.DocsAlertId) =>
        new(Options.Create(new TomorrowIoOptions { AlertId = alertId }));

    private static IReadOnlyList<NormalizedWeatherEvent> Normalize(string payload, string alertId = TomorrowAlertFixtures.DocsAlertId)
    {
        var result = Create(alertId).Normalize(payload, "TomorrowIo", "delivery-1", Received);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        return result.Value;
    }

    [Fact]
    public void DocsCustomInsightPayload_BecomesOneCustomEventForItsLocation()
    {
        var evt = Normalize(TomorrowAlertFixtures.Webhook()).Should().ContainSingle().Subject;

        evt.Provider.Should().Be(WeatherProviders.TomorrowIo);
        evt.ProviderLocationIds.Should().Equal(TomorrowAlertFixtures.DocsLocationId);
        evt.ValidFromUtc.Should().Be(new DateTimeOffset(2020, 5, 10, 7, 49, 34, TimeSpan.Zero), "the docs write +0000 offsets");
        evt.ValidToUtc.Should().Be(evt.ValidFromUtc.AddHours(24));
        evt.IsCustomEvent.Should().BeTrue();
        evt.Category.Should().Be("CustomInsight");
        evt.Description.Should().StartWith("* WHAT... Visibility 1km or less");
        evt.VerifiedDirective.Should().Be(WeatherVerifiedDirective.None);
        evt.Areas.Should().BeEmpty();
        evt.ProviderEventId.Should().Be(
            $"{TomorrowAlertFixtures.DocsAlertId}:1589096974:{TomorrowAlertFixtures.DocsLocationId}");
    }

    [Theory]
    [InlineData("PRIOR")]
    [InlineData("START")]
    public void PriorAndStartOfOneOccurrence_ShareTheEventId(string type)
    {
        Normalize(TomorrowAlertFixtures.Webhook(type)).Single().ProviderEventId
            .Should().Be(Normalize(TomorrowAlertFixtures.Webhook("PRIOR")).Single().ProviderEventId);
    }

    [Fact]
    public void EndAllClear_ProducesNoEvidence()
    {
        Normalize(TomorrowAlertFixtures.EndWebhook()).Should().BeEmpty();
    }

    [Fact]
    public void MissingOptionalFields_AreTolerated()
    {
        var evt = Normalize(TomorrowAlertFixtures.MinimalWebhook("START")).Should().ContainSingle().Subject;

        evt.Description.Should().BeNull();
        evt.ProviderSeverity.Should().BeNull();
    }

    [Fact]
    public void NotificationOfAnotherAlert_IsIgnored()
    {
        Normalize(TomorrowAlertFixtures.Webhook(alertId: "someone-else")).Should().BeEmpty();
    }

    [Fact]
    public void WithoutAConfiguredAlert_NothingIsAccepted()
    {
        Normalize(TomorrowAlertFixtures.Webhook(), alertId: "").Should().BeEmpty();
    }

    [Fact]
    public void NotificationListItem_IsReadFromItsDocumentedShape()
    {
        var evt = Normalize(TomorrowAlertFixtures.ListItem()).Should().ContainSingle().Subject;

        evt.ProviderLocationIds.Should().Equal(TomorrowAlertFixtures.DocsLocationId);
        evt.ValidFromUtc.Should().Be(new DateTimeOffset(2020, 5, 10, 7, 49, 34, TimeSpan.Zero));
        evt.ProviderSeverity.Should().Be(WeatherEvent.SeverityLow, "the docs list severity \"minor\"");
        evt.ProviderEventId.Should().Be(Normalize(TomorrowAlertFixtures.Webhook("START")).Single().ProviderEventId);
    }

    [Fact]
    public void CategoryPublishWithAuthorityDirective_CarriesAreaRiskAndDirective()
    {
        string payload = """
            {"data":{"alert":{"id":"38f689d83c264eb0b084ba095f2ea743"},"insight":{"id":"floods"},
            "locations":[{"id":"a"},{"id":"b"}],
            "notification":{"type":"PUBLISH","title":"Flood Hazard","description":"A flood hazard has been published."},
            "event":{"startTime":"2026-11-03T00:00:00Z","endTime":"2026-11-04T00:00:00Z","updateTime":"2026-11-02T09:00:00Z",
              "severity":"severe","certainty":"likely","urgency":"expected","source":"NCHMF",
              "eventValues":{"origin":"NCHMF","title":"Bão số 9","description":"Rủi ro thiên tai cấp 4",
                "instruction":"Yêu cầu cấm biển từ 12h","response":"avoid",
                "location":{"type":"Polygon","coordinates":[[[108.0,15.9],[108.4,15.9],[108.4,16.2],[108.0,16.2],[108.0,15.9]]]}}}}}
            """;

        var evt = Normalize(payload).Should().ContainSingle().Subject;

        evt.IsCustomEvent.Should().BeFalse();
        evt.Category.Should().Be("floods");
        evt.OriginalAuthority.Should().Be("NCHMF");
        evt.OfficialVietnamRiskLevel.Should().Be(4);
        evt.VerifiedDirective.Should().Be(WeatherVerifiedDirective.TravelProhibition);
        evt.ProviderSeverity.Should().Be(WeatherEvent.SeveritySevere);
        evt.ValidToUtc.Should().Be(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero));
        evt.UpdatedAtUtc.Should().Be(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        evt.ProviderLocationIds.Should().Equal("a", "b");
        evt.Covers(new WeatherPoint(16.05m, 108.2m)).Should().BeTrue();
    }

    [Fact]
    public void CustomInsightText_NeverBecomesAVerifiedDirective()
    {
        string payload = TomorrowAlertFixtures.Webhook().Replace(
            "Low Visibility Advisory", "NCHMF cấm biển", StringComparison.Ordinal);

        Normalize(payload).Single().VerifiedDirective.Should().Be(WeatherVerifiedDirective.None);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"id":"legacy-self-designed","start":"2026-11-03T08:00:00Z"}""")]
    [InlineData("""{"data":{"alert":{"id":"38f689d83c264eb0b084ba095f2ea743"},"notification":{"type":"START"},"event":{}}}""")]
    public void PayloadOutsideTheContract_IsRejectedAsMalformed(string payload)
    {
        var result = Create().Normalize(payload, "TomorrowIo", "delivery-1", Received);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(WeatherAlertErrorCodes.PayloadMalformed);
    }

    [Fact]
    public void EmptyPayload_IsRejected()
    {
        Create().Normalize(" ", "TomorrowIo", null, Received).ErrorCode.Should().Be(WeatherAlertErrorCodes.PayloadEmpty);
    }
}