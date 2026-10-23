using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiRanking;

namespace TripMate.Infrastructure.TripMateAi;

/// <summary>
/// Xếp hạng POI qua service TripMate AI (POST /v1/rank-pois), thay cho Gemini khi
/// <c>AiRanking:Provider = TripMateAi</c>. Cùng hợp đồng <see cref="IPoiRankingProvider"/>, cùng mã lỗi,
/// cùng giới hạn tốc độ cục bộ (<see cref="IAiProviderBudget"/>) như <see cref="HttpPoiRankingProvider"/>.
/// </summary>
public sealed class TripMateAiPoiRankingProvider(
    HttpClient httpClient,
    IOptions<PoiRankingProviderOptions> options,
    IAiProviderBudget? budget = null) : IPoiRankingProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly PoiRankingProviderOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));
    private readonly IAiProviderBudget? _budget = budget;

    public async Task<Result<PoiRankingResult>> RankAsync(
        PoiRankingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = new RankingRequestBody(
            request.Context.PreferenceTokens.ToArray(),
            request.Candidates.Select(candidate => new RankingCandidateBody(
                candidate.PoiId,
                candidate.Name,
                candidate.CategoryName,
                candidate.TagNames.ToArray())).ToArray());

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        httpRequest.Headers.Add(TripMateAiHeaders.ApiKey, _options.ApiKey);

        IDisposable? permit = null;
        if (_budget is not null && !_budget.TryAcquire(AiProviderNames.Ranking, out permit))
        {
            return Result.Failure<PoiRankingResult>(
                PoiRankingProviderErrorCodes.Quota,
                "The local POI ranking provider budget is unavailable.");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return NetworkFailure();
        }
        catch (IOException)
        {
            return NetworkFailure();
        }
        finally
        {
            permit?.Dispose();
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return MapHttpFailure(response.StatusCode);
            }

            RankingResponseBody? body;
            try
            {
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                body = JsonSerializer.Deserialize<RankingResponseBody>(content, SerializerOptions);
            }
            catch (HttpRequestException)
            {
                return NetworkFailure();
            }
            catch (IOException)
            {
                return NetworkFailure();
            }
            catch (JsonException)
            {
                return InvalidResponseFailure();
            }

            if (body?.Ranked is null
                || body.Ranked.Any(item => item is null || item.PoiId is null || item.AiScore is null))
            {
                return InvalidResponseFailure();
            }

            // Kiểm tra đủ POI, không trùng và aiScore trong [0, 1] do PoiRankingOrchestrator đảm nhận,
            // giống hệt với kết quả từ Gemini.
            return Result.Success(new PoiRankingResult(
                body.Ranked
                    .Select(item => new PoiRankingItem(item!.PoiId!.Value, item.AiScore!.Value, item.Reason))
                    .ToArray()));
        }
    }

    private static Result<PoiRankingResult> NetworkFailure() =>
        Result.Failure<PoiRankingResult>(
            PoiRankingProviderErrorCodes.Network,
            "The POI ranking provider could not be reached.");

    private static Result<PoiRankingResult> InvalidResponseFailure() =>
        Result.Failure<PoiRankingResult>(
            PoiRankingProviderErrorCodes.InvalidResponse,
            "The POI ranking provider returned an invalid response.");

    private static Result<PoiRankingResult> MapHttpFailure(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return Result.Failure<PoiRankingResult>(
                PoiRankingProviderErrorCodes.Quota,
                "The POI ranking provider quota was exceeded.");
        }

        int numericStatus = (int)statusCode;
        if (numericStatus is >= 500 and <= 599)
        {
            return Result.Failure<PoiRankingResult>(
                PoiRankingProviderErrorCodes.ServerError,
                "The POI ranking provider returned a server error.");
        }

        return Result.Failure<PoiRankingResult>(
            PoiRankingProviderErrorCodes.InvalidResponse,
            "The POI ranking provider rejected the request.");
    }

    private sealed record RankingRequestBody(
        IReadOnlyCollection<string> PreferenceTokens,
        IReadOnlyCollection<RankingCandidateBody> Candidates);

    private sealed record RankingCandidateBody(
        long PoiId,
        string Name,
        string CategoryName,
        IReadOnlyCollection<string> TagNames);

    private sealed class RankingResponseBody
    {
        public IReadOnlyCollection<RankingResponseItem?>? Ranked { get; init; }
    }

    private sealed class RankingResponseItem
    {
        public long? PoiId { get; init; }

        public decimal? AiScore { get; init; }

        public string? Reason { get; init; }
    }
}