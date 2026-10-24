using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.DevSearch;
using TripMate.Application.Features.Tours.Search.V2;
using TripMate.Application.Features.Tours.SemanticPoc;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class DevTourHttpTests
{
    private const string Key = "isolated-test-access-key-at-least-32-characters";
    [Theory]
    [InlineData("Production", true, Key, 404)]
    [InlineData("Development", false, Key, 404)]
    [InlineData("Development", true, "", 401)]
    [InlineData("Development", true, "wrong", 401)]
    [InlineData("Development", true, "duplicate", 401)]
    public async Task HttpGate_DeniesBeforeCatalogOrProviders(string env, bool enabled, string supplied, int status)
    {
        var catalog = new Catalog();
        await using var factory = Factory(env, enabled, catalog);
        using var client = factory.CreateClient();
        if (supplied.Length > 0) client.DefaultRequestHeaders.TryAddWithoutValidation("X-TM70-Dev-Key", supplied == "duplicate" ? new[] { Key, Key } : new[] { supplied });
        var result = await client.GetAsync("/api/dev/tours/search?keyword=beach&mode=hybrid");
        ((int)result.StatusCode).Should().Be(status);
        catalog.Reads.Should().Be(0);
    }

    [Fact]
    public async Task DependencyFailure_Produces503ProblemWithTrace_WithoutFallback()
    {
        var catalog = new Catalog();
        await using var factory = Factory("Development", true, catalog);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TM70-Dev-Key", Key);
        var result = await client.GetAsync("/api/dev/tours/search?keyword=beach&mode=hybrid");
        result.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await result.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("DevSearch.DependencyUnavailable");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        body.TryGetProperty("items", out _).Should().BeFalse();
    }
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(string env, bool enabled, Catalog catalog)
        => new TripMateApiFactory(environmentName: env, configureTestServices: services =>
            {
                services.RemoveAll<IDevTourCatalog>();
                services.AddScoped<IDevTourCatalog>(_ => catalog);
                services.AddScoped(_ => new DevTourSearch(catalog, new BrokenEmbedding(), new Vectors(), new Clock(), 60));
            })
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("DevTourSearch:Enabled", enabled.ToString());
                builder.UseSetting("DevTourSearch:Key", Key);
                builder.UseSetting("DevTourSearch:EmbeddingUrl", "http://127.0.0.1:8017");
                builder.UseSetting("DevTourSearch:QdrantUrl", "http://127.0.0.1:6337");
                builder.UseSetting("DevTourSearch:Collection", "tm70_poc_dev_unit_http");
            });
    private sealed class Catalog : IDevTourCatalog
    {
        public int Reads;
        public Task<DevTourSnapshot> ReadAsync(TourSearchV2Request request, DateTimeOffset now, CancellationToken ct)
        { Reads++; return Task.FromResult(new DevTourSnapshot([], [])); }
    }
    private sealed class BrokenEmbedding : IEmbeddingService
    { public Task<float[]> EmbedAsync(string text, bool passage, CancellationToken ct) => throw new HttpRequestException(); }
    private sealed class Vectors : ITourVectorSearch
    { public Task<IReadOnlyList<SemanticCandidate>> SearchAllAsync(float[] vector, CancellationToken ct) => throw new Exception("Unexpected vector call"); }
    private sealed class Clock : IDateTimeProvider { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
}