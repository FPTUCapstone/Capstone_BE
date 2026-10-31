using System.Text.Json;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tours.Search.V2;
using TripMate.Application.Features.Tours.SemanticPoc;

namespace TripMate.Application.Features.Tours.DevSearch;

public sealed record DevTourRow(SemanticTourDocument Document, TourSearchV2ItemDto Item,
    decimal? RawRating, int TitleOrder, int LexicalClass);
public sealed record DevTourSnapshot(IReadOnlyList<SemanticTourDocument> Corpus, IReadOnlyList<DevTourRow> Eligible);
public sealed class DevSearchCorpusTooLargeException : Exception;
public interface IDevTourCatalog
{
    Task<DevTourSnapshot> ReadAsync(TourSearchV2Request request, DateTimeOffset asOfUtc, CancellationToken cancellationToken);
}

/// <summary>Explicit development experiment. No production registration or fallback policy.</summary>
public sealed class DevTourSearch(IDevTourCatalog catalog, IEmbeddingService embedding,
    ITourVectorSearch vectors, IDateTimeProvider clock, int rrfK)
{
    public const string Lexical = "lexical";
    public const string Semantic = "semantic";
    public const string Hybrid = "hybrid";
    public static readonly string[] Modes = [Lexical, Semantic, Hybrid];

    public async Task<Result<PagedToursV2ResponseDto>> SearchAsync(TourSearchV2Request request, string mode, CancellationToken cancellationToken)
    {
        if (!Modes.Contains(mode, StringComparer.Ordinal) || rrfK is < 1 or > 1000
            || request.Page < 1 || request.PageSize is < 1 or > 100 || request.Keyword?.Length > 500)
            return Result.Failure<PagedToursV2ResponseDto>("DevSearch.InvalidInput", "Invalid development search criteria.");
        var asOf = clock.UtcNow.ToUniversalTime();
        try
        {
            var snapshot = await catalog.ReadAsync(request, asOf, cancellationToken);
            if (snapshot.Corpus.Count > SemanticModel.MaxCorpus)
                return Failure("CorpusTooLarge");
            var keyword = request.Keyword?.Normalize().Trim();
            IReadOnlyList<SemanticCandidate> points = [];
            if (!string.IsNullOrWhiteSpace(keyword) && mode != Lexical)
            {
                var vector = await embedding.EmbedAsync(keyword, false, cancellationToken);
                if (!SemanticModel.IsValidVector(vector)) return Failure("InvalidVector");
                points = await vectors.SearchAllAsync(vector, cancellationToken);
                if (points.Count > SemanticModel.MaxCorpus || points.Any(p => p.TourId <= 0 || !float.IsFinite(p.Score))
                    || points.Select(p => p.TourId).Distinct().Count() != points.Count)
                    return Failure("InvalidCandidates");
                // Re-read authoritative SQL after HTTP work, preserving the original asOfUtc.
                // Publication/content changes never inherit authorization from a vector payload.
                snapshot = await catalog.ReadAsync(request, asOf, cancellationToken);
                if (snapshot.Corpus.Count > SemanticModel.MaxCorpus) return Failure("CorpusTooLarge");
                var byId = points.ToDictionary(p => p.TourId);
                if (snapshot.Corpus.Any(d => !byId.TryGetValue(d.TourId, out var point)
                    || point.ModelVersion != SemanticModel.Version || point.Fingerprint != d.Fingerprint))
                    return Failure("IndexIncomplete");
            }
            var rows = snapshot.Eligible;
            var lexical = rows.Where(r => r.LexicalClass < 5).OrderBy(r => r.LexicalClass)
                .ThenBy(r => r.RawRating is null).ThenByDescending(r => r.RawRating).ThenBy(r => r.TitleOrder).ToArray();
            var scores = points.ToDictionary(p => p.TourId, p => (double)p.Score);
            if (string.IsNullOrWhiteSpace(keyword)) scores = rows.ToDictionary(r => r.Document.TourId, _ => 0d);
            IEnumerable<DevTourRow> selected = mode == Lexical && !string.IsNullOrWhiteSpace(keyword) ? lexical : rows;
            if (mode == Hybrid && !string.IsNullOrWhiteSpace(keyword))
            {
                var semantic = rows.OrderByDescending(r => scores[r.Document.TourId])
                    .ThenBy(r => r.RawRating is null).ThenByDescending(r => r.RawRating).ThenBy(r => r.TitleOrder).ToArray();
                scores = semantic.Select((r, i) => (r.Document.TourId, Score: 1d / (rrfK + i + 1)))
                    .ToDictionary(x => x.TourId, x => x.Score);
                for (var i = 0; i < lexical.Length; i++) scores[lexical[i].Document.TourId] += 1d / (rrfK + i + 1);
            }
            var sort = string.IsNullOrWhiteSpace(keyword) && request.Sort == TourSearchV2Request.SortRelevance
                ? TourSearchV2Request.SortDepartureDate : request.Sort;
            var ordered = sort switch
            {
                TourSearchV2Request.SortPriceAscending => selected.OrderBy(r => r.Item.BasePrice).ThenBy(r => r.TitleOrder),
                TourSearchV2Request.SortPriceDescending => selected.OrderByDescending(r => r.Item.BasePrice).ThenBy(r => r.TitleOrder),
                TourSearchV2Request.SortRating => selected.OrderBy(r => r.RawRating is null).ThenByDescending(r => r.RawRating).ThenBy(r => r.TitleOrder),
                TourSearchV2Request.SortRelevance when mode == Lexical => selected.OrderBy(r => r.LexicalClass)
                    .ThenBy(r => r.RawRating is null).ThenByDescending(r => r.RawRating).ThenBy(r => r.TitleOrder),
                TourSearchV2Request.SortRelevance => selected.OrderByDescending(r => scores[r.Document.TourId])
                    .ThenBy(r => r.RawRating is null).ThenByDescending(r => r.RawRating).ThenBy(r => r.TitleOrder),
                _ => selected.OrderBy(r => r.Item.DepartureAtUtc is null).ThenBy(r => r.Item.DepartureAtUtc).ThenBy(r => r.TitleOrder),
            };
            var all = ordered.ToArray();
            var offset = ((long)request.Page - 1) * request.PageSize;
            var items = offset >= all.Length ? [] : all.Skip((int)offset).Take(request.PageSize).Select(r => r.Item).ToArray();
            return Result.Success(new PagedToursV2ResponseDto(request.Page, request.PageSize, all.Length,
                (all.Length + request.PageSize - 1) / request.PageSize, asOf.UtcDateTime, items));
        }
        catch (DevSearchCorpusTooLargeException) { return Failure("CorpusTooLarge"); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Failure("DependencyUnavailable");
        }
    }

    private static Result<PagedToursV2ResponseDto> Failure(string code) =>
        Result.Failure<PagedToursV2ResponseDto>("DevSearch." + code, "Search is unavailable; no fallback or partial total was returned.");
}