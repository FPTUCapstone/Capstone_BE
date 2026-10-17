using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;

using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Tours.Search.V2;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourSearchV2ContractTests
{
    [Fact]
    public void Contract_RequiredNullablePropertiesSurviveNullOmissionSettings()
    {
        var item = new TourSearchV2ItemDto("1", "Tour", null, [], 1, 0, "VND", null,
            null, "noUpcomingSchedule", null, null, "Operator");
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(item, options));
        foreach (var property in new[] { "thumbnailUrl", "aggregateRating", "remainingSlots", "representativeScheduleId", "departureAtUtc" })
            document.RootElement.GetProperty(property).ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Theory]
    [InlineData(null, null, "departureDate")]
    [InlineData("  ", "relevance", "departureDate")]
    [InlineData("  Huế  ", null, "relevance")]
    [InlineData("Huế", "priceAsc", "priceAsc")]
    public void Parser_NormalizesTextAndDynamicSort(string? keyword, string? sort, string expected)
    {
        var values = new Dictionary<string, StringValues>();
        if (keyword is not null) values["keyword"] = keyword;
        if (sort is not null) values["sort"] = sort;
        var state = new ModelStateDictionary();
        TourSearchV2QueryParser.TryParse(new QueryCollection(values), state, out var request).Should().BeTrue();
        request.Sort.Should().Be(expected);
        request.Keyword.Should().Be(string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim());
        request.Page.Should().Be(1);
        request.PageSize.Should().Be(20);
    }

    [Theory]
    [InlineData("Keyword", "Hue")]
    [InlineData("departureDate", "2026-10-15")]
    [InlineData("minimumRating", "NaN")]
    [InlineData("minimumRating", "1e0")]
    [InlineData("minimumRating", "5.00000000000000000000000000001")]
    [InlineData("startDate", "2026-10-15T00:00:00Z")]
    [InlineData("minPrice", "1.0")]
    [InlineData("maxPrice", "+1")]
    [InlineData("durationDays", "")]
    [InlineData("page", "")]
    [InlineData("pageSize", "2147483648")]
    [InlineData("startDate", " ")]
    public void Parser_RejectsMalformedInputs(string key, string value)
    {
        var state = new ModelStateDictionary();
        TourSearchV2QueryParser.TryParse(new QueryCollection(new Dictionary<string, StringValues>
        {
            [key] = value,
        }), state, out _).Should().BeFalse();
        state.ErrorCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Parser_RejectsDuplicateAndMixedCaseKeys()
    {
        foreach (var values in new[]
        {
            new Dictionary<string, StringValues> { ["keyword"] = new StringValues(["Hue", "Da Nang"]) },
            new Dictionary<string, StringValues> { ["keyword"] = "Hue", ["Keyword"] = "Da Nang" },
        })
        {
            TourSearchV2QueryParser.TryParse(new QueryCollection(values), new ModelStateDictionary(), out _)
                .Should().BeFalse();
        }
    }

    [Fact]
    public void Parser_PreservesIndependentDatesAndNfc()
    {
        var state = new ModelStateDictionary();
        TourSearchV2QueryParser.TryParse(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["keyword"] = "  Huế".Normalize(System.Text.NormalizationForm.FormD),
            ["destination"] = "  Huế ",
            ["startDate"] = "2026-10-15",
            ["category"] = " ECO ",
            ["durationDays"] = "3",
            ["minimumRating"] = "4.25",
            ["minPrice"] = "9999999999",
        }), state, out var request).Should().BeTrue();
        request.Keyword.Should().Be("Huế");
        request.Destination.Should().Be("Huế");
        request.Category.Should().Be("ECO");
        request.StartDate.Should().Be(new DateOnly(2026, 10, 15));
        request.EndDate.Should().BeNull();
        request.MinimumRating.Should().Be(4.25m);
        request.MinPrice.Should().Be(9_999_999_999);
    }
}