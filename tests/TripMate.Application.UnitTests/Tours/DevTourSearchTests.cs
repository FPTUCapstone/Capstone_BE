using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.DevSearch;
using TripMate.Application.Features.Tours.Search.V2;
using TripMate.Application.Features.Tours.SemanticPoc;

namespace TripMate.Application.UnitTests.Tours;

public sealed class DevTourSearchTests
{
    [Fact]
    public async Task Hybrid_FusesIndependentRanks_WithoutDuplicatesOrScoreOverrideOfPriceSort()
    {
        var catalog = new Catalog(3);
        catalog.Rows[1] = catalog.Rows[1] with { LexicalClass = 5 };
        catalog.Rows[2] = catalog.Rows[2] with { LexicalClass = 5 };
        var vectors = new Vectors(catalog) { Defect = "reverse" };
        var service = new DevTourSearch(catalog, new Embedding(), vectors, new Clock(), 60);
        var semantic = await service.SearchAsync(new(Keyword: "hue", Sort: "relevance"), "semantic", default);
        var hybrid = await service.SearchAsync(new(Keyword: "hue", Sort: "relevance"), "hybrid", default);
        semantic.Value.Items[0].TourId.Should().Be("3");
        hybrid.Value.Items.Select(i => i.TourId).Should().Equal("1", "3", "2");
        var price = await service.SearchAsync(new(Keyword: "hue", Sort: "priceAsc"), "hybrid", default);
        price.Value.Items.Select(i => i.TourId).Should().Equal("1", "2", "3");
    }

    [Fact]
    public async Task BrowseAndEmptyFilters_DoNotNeedVectorDependencies_AndOversizedPageIsEmpty()
    {
        var catalog = new Catalog(3) { FilteredIds = [1L, 3L] };
        var service = new DevTourSearch(catalog, new BrokenEmbedding(), new Vectors(catalog), new Clock(), 60);
        var result = await service.SearchAsync(new(Page: int.MaxValue), "semantic", default);
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SqlChangesDuringInference_RecheckRejectsNewlyStalePoint()
    {
        var catalog = new Catalog(2) { ChangeOnSecondRead = true };
        var service = new DevTourSearch(catalog, new Embedding(), new Vectors(catalog), new Clock(), 60);
        var result = await service.SearchAsync(new(Keyword: "hue"), "semantic", default);
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("DevSearch.IndexIncomplete");
    }
    [Theory]
    [InlineData("semantic")]
    [InlineData("hybrid")]
    public async Task CompleteCoverage_ExactGlobalTotalBeforePage(string mode)
    {
        var catalog = new Catalog(3);
        var service = new DevTourSearch(catalog, new Embedding(), new Vectors(catalog), new Clock(), 60);
        var result = await service.SearchAsync(new(Keyword: "du lịch", Sort: "relevance", Page: 2, PageSize: 2), mode, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(3);
        result.Value.TotalPages.Should().Be(2);
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].TourId.Should().Be("3");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("model")]
    [InlineData("duplicate")]
    [InlineData("nonfinite")]
    public async Task IncompleteOrInvalidVectors_FailInsteadOfFabricatingTotal(string defect)
    {
        var catalog = new Catalog(3);
        var vectors = new Vectors(catalog) { Defect = defect };
        var service = new DevTourSearch(catalog, new Embedding(), vectors, new Clock(), 60);
        var result = await service.SearchAsync(new(Keyword: "biển"), "semantic", default);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ForeignHiddenPoint_CannotAuthorizeResult_AndRawRatingOrdersBeforePaging()
    {
        var catalog = new Catalog(2);
        catalog.Rows[0] = catalog.Rows[0] with { RawRating = 4.44m };
        catalog.Rows[1] = catalog.Rows[1] with { RawRating = 4.46m };
        var service = new DevTourSearch(catalog, new Embedding(), new Vectors(catalog) { Defect = "hidden" }, new Clock(), 60);
        var result = await service.SearchAsync(new(Keyword: "biển", Sort: "rating", PageSize: 1), "semantic", default);
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items[0].TourId.Should().Be("2");
    }

    [Fact]
    public async Task Lexical_UsesSqlMembership_AndDoesNotCallDependencies()
    {
        var catalog = new Catalog(3);
        catalog.Rows[2] = catalog.Rows[2] with { LexicalClass = 5 };
        var service = new DevTourSearch(catalog, new BrokenEmbedding(), new Vectors(catalog), new Clock(), 60);
        var result = await service.SearchAsync(new(Keyword: "hue", Sort: "relevance"), "lexical", default);
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task DependencyFailure_IsFailureWithoutLexicalFallback()
    {
        var catalog = new Catalog(2);
        var service = new DevTourSearch(catalog, new BrokenEmbedding(), new Vectors(catalog), new Clock(), 60);
        var result = await service.SearchAsync(new(Keyword: "hue"), "hybrid", default);
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("DevSearch.DependencyUnavailable");
    }

    [Fact]
    public async Task MoreThan256Corpus_FailsEvenWithSmallFilteredPage()
    {
        var catalog = new Catalog(257);
        var service = new DevTourSearch(catalog, new Embedding(), new Vectors(catalog), new Clock(), 60);
        var result = await service.SearchAsync(new(Keyword: "hue"), "semantic", default);
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("DevSearch.CorpusTooLarge");
    }

    private sealed class Clock : IDateTimeProvider { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-10-17T00:00:00Z"); }
    private sealed class Catalog : IDevTourCatalog
    {
        public List<DevTourRow> Rows { get; }
        public long[]? FilteredIds { get; init; }
        public bool ChangeOnSecondRead { get; init; }
        private int _reads;
        public Catalog(int count) => Rows = Enumerable.Range(1, count).Select(i => new DevTourRow(
            new(i, "Tour " + i, [], "Dev Travel", true),
            new(i.ToString(), "Tour " + i, null, [], 2, 100, "VND", null, null, "noUpcomingSchedule", null, null, "Dev Travel"),
            null, i, 0)).ToList();
        public Task<DevTourSnapshot> ReadAsync(TourSearchV2Request request, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
        {
            if (++_reads == 2 && ChangeOnSecondRead)
                Rows[0] = Rows[0] with { Document = Rows[0].Document with { Title = "Changed" } };
            return Task.FromResult(new DevTourSnapshot(Rows.Select(r => r.Document).ToArray(),
                Rows.Where(r => FilteredIds is null || FilteredIds.Contains(r.Document.TourId)).ToArray()));
        }
    }
    private sealed class Embedding : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, bool passage, CancellationToken cancellationToken)
            => Task.FromResult(new float[384].Select((_, i) => i == 0 ? 1f : 0f).ToArray());
    }
    private sealed class BrokenEmbedding : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, bool passage, CancellationToken cancellationToken) => throw new HttpRequestException();
    }
    private sealed class Vectors(Catalog catalog) : ITourVectorSearch
    {
        public string? Defect { get; init; }
        public Task<IReadOnlyList<SemanticCandidate>> SearchAllAsync(float[] vector, CancellationToken cancellationToken)
        {
            var list = catalog.Rows.Select(r => new SemanticCandidate(r.Document.TourId, 1, r.Document.Fingerprint, SemanticModel.Version)).ToList();
            if (Defect == "missing") list.RemoveAt(0);
            if (Defect == "stale") list[0] = list[0] with { Fingerprint = "stale" };
            if (Defect == "model") list[0] = list[0] with { ModelVersion = "old" };
            if (Defect == "duplicate") list.Add(list[0]);
            if (Defect == "nonfinite") list[0] = list[0] with { Score = float.NaN };
            if (Defect == "hidden") list.Add(new(999, 1, "hidden", SemanticModel.Version));
            if (Defect == "reverse") list = list.Select((p, i) => p with { Score = i / 3f }).ToList();
            return Task.FromResult<IReadOnlyList<SemanticCandidate>>(list);
        }
    }
}