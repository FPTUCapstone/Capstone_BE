using FluentAssertions;

using Moq;

using TripMate.Application.Features.Tours.SemanticPoc;

namespace TripMate.Application.UnitTests.Tours;

public sealed class SemanticPocTests
{
    [Fact]
    public async Task Search_RejectsEmptyAndInvalidPagingBeforeCallingServices()
    {
        var embedding = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var search = new SemanticTourSearch(embedding.Object, Mock.Of<ITourVectorSearch>(), Mock.Of<ISemanticTourAuthority>());
        (await search.SearchAsync("  ", 1, 20, default)).ErrorCode.Should().Be("SemanticPoc.InvalidInput");
        (await search.SearchAsync("Đà Nẵng", int.MaxValue, 20, default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Search_FiltersStaleAndUnpublishedBeforePagingAndUsesStableTies()
    {
        var docs = Enumerable.Range(1, 25).Select(i => new SemanticTourDocument(i, $"Tour {i}", ["Đà Nẵng"], "Synthetic", true)).ToArray();
        var candidates = docs.Select(d => new SemanticCandidate(d.TourId, 0.8f, d.Fingerprint, SemanticModel.Version)).ToList();
        candidates[0] = candidates[0] with { Fingerprint = "stale" };
        docs[1] = docs[1] with { IsPublic = false };
        var embeddings = new Mock<IEmbeddingService>();
        embeddings.Setup(x => x.EmbedAsync("Đà Nẵng", false, It.IsAny<CancellationToken>())).ReturnsAsync(UnitVector());
        var vector = new Mock<ITourVectorSearch>();
        vector.Setup(x => x.SearchAllAsync(It.IsAny<float[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(candidates);
        var authority = new Mock<ISemanticTourAuthority>();
        authority.Setup(x => x.ResolveAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>())).ReturnsAsync(docs);
        var result = await new SemanticTourSearch(embeddings.Object, vector.Object, authority.Object).SearchAsync("  Đà Nẵng  ", 2, 20, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(23);
        result.Value.Items.Select(x => x.TourId).Should().Equal(23, 24, 25);
        authority.Verify(x => x.ResolveAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Search_RejectsOversizedCorpusAndDependencyFailure()
    {
        var embedding = new Mock<IEmbeddingService>();
        embedding.Setup(x => x.EmbedAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        var search = new SemanticTourSearch(embedding.Object, Mock.Of<ITourVectorSearch>(), Mock.Of<ISemanticTourAuthority>());
        (await search.SearchAsync("biển", 1, 20, default)).ErrorCode.Should().Be("SemanticPoc.DependencyUnavailable");
        embedding.Setup(x => x.EmbedAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(UnitVector());
        var vector = new Mock<ITourVectorSearch>();
        vector.Setup(x => x.SearchAllAsync(It.IsAny<float[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(Enumerable.Range(1, 257).Select(i => new SemanticCandidate(i, 1, "x", SemanticModel.Version)).ToArray());
        (await new SemanticTourSearch(embedding.Object, vector.Object, Mock.Of<ISemanticTourAuthority>()).SearchAsync("biển", 1, 20, default)).ErrorCode.Should().Be("SemanticPoc.CorpusTooLarge");
    }

    [Fact]
    public void Fingerprint_StableAcrossDestinationOrderAndChangesForPublicContent()
    {
        var doc = new SemanticTourDocument(1, "Tour", ["Huế", "Đà Nẵng"], "Operator", true);
        doc.Fingerprint.Should().Be((doc with { Destinations = ["Đà Nẵng", "Huế"] }).Fingerprint);
        doc.Fingerprint.Should().NotBe((doc with { Title = "Changed" }).Fingerprint);
        doc.Text.Should().NotContain("Description");
    }

    private static float[] UnitVector()
    {
        var vector = new float[384];
        vector[0] = 1;
        return vector;
    }
}