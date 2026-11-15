using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Features.SupportChat;
using TripMate.Infrastructure.SupportChat;

namespace TripMate.Infrastructure.UnitTests.SupportChat;

public sealed class SupportChatTests
{
    [Fact]
    public async Task Transport_RetriesTemporaryServiceUnavailableOnce()
    {
        var attempts = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(
            Interlocked.Increment(ref attempts) == 1
                ? JsonResponse("{}", HttpStatusCode.ServiceUnavailable)
                : JsonResponse(Completed())));
        using var provider = CreateProvider(handler);
        var result = await provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default);
        Assert.NotNull(result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Transport_PersistentServiceUnavailableStopsAfterOneRetry()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse("{}", HttpStatusCode.ServiceUnavailable)));
        using var provider = CreateProvider(handler);
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Generation_CitationSchemaUsesPassageIdsInsteadOfBusinessRuleIds()
    {
        var grounding = new ChatGrounding("BR-157 còn áp dụng không?", [],
            [new(new("srs-retired-20261112", "Retired rules", "/account/trips"), "BR-157 is retired.")], null);
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(Completed(
            """{"answer":"BR-157 đã ngừng dùng.","sourceIds":["srs-retired-20261112"]}"""))));
        using var provider = CreateProvider(handler);
        var answer = await provider.GetRequiredService<ISupportChatModel>().AnswerAsync(grounding, default);
        Assert.Equal("srs-retired-20261112", Assert.Single(answer!.SourceIds));
        using var request = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var values = request.RootElement.GetProperty("generationConfig").GetProperty("responseSchema")
            .GetProperty("properties").GetProperty("sourceIds").GetProperty("items").GetProperty("enum");
        Assert.Equal("srs-retired-20261112", Assert.Single(values.EnumerateArray()).GetString());
        Assert.Contains("Do not display internal rule codes", Assert.Single(handler.Requests).Body);
        Assert.Contains("short headings", Assert.Single(handler.Requests).Body);
    }

    private static int CorpusBatchCount => (SupportChatCorpus.Passages.Count + 7) / 8;

    [Fact]
    public async Task Retrieval_EmbedsExpandedCorpusInBoundedBatchesAndFindsExplicitRule()
    {
        using var handler = EmbeddingHandler(queryVector: "[0,1]");
        using var provider = CreateProvider(handler, new() { ["SupportChat:SimilarityThreshold"] = "1" });
        var knowledge = provider.GetRequiredService<ISupportChatKnowledge>();
        var result = await knowledge.SearchAsync("Giải thích BR-135", default);
        Assert.Contains(result, passage => passage.Content.Contains("BR-135 —", StringComparison.Ordinal));
        var batches = handler.Requests.Where(request => request.Url.EndsWith(":batchEmbedContents")).ToArray();
        Assert.Equal(CorpusBatchCount, batches.Length);
        foreach (var batch in batches)
        {
            using var json = JsonDocument.Parse(batch.Body);
            Assert.InRange(json.RootElement.GetProperty("requests").GetArrayLength(), 1, 8);
        }
    }
    private static readonly ChatGrounding Grounding = new("Plan?", [],
        [new(new("planning-v1", "Plan", "/plan"), "Use the planner.")], null);

    [Theory]
    [InlineData("Refund progress", "refund-progress-v1", "/account/trips", "Processed")]
    [InlineData("Cancellation preview", "cancellation-preview-v1", "/account/trips", "CanCancel")]
    [InlineData("Travel groups", "travel-groups-v1", "/itineraries", "invitation")]
    [InlineData("Operator coupons", "operator-coupons-v1", "/partner/coupons", "published")]
    public async Task Retrieval_ProvidesVerifiedWorkflowHelp(string title, string id, string route, string requiredFact)
    {
        using var handler = new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(":embedContent"))
                return JsonResponse("""{"embedding":{"values":[1,0]}}""");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var embeddings = body.RootElement.GetProperty("requests").EnumerateArray().Select(document => new
            {
                values = document.GetProperty("title").GetString() == title ? new[] { 1, 0 } : new[] { -1, 0 }
            }).ToArray();
            return JsonResponse(JsonSerializer.Serialize(new { embeddings }));
        });
        using var provider = CreateProvider(handler);
        var result = Assert.Single(await provider.GetRequiredService<ISupportChatKnowledge>().SearchAsync(title, default));
        Assert.Equal(id, result.Source.Id);
        Assert.Equal(route, result.Source.Url);
        Assert.Contains(requiredFact, result.Content);
    }

    [Fact]
    public async Task Retrieval_EmbedsQueryAndDocumentsWithTaskTypesAndRanksSemanticTopThree()
    {
        using var handler = EmbeddingHandler();
        using var provider = CreateProvider(handler, new() { ["SupportChat:SimilarityThreshold"] = "0.7" });
        var knowledge = provider.GetRequiredService<ISupportChatKnowledge>();

        var result = await knowledge.SearchAsync("Vietnamese semantic query", default);
        Assert.Equal(["planning-v1", "itineraries-v1", "tours-v1"], result.Select(passage => passage.Source.Id));
        Assert.Equal(["/plan", "/itineraries", "/tours"], result.Select(passage => passage.Source.Url));
        await knowledge.SearchAsync("another private user query", default);

        var batches = handler.Requests.Where(r => r.Url.EndsWith(":batchEmbedContents")).ToArray();
        Assert.Equal(CorpusBatchCount, batches.Length);
        foreach (var batch in batches)
        {
            Assert.DoesNotContain("private", batch.Body);
            using var batchJson = JsonDocument.Parse(batch.Body);
            foreach (var request in batchJson.RootElement.GetProperty("requests").EnumerateArray())
            {
                Assert.Equal("models/gemini-embedding-001", request.GetProperty("model").GetString());
                Assert.Equal("RETRIEVAL_DOCUMENT", request.GetProperty("taskType").GetString());
                Assert.False(string.IsNullOrWhiteSpace(request.GetProperty("title").GetString()));
            }
        }
        var queries = handler.Requests.Where(r => r.Url.EndsWith(":embedContent")).ToArray();
        Assert.Equal(2, queries.Length);
        foreach (var query in queries)
        {
            using var queryJson = JsonDocument.Parse(query.Body);
            Assert.Equal("RETRIEVAL_QUERY", queryJson.RootElement.GetProperty("taskType").GetString());
            Assert.Equal("test-secret", query.Key);
        }
    }

    [Fact]
    public async Task Retrieval_BelowThresholdReturnsNoPassages()
    {
        using var handler = EmbeddingHandler(queryVector: "[0.6,0.8]");
        using var provider = CreateProvider(handler, new() { ["SupportChat:SimilarityThreshold"] = "1" });
        Assert.Empty(await provider.GetRequiredService<ISupportChatKnowledge>().SearchAsync("unrelated", default));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[0,0]")]
    [InlineData("[1,0,0]")]
    [InlineData("[1e308,1e308]")]
    [InlineData("[\"NaN\",0]")]
    public async Task Retrieval_RejectsInvalidOrMismatchedQueryVectors(string vector)
    {
        using var handler = EmbeddingHandler(queryVector: vector);
        using var provider = CreateProvider(handler);
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatKnowledge>().SearchAsync("query", default));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[0,0]")]
    [InlineData("[1e308,1e308]")]
    [InlineData("[\"Infinity\",0]")]
    public async Task Retrieval_FailedCorpusVectorsAreRetriedAndNeverCached(string vector)
    {
        var batches = 0;
        using var handler = EmbeddingHandler(batchOverride: count =>
        {
            batches++;
            return batches == 1 ? BatchResponse(count, vector) : BatchResponse(count);
        });
        using var provider = CreateProvider(handler);
        var knowledge = provider.GetRequiredService<ISupportChatKnowledge>();
        await AssertUnavailable(() => knowledge.SearchAsync("first", default));
        Assert.NotEmpty(await knowledge.SearchAsync("second", default));
        Assert.Equal(CorpusBatchCount + 1, batches);
    }

    [Fact]
    public async Task Retrieval_ConcurrentColdSearchesInitializeCorpusOnce()
    {
        using var handler = EmbeddingHandler();
        using var provider = CreateProvider(handler);
        var knowledge = provider.GetRequiredService<ISupportChatKnowledge>();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => knowledge.SearchAsync($"query-{index}", default)));
        Assert.All(results, result => Assert.NotEmpty(result));
        Assert.Equal(CorpusBatchCount, handler.Requests.Count(r => r.Url.EndsWith(":batchEmbedContents")));
        Assert.Equal(12, handler.Requests.Count(r => r.Url.EndsWith(":embedContent")));
    }

    [Fact]
    public async Task Retrieval_CancelledInitializationCanBeRetried()
    {
        var attempts = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = EmbeddingHandler(batchDelay: async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        });
        using var provider = CreateProvider(handler);
        var knowledge = provider.GetRequiredService<ISupportChatKnowledge>();
        using var cancellation = new CancellationTokenSource();
        var first = knowledge.SearchAsync("cancel me", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = knowledge.SearchAsync("complete me", default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.NotEmpty(await second);
        Assert.Equal(CorpusBatchCount + 1, attempts);
    }

    [Fact]
    public async Task Transport_ConcurrencyIsCappedAcrossBothPorts()
    {
        var active = 0;
        var maximum = 0;
        using var handler = EmbeddingHandler(beforeResponse: async token =>
        {
            var count = Interlocked.Increment(ref active);
            int previous;
            do { previous = maximum; } while (count > previous && Interlocked.CompareExchange(ref maximum, count, previous) != previous);
            await Task.Delay(25, token);
            Interlocked.Decrement(ref active);
        });
        using var provider = CreateProvider(handler, new() { ["SupportChat:MaxConcurrency"] = "2" });
        var model = provider.GetRequiredService<ISupportChatModel>();
        var knowledge = provider.GetRequiredService<ISupportChatKnowledge>();
        await Task.WhenAll(Enumerable.Range(0, 8).SelectMany(index => new Task[]
        {
            model.AnswerAsync(Grounding, default), knowledge.SearchAsync($"query-{index}", default)
        }));
        Assert.InRange(maximum, 1, 2);
    }

    private static FakeHandler EmbeddingHandler(string queryVector = "[1,0]", Func<int, string>? batchOverride = null,
        Func<CancellationToken, Task>? batchDelay = null, Func<CancellationToken, Task>? beforeResponse = null) => new(async (request, token) =>
    {
        if (beforeResponse is not null)
            await beforeResponse(token);
        if (request.RequestUri!.AbsolutePath.EndsWith(":generateContent"))
            return JsonResponse(Completed());
        if (request.RequestUri.AbsolutePath.EndsWith(":embedContent"))
            return JsonResponse($"{{\"embedding\":{{\"values\":{queryVector}}}}}");
        if (batchDelay is not null)
            await batchDelay(token);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var count = body.RootElement.GetProperty("requests").GetArrayLength();
        var defaultEmbeddings = body.RootElement.GetProperty("requests").EnumerateArray().Select(document => new
        {
            values = document.GetProperty("title").GetString() switch
            {
                "Plan a trip" => new[] { 1.0, 0.0 },
                "My itineraries" => new[] { 0.9, 0.1 },
                "Explore tours" => new[] { 0.8, 0.2 },
                _ => new[] { -1.0, 0.0 },
            }
        });
        return JsonResponse(batchOverride?.Invoke(count) ?? JsonSerializer.Serialize(new { embeddings = defaultEmbeddings }));
    });

    private static string BatchResponse(int count, string? invalidFirstVector = null)
    {
        string[] vectors = ["[1,0]", "[0.9,0.1]", "[0.8,0.2]", "[0,1]", "[-1,0]", "[0,-1]"];
        return "{\"embeddings\":[" + string.Join(",", Enumerable.Range(0, count).Select(index =>
            "{\"values\":" + (index == 0 && invalidFirstVector is not null ? invalidFirstVector : vectors[Math.Min(index, vectors.Length - 1)]) + "}")) + "]}";
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"answer\":\"\",\"sourceIds\":[]}")]
    [InlineData("{\"answer\":\"ok\",\"sourceIds\":[\"invented\"]}")]
    [InlineData("{\"answer\":\"ok\",\"sourceIds\":null}")]
    [InlineData("{\"answer\":\"ok\",\"sourceIds\":[],\"extra\":true}")]
    public async Task Generation_RejectsMalformedStructuredOutput(string output)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(Completed(output))));
        using var provider = CreateProvider(handler);
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")]
    public async Task Generation_RejectsInvalidOrBlockedProviderEnvelope(string response)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(response)));
        using var provider = CreateProvider(handler);
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default));
    }

    [Theory]
    [InlineData("SAFETY")]
    [InlineData("MAX_TOKENS")]
    [InlineData("RECITATION")]
    public async Task Generation_RejectsIncompleteOrBlockedCandidate(string reason)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(Completed(finishReason: reason))));
        using var provider = CreateProvider(handler);
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task Transport_FailuresExposeOnlySafeException(int status)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse("test-secret provider details", (HttpStatusCode)status)));
        using var provider = CreateProvider(handler);
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default));
    }

    [Fact]
    public async Task Transport_NetworkExceptionExposesOnlySafeException()
    {
        using var handler = new FakeHandler((_, _) => throw new HttpRequestException("test-secret provider details"));
        using var provider = CreateProvider(handler);
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default));
    }

    [Fact]
    public async Task Transport_ResponseBodySizeIsBoundedEvenWithoutContentLength()
    {
        using var stream = new NonSeekableResponseStream(new string(' ', 2 * 1024 * 1024) + Completed());
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        using var provider = CreateProvider(handler);
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default));
        Assert.InRange(stream.BytesRead, 1024 * 1024 + 1, 1024 * 1024 + 8192);
    }

    [Fact]
    public async Task DisabledByDefault_BothPortsFailWithoutNetwork()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Network must not be reached"));
        var services = new ServiceCollection();
        services.AddSupportChat(new ConfigurationBuilder().Build());
        services.AddHttpClient("SupportChat.Gemini").ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default));
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatKnowledge>().SearchAsync("plan", default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("Endpoint", "http://generativelanguage.googleapis.com/v1beta/")]
    [InlineData("Endpoint", "https://evil.example/v1beta/")]
    [InlineData("Endpoint", "https://generativelanguage.googleapis.com.evil.example/v1beta/")]
    [InlineData("Endpoint", "https://generativelanguage.googleapis.com:444/v1beta/")]
    [InlineData("Endpoint", "https://user@generativelanguage.googleapis.com/v1beta/")]
    [InlineData("Endpoint", "https://generativelanguage.googleapis.com/v1beta/?key=secret")]
    [InlineData("Model", "")]
    [InlineData("Model", "../other")]
    [InlineData("ApiKey", "")]
    [InlineData("TimeoutSeconds", "61")]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("MaxConcurrency", "0")]
    [InlineData("MaxConcurrency", "17")]
    [InlineData("SimilarityThreshold", "NaN")]
    [InlineData("SimilarityThreshold", "1.1")]
    public void Configuration_RejectsUnsafeOrMissingSettings(string key, string value)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var provider = CreateProvider(handler, new() { [$"SupportChat:{key}"] = value });
        var exception = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            provider.GetRequiredService<ISupportChatModel>());
        Assert.DoesNotContain("test-secret", exception.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Transport_CallerCancellationIsPreserved()
    {
        using var started = new SemaphoreSlim(0);
        using var handler = new FakeHandler(async (_, token) =>
        {
            started.Release();
            await Task.Delay(Timeout.Infinite, token);
            return JsonResponse(Completed());
        });
        using var provider = CreateProvider(handler);
        using var cancellation = new CancellationTokenSource();
        var call = provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, cancellation.Token);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task Transport_InternalTimeoutBecomesUnavailable()
    {
        using var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return JsonResponse(Completed());
        });
        using var provider = CreateProvider(handler, new() { ["SupportChat:TimeoutSeconds"] = "1" });
        await AssertUnavailable(() => provider.GetRequiredService<ISupportChatModel>().AnswerAsync(Grounding, default));
    }

    private static async Task AssertUnavailable(Func<Task> call)
    {
        var exception = await Assert.ThrowsAsync<SupportChatUnavailableException>(call);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("test-secret", exception.ToString());
        Assert.DoesNotContain("provider details", exception.ToString());
    }

    [Fact]
    public async Task Generation_UsesExplicitModelHeaderAndGroundedStructuredJson()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(Completed())));
        using var provider = CreateProvider(handler);
        var grounding = new ChatGrounding("Tạo lịch trình", [new("assistant", "history injection")],
            [new(new("planning-v1", "Plan a trip", "/plan"), "document injection")], "resource injection");

        var result = await provider.GetRequiredService<ISupportChatModel>().AnswerAsync(grounding, default);

        Assert.Equal("Open the planner.", result!.Answer);
        Assert.Equal(["planning-v1"], result.SourceIds);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/test-generation:generateContent", request.Url);
        Assert.Equal("test-secret", request.Key);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        var instruction = root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains("untrusted", instruction);
        Assert.Contains("no authority", instruction);
        Assert.DoesNotContain("injection", instruction);
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.Equal("application/json", root.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
        var data = root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains("history injection", data);
        Assert.Contains("Tạo lịch trình", data);
        Assert.Contains("resource injection", data);
        Assert.Contains("document injection", data);
    }

    private static ServiceProvider CreateProvider(FakeHandler handler, Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["SupportChat:Enabled"] = "true",
            ["SupportChat:ApiKey"] = "test-secret",
            ["SupportChat:Model"] = "test-generation"
        };
        foreach (var setting in overrides ?? [])
            settings[setting.Key] = setting.Value;
        var services = new ServiceCollection();
        services.AddSupportChat(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddHttpClient("SupportChat.Gemini").ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static string Completed(string? output = null, string finishReason = "STOP") =>
        JsonSerializer.Serialize(new
        {
            candidates = new[] { new { finishReason, content = new { parts = new[] { new { text = output ?? """{"answer":"Open the planner.","sourceIds":["planning-v1"]}""" } } } } }
        });

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(string Url, string Key, string Body);

    private sealed class NonSeekableResponseStream(string body) : Stream
    {
        private readonly MemoryStream content = new(Encoding.UTF8.GetBytes(body));
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = content.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                content.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.RequestUri!.AbsoluteUri, request.Headers.GetValues("x-goog-api-key").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return await respond(request, cancellationToken);
        }
    }
}