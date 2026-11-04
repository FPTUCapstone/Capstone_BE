using FluentAssertions;

using TripMate.Application.Features.Weather.Emergency;
using TripMate.Infrastructure.Weather;

namespace TripMate.Application.UnitTests.Features.Weather.Processing;

public sealed class TomorrowAlertNormalizerTests
{
    private readonly TomorrowAlertNormalizer _normalizer = new();

    [Fact]
    public void Normalize_WithOfficialNchmfSeaBan_ExtractsAuthorityRiskAndDirective()
    {
        string payload = """
        {
            "events": [
                {
                    "id": "tomorrow-evt-001",
                    "event": {
                        "id": "official-storm-99",
                        "eventCategory": "Typhoon",
                        "start": "2026-11-03T06:00:00Z",
                        "end": "2026-11-04T18:00:00Z",
                        "updateTime": "2026-11-03T05:00:00Z",
                        "status": "Active",
                        "headline": "Cảnh báo bão khẩn cấp từ Trung tâm Dự báo Khí tượng Thủy văn Quốc gia NCHMF",
                        "description": "Rủi ro thiên tai cấp 4. Ban chỉ huy yêu cầu cấm biển tuyệt đối các tàu thuyền xuất bến.",
                        "severity": "Extreme",
                        "geometry": {
                            "type": "Polygon",
                            "coordinates": [
                                [
                                    [108.20, 16.05],
                                    [108.30, 16.05],
                                    [108.30, 16.15],
                                    [108.20, 16.15],
                                    [108.20, 16.05]
                                ]
                            ]
                        }
                    }
                }
            ]
        }
        """;

        var result = _normalizer.Normalize(payload, "TomorrowIo", "notif-001", DateTimeOffset.UtcNow);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        var evt = result.Value[0];

        evt.Provider.Should().Be("TomorrowIo");
        evt.ProviderEventId.Should().Be("official-storm-99");
        evt.OfficialVietnamRiskLevel.Should().Be(4);
        evt.VerifiedDirective.Should().Be(WeatherVerifiedDirective.TravelProhibition);
        evt.OriginalAuthority.Should().Contain("Trung tâm Dự báo Khí tượng Thủy văn Quốc gia");
        evt.IsWithdrawn.Should().BeFalse();
        evt.ProviderSeverity.Should().Be("Extreme");
        evt.Areas.Should().HaveCount(1);
        evt.Areas[0].Outer.Should().HaveCount(5);
    }

    [Fact]
    public void Normalize_WithProviderSeverityOnly_DoesNotAssignOfficialRiskLevelOrDirective()
    {
        string payload = """
        {
            "id": "evt-provider-severity",
            "start": "2026-11-03T08:00:00Z",
            "end": "2026-11-03T18:00:00Z",
            "severity": "Extreme",
            "description": "High thunderstorm probability based on Tomorrow.io forecast model.",
            "geometry": {
                "type": "Polygon",
                "coordinates": [
                    [[108.2, 16.0], [108.3, 16.0], [108.3, 16.1], [108.2, 16.1], [108.2, 16.0]]
                ]
            }
        }
        """;

        var result = _normalizer.Normalize(payload, "TomorrowIo", "notif-002", DateTimeOffset.UtcNow);

        result.IsSuccess.Should().BeTrue();
        var evt = result.Value[0];
        evt.OfficialVietnamRiskLevel.Should().BeNull("provider severity must never populate Vietnamese risk level");
        evt.VerifiedDirective.Should().Be(WeatherVerifiedDirective.None, "forecast severity is not a verified government directive");
        evt.ProviderSeverity.Should().Be("Extreme");
    }

    [Fact]
    public void Normalize_WithWithdrawnEvent_SetsIsWithdrawnTrue()
    {
        string payload = """
        {
            "id": "evt-cancelled-001",
            "status": "Withdrawn",
            "action": "CANCEL",
            "start": "2026-11-03T08:00:00Z",
            "end": "2026-11-03T18:00:00Z",
            "headline": "Bão đã tan, hủy bỏ cảnh báo.",
            "geometry": {
                "type": "Polygon",
                "coordinates": [
                    [[108.2, 16.0], [108.3, 16.0], [108.3, 16.1], [108.2, 16.1], [108.2, 16.0]]
                ]
            }
        }
        """;

        var result = _normalizer.Normalize(payload, "TomorrowIo", "notif-003", DateTimeOffset.UtcNow);

        result.IsSuccess.Should().BeTrue();
        result.Value[0].IsWithdrawn.Should().BeTrue();
    }

    [Fact]
    public void Normalize_WithCustomInsightVectorEvent_MarksAsCustomEvent()
    {
        string payload = """
        {
            "insightId": "custom-insight-user-rule-123",
            "feedId": "user-feed-danang",
            "id": "custom-evt-99",
            "start": "2026-11-03T08:00:00Z",
            "end": "2026-11-03T18:00:00Z",
            "geometry": {
                "type": "Polygon",
                "coordinates": [
                    [[108.2, 16.0], [108.3, 16.0], [108.3, 16.1], [108.2, 16.1], [108.2, 16.0]]
                ]
            }
        }
        """;

        var result = _normalizer.Normalize(payload, "TomorrowIo", "notif-004", DateTimeOffset.UtcNow);

        result.IsSuccess.Should().BeTrue();
        result.Value[0].IsCustomEvent.Should().BeTrue("events derived from custom insights must be flagged as custom");
        result.Value[0].VerifiedDirective.Should().Be(WeatherVerifiedDirective.None);
    }
}
