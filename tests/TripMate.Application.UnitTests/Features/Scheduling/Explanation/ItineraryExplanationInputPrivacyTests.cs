using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Explanation;

public sealed class ItineraryExplanationInputPrivacyTests
{
    private static readonly DateTimeOffset StartAtUtc =
        new(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithMetadata_UsesMetadataAndNormalizesTagsAndPreferences()
    {
        GeneratedItineraryPlan plan = CreatePlan(CreateVisit());
        IReadOnlyDictionary<long, ExplanationPoiMetadata> metadata =
            new Dictionary<long, ExplanationPoiMetadata>
            {
                [42] = new(
                    "  Bảo tàng Đà Lạt  ",
                    "  Văn hóa  ",
                    ["  Nature ", "culture", "NATURE", "", " cülture "]),
            };

        var input = ItineraryExplanationInput.Create(
            plan,
            metadata,
            ["  Beach ", "beach", "Ẩm thực", ""],
            StartAtUtc,
            "Asia/Ho_Chi_Minh");

        var item = input.Items.Should().ContainSingle().Subject;
        item.PoiName.Should().Be("Bảo tàng Đà Lạt");
        item.CategoryName.Should().Be("Văn hóa");
        item.TagNames.Should().Equal("culture", "Nature");
        input.Context.PreferenceTokens.Should().Equal("am thuc", "beach");
        input.Context.StartAtUtc.Should().Be(StartAtUtc);
        input.Context.TimeZoneId.Should().Be("Asia/Ho_Chi_Minh");
        input.Context.TotalDurationMinutes.Should().Be(plan.TotalDurationMinutes);
    }

    [Fact]
    public void Create_WithoutMetadata_UsesNullCategoryAndEmptyTagsWithoutFailing()
    {
        GeneratedItineraryPlan plan = CreatePlan(CreateVisit());

        var action = () => ItineraryExplanationInput.Create(
            plan,
            new Dictionary<long, ExplanationPoiMetadata>(),
            [],
            StartAtUtc,
            "Asia/Ho_Chi_Minh");

        var input = action.Should().NotThrow().Which;
        var item = input.Items.Should().ContainSingle().Subject;
        item.PoiName.Should().BeNull();
        item.CategoryName.Should().BeNull();
        item.TagNames.Should().BeEmpty();
    }

    [Fact]
    public void Create_ForPoiLessRest_UsesNullAndEmptyDescriptiveMetadata()
    {
        var rest = new GeneratedItineraryItem(
            1,
            null,
            null,
            ItineraryItemKind.Rest,
            StartAtUtc,
            StartAtUtc.AddMinutes(30),
            false,
            null,
            "Free rest time");

        var input = ItineraryExplanationInput.Create(
            CreatePlan(rest),
            new Dictionary<long, ExplanationPoiMetadata>(),
            [],
            StartAtUtc,
            "Asia/Ho_Chi_Minh");

        var item = input.Items.Should().ContainSingle().Subject;
        item.PoiId.Should().BeNull();
        item.PoiName.Should().BeNull();
        item.CategoryName.Should().BeNull();
        item.TagNames.Should().BeEmpty();
    }

    [Fact]
    public void SerializedInput_ExcludesIdentityCoordinatesScoresBehaviorAndAuthMaterial()
    {
        var input = ItineraryExplanationInput.Create(
            CreatePlan(CreateVisit()),
            new Dictionary<long, ExplanationPoiMetadata>
            {
                [42] = new("Bảo tàng Đà Lạt", "Văn hóa", ["Lịch sử"]),
            },
            ["culture"],
            StartAtUtc,
            "Asia/Ho_Chi_Minh");
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(input));
        var propertyNames = ReadPropertyNames(document.RootElement).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var forbidden = new[]
        {
            "TravelerId",
            "TravelerUserId",
            "Email",
            "AccountId",
            "IdempotencyKey",
            "RequestHash",
            "Latitude",
            "Longitude",
            "BaseScore",
            "TripMateBaseScore",
            "AiScore",
            "EffectiveDesirabilityScore",
            "PreferenceScore",
            "ScenicQuality",
            "ScenicScore",
            "PhotoQuality",
            "PhotoRating",
            "EstimatedVisitCostForRanking",
            "BehaviorAggregates",
            "ApiKey",
            "Authorization",
            "AccessToken",
        };

        propertyNames.Should().NotContain(forbidden);
    }

    private static GeneratedItineraryItem CreateVisit() => new(
        1,
        42,
        "Plan POI name",
        ItineraryItemKind.Visit,
        StartAtUtc.AddMinutes(10),
        StartAtUtc.AddMinutes(70),
        false,
        50_000m,
        "Suggested nearby location",
        15);

    private static GeneratedItineraryPlan CreatePlan(GeneratedItineraryItem item) => new(
        [item],
        item.PlannedDepartureUtc.AddMinutes(item.TravelDurationToNextMinutes ?? 0),
        85,
        item.EstimatedCost ?? 0m);

    private static IEnumerable<string> ReadPropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (string nestedName in ReadPropertyNames(property.Value))
                {
                    yield return nestedName;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                foreach (string nestedName in ReadPropertyNames(item))
                {
                    yield return nestedName;
                }
            }
        }
    }
}