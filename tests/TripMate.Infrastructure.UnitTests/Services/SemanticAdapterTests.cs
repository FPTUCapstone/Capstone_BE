using System.Net;
using System.Text;

using FluentAssertions;

using TripMate.Application.Features.Tours.SemanticPoc;
using TripMate.Infrastructure.Services.SemanticPoc;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class SemanticAdapterTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"vector\":null,\"modelVersion\":\"wrong\"}")]
    [InlineData("{\"vector\":null,\"modelVersion\":\"intfloat/multilingual-e5-small@fd1525a9fd15316a2d503bf26ab031a61d056e98:mean-pool-l2-512-v1\"}")]
    public async Task Embedding_RejectsMalformedResponse(string json)
    {
        var adapter = new HttpEmbeddingService(Client(json));
        var action = () => adapter.EmbedAsync("biển", false, default);
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"result\":{\"points\":[{\"id\":\"invalid\",\"score\":0.5,\"payload\":{}}]}}")]
    public async Task Qdrant_RejectsMalformedResponse(string json)
    {
        var adapter = new QdrantTourVectorSearch(Client(json), "tm70_poc_test");
        var action = () => adapter.SearchAllAsync(new float[384], default);
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(384, "Dot")]
    [InlineData(768, "Cosine")]
    public async Task Qdrant_RejectsWrongCollectionContract(int size, string distance)
    {
        var config = System.Text.Json.JsonSerializer.Serialize(new { result = new { config = new { @params = new { vectors = new { size, distance } } } } });
        var client = new HttpClient(new Handler("{\"result\":{\"points\":[]}}", null, config)) { BaseAddress = new Uri("http://localhost/") };
        var action = () => new QdrantTourVectorSearch(client, "tm70_poc_test").SearchAllAsync(new float[384], default);
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Qdrant_UsesExactAndSentinelBoundAndReadsRealProtocol()
    {
        string? request = null;
        var handler = new Handler("{\"result\":{\"points\":[]}}", async value => request = await value.Content!.ReadAsStringAsync());
        var adapter = new QdrantTourVectorSearch(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, "tm70_poc_test");
        (await adapter.SearchAllAsync(new float[384], default)).Should().BeEmpty();
        request.Should().Contain("\"exact\":true").And.Contain("\"limit\":257");
    }

    private static HttpClient Client(string json) => new(new Handler(json)) { BaseAddress = new Uri("http://localhost/") };
    private sealed class Handler(string json, Func<HttpRequestMessage, Task>? inspect = null, string? config = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (inspect is not null && request.Method == HttpMethod.Post) await inspect(request);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Get ? config ?? "{\"result\":{\"config\":{\"params\":{\"vectors\":{\"size\":384,\"distance\":\"Cosine\"}}}}}" : json, Encoding.UTF8, "application/json") };
        }
    }
}