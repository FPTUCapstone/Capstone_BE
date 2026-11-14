using System.Reflection;

using FluentAssertions;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public sealed class PoiRankingContractsTests
{
    [Fact]
    public void Contracts_WithSemanticData_PreserveTheirValues()
    {
        IReadOnlyCollection<string> preferenceTokens = ["culture", "nature"];
        IReadOnlyCollection<string> tagNames = ["museum", "heritage"];
        var context = new PoiRankingContext(preferenceTokens);
        var candidate = new PoiRankingCandidate(17L, "Museum", "Culture", tagNames);
        var request = new PoiRankingRequest([candidate], context);
        var item = new PoiRankingItem(17L, 0.875m, "Strong semantic match");
        var result = new PoiRankingResult([item]);

        request.Context.PreferenceTokens.Should().BeSameAs(preferenceTokens);
        request.Candidates.Should().ContainSingle().Which.Should().Be(candidate);
        candidate.TagNames.Should().BeSameAs(tagNames);
        result.Ranked.Should().ContainSingle().Which.Should().Be(item);
        item.AiScore.Should().Be(0.875m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Optional provider reason")]
    public void PoiRankingItem_Reason_SupportsOptionalMetadata(string? reason)
    {
        var item = new PoiRankingItem(17L, 0.5m, reason);

        item.Reason.Should().Be(reason);
    }

    [Fact]
    public void ProviderInterface_ExposesApprovedAsyncSignature()
    {
        MethodInfo method = typeof(IPoiRankingProvider)
            .GetMethod(nameof(IPoiRankingProvider.RankAsync))!;

        method.ReturnType.Should().Be<Task<Result<PoiRankingResult>>>();
        method.GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(
            typeof(PoiRankingRequest),
            typeof(CancellationToken));
    }

    [Fact]
    public void ProviderRequestContract_ExposesOnlyApprovedSemanticData()
    {
        PublicPropertyNames<PoiRankingCandidate>().Should().BeEquivalentTo(
            nameof(PoiRankingCandidate.PoiId),
            nameof(PoiRankingCandidate.Name),
            nameof(PoiRankingCandidate.CategoryName),
            nameof(PoiRankingCandidate.TagNames));
        // UC-10 item 6 approved one addition: the behavior profile. It describes POIs (the same semantic fields
        // as a candidate) with a weight; it never carries the traveler's identity, events, dates or scores.
        PublicPropertyNames<PoiRankingContext>().Should().BeEquivalentTo(
            nameof(PoiRankingContext.PreferenceTokens),
            nameof(PoiRankingContext.Behavior));
        PublicPropertyNames<PoiRankingBehavior>().Should().BeEquivalentTo(
            nameof(PoiRankingBehavior.Liked),
            nameof(PoiRankingBehavior.Disliked));
        PublicPropertyNames<PoiRankingBehaviorPoi>().Should().BeEquivalentTo(
            nameof(PoiRankingBehaviorPoi.PoiId),
            nameof(PoiRankingBehaviorPoi.Name),
            nameof(PoiRankingBehaviorPoi.CategoryName),
            nameof(PoiRankingBehaviorPoi.TagNames),
            nameof(PoiRankingBehaviorPoi.Weight));
        PublicPropertyNames<PoiRankingRequest>().Should().BeEquivalentTo(
            nameof(PoiRankingRequest.Candidates),
            nameof(PoiRankingRequest.Context));
    }

    [Fact]
    public void Context_WithoutABehaviorProfile_IsTheContractFromBeforeItem6()
    {
        var context = new PoiRankingContext(["culture"]);

        context.Behavior.Should().BeNull();
    }

    private static string[] PublicPropertyNames<T>() =>
        typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
}