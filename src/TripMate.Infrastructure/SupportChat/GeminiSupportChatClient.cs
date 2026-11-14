using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.SupportChat;

namespace TripMate.Infrastructure.SupportChat;

internal sealed class GeminiSupportChatClient : IDisposable
{
    internal const string HttpClientName = "SupportChat.Gemini";
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly IHttpClientFactory clientFactory;
    private readonly SemaphoreSlim concurrency;
    private readonly ILogger<GeminiSupportChatClient> logger;
    internal SupportChatOptions Options { get; }

    public GeminiSupportChatClient(IHttpClientFactory clientFactory, IOptions<SupportChatOptions> options,
        ILogger<GeminiSupportChatClient> logger)
    {
        this.clientFactory = clientFactory;
        this.logger = logger;
        Options = options.Value;
        concurrency = new SemaphoreSlim(Options.MaxConcurrency);
    }

    internal void EnsureEnabled()
    {
        if (!Options.Enabled)
            throw new SupportChatUnavailableException();
    }

    internal async Task<T> SendAsync<T>(string model, string action, object payload,
        Func<JsonElement, T> parse, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Options.TimeoutSeconds));
        var acquired = false;
        try
        {
            await concurrency.WaitAsync(deadline.Token);
            acquired = true;
            using var client = clientFactory.CreateClient(HttpClientName);
            using var response = await SendRequestAsync(client, model, action, payload, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Support chat provider {Action} returned HTTP {StatusCode}.", action, (int)response.StatusCode);
                throw new SupportChatUnavailableException();
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new SupportChatUnavailableException();

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, deadline.Token)) != 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    throw new SupportChatUnavailableException();
                buffer.Write(chunk, 0, count);
            }

            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            T result;
            try
            {
                result = parse(document.RootElement);
            }
            catch (SupportChatUnavailableException)
            {
                logger.LogWarning("Support chat provider response failed validation for {Action}.", action);
                throw;
            }
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SupportChatUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            // Discard provider bodies and exception details, which can contain credentials or user data.
            throw new SupportChatUnavailableException();
        }
        finally
        {
            if (acquired)
                concurrency.Release();
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpClient client, string model, string action,
        object payload, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(Options.Endpoint), $"models/{model}:{action}"));
            request.Headers.Add("x-goog-api-key", Options.ApiKey);
            request.Content = JsonContent.Create(payload);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable || attempt == 1)
                return response;
            response.Dispose();
            logger.LogWarning("Support chat provider {Action} temporarily unavailable; retrying once.", action);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        throw new SupportChatUnavailableException();
    }

    internal static JsonDocument ReadStructuredOutput(JsonElement root)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback)
            && feedback.TryGetProperty("blockReason", out var block)
            && block.GetString() != "BLOCK_REASON_UNSPECIFIED")
            throw new SupportChatUnavailableException();
        var candidates = root.GetProperty("candidates");
        if (candidates.GetArrayLength() != 1) throw new SupportChatUnavailableException();
        var candidate = candidates[0];
        if (candidate.GetProperty("finishReason").GetString() != "STOP")
            throw new SupportChatUnavailableException();
        if (candidate.TryGetProperty("safetyRatings", out var ratings)
            && ratings.EnumerateArray().Any(rating => rating.TryGetProperty("blocked", out var blocked) && blocked.GetBoolean()))
            throw new SupportChatUnavailableException();
        var parts = candidate.GetProperty("content").GetProperty("parts").EnumerateArray()
            .Where(part => !part.TryGetProperty("thought", out var thought) || !thought.GetBoolean()).ToArray();
        if (parts.Length != 1 || parts[0].TryGetProperty("functionCall", out _))
            throw new SupportChatUnavailableException();
        return JsonDocument.Parse(parts[0].GetProperty("text").GetString()!);
    }

    public void Dispose() => concurrency.Dispose();
}