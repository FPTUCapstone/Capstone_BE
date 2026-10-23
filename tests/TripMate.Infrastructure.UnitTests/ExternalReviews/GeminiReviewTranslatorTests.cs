using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.PointsOfInterest.ExternalReviews;
using TripMate.Infrastructure.ExternalReviews;

namespace TripMate.Infrastructure.UnitTests.ExternalReviews;

public sealed class GeminiReviewTranslatorTests
{
    private const string CompletedBody = """
        { "status": "completed", "steps": [ { "type": "model_output", "content": [
            { "type": "text", "text": "{\"title\":\"Không nên đến!\",\"text\":\"Bị người bán hàng rong làm phiền.\"}" } ] } ] }
        """;

    [Fact]
    public async Task TranslateAsync_ReturnsTheTranslatedTitleAndText_WithTheKeyHeader()
    {
        var handler = new StubHandler(CompletedBody);
        var translator = Create(handler);

        var result = await translator.TranslateAsync("454980:1", "NO go!", "Accosted by hawkers.", "vi", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Không nên đến!");
        result.Value.Text.Should().Be("Bị người bán hàng rong làm phiền.");
        handler.LastRequest!.Headers.GetValues("x-goog-api-key").Should().Equal("test-key");
        handler.LastBody.Should().Contain("gemini-3.5-flash-lite").And.Contain("Accosted by hawkers.");
    }

    [Fact]
    public async Task TranslateAsync_SameReviewTwice_CallsGeminiOnce()
    {
        var handler = new StubHandler(CompletedBody);
        var translator = Create(handler);

        await translator.TranslateAsync("454980:1", "NO go!", "text", "vi", CancellationToken.None);
        await translator.TranslateAsync("454980:1", "NO go!", "text", "vi", CancellationToken.None);

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task TranslateAsync_WhenDisabled_FailsWithoutCallingGemini()
    {
        var handler = new StubHandler(CompletedBody);
        var translator = Create(handler, enabled: false);

        var result = await translator.TranslateAsync("1:1", "a", "b", "vi", CancellationToken.None);

        result.ErrorCode.Should().Be(ExternalReviewErrorCodes.TranslationUnavailable);
        handler.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("""{ "status": "failed", "steps": [] }""")]
    [InlineData("""{ "status": "completed", "steps": [ { "type": "model_output", "content": [ { "type": "text", "text": "not json" } ] } ] }""")]
    [InlineData("not even json")]
    [InlineData("""{ "status": "in_progress", "steps": [ { "type": "model_output", "content": [ { "type": "text", "text": "{\"title\":\"a\",\"text\":\"b\"}" } ] } ] }""")]
    public void Parse_UnexpectedResponse_ReturnsNull(string body)
    {
        GeminiReviewTranslator.Parse(body).Should().BeNull();
    }

    private static GeminiReviewTranslator Create(StubHandler handler, bool enabled = true) =>
        new(
            new HttpClient(handler),
            Options.Create(new ReviewTranslationOptions
            {
                Enabled = enabled,
                ApiKey = "test-key",
                Endpoint = "https://gemini.example/v1/interactions",
            }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<GeminiReviewTranslator>.Instance);

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}