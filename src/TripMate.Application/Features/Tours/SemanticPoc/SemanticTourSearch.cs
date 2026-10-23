using System.Security.Cryptography;
using System.Text;


using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Tours.SemanticPoc;

public static class SemanticModel
{
    public const string Name = "intfloat/multilingual-e5-small";
    public const string Revision = "fd1525a9fd15316a2d503bf26ab031a61d056e98";
    public const string Version = Name + "@" + Revision + ":mean-pool-l2-512-v1";
    public const int Dimensions = 384;
    public const int MaxCorpus = 256;

    public static bool IsValidVector(float[]? vector) => vector is not null && vector.Length == Dimensions
        && vector.All(float.IsFinite)
        && Math.Abs(vector.Sum(x => (double)x * x) - 1) < 0.001;
}

public sealed record SemanticTourDocument(long TourId, string Title, string[] Destinations, string OperatorName, bool IsPublic)
{
    // UTF-8 byte lengths frame raw Unicode without cross-runtime JSON escaping differences.
    public string Text => "Title: " + Frame(Title.Normalize()) + "\nDestinations: "
        + string.Concat(Destinations.Select(x => x.Normalize()).Distinct(StringComparer.Ordinal)
            .OrderBy(x => Convert.ToHexString(Encoding.UTF8.GetBytes(x)), StringComparer.Ordinal).Select(Frame))
        + "\nOperator: " + Frame(OperatorName.Normalize());
    private static string Frame(string value) => Encoding.UTF8.GetByteCount(value).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}

public sealed record SemanticCandidate(long TourId, float Score, string Fingerprint, string ModelVersion);
public sealed record SemanticHit(long TourId, string Title, float Score);
public sealed record SemanticPage(IReadOnlyList<SemanticHit> Items, int TotalCount, int Page, int PageSize, string CountScope = "eligible-indexed-fixture-corpus", bool SqlUniverseCoverageUnknown = true);

public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, bool passage, CancellationToken cancellationToken);
}

public interface ITourVectorSearch
{
    // Must return every point in the isolated bounded collection, using exact cosine search.
    Task<IReadOnlyList<SemanticCandidate>> SearchAllAsync(float[] vector, CancellationToken cancellationToken);
}

public interface ISemanticTourAuthority
{
    Task<IReadOnlyList<SemanticTourDocument>> ResolveAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);
}

/// <summary>Explicit opt-in PoC only. Not registered with production endpoint or dependency injection.</summary>
public sealed class SemanticTourSearch(IEmbeddingService embedding, ITourVectorSearch vectors, ISemanticTourAuthority authority)
{
    public async Task<Result<SemanticPage>> SearchAsync(string? query, int page, int pageSize, CancellationToken cancellationToken)
    {
        string? normalized;
        try { normalized = query?.Normalize().Trim(); }
        catch (ArgumentException) { return Result.Failure<SemanticPage>("SemanticPoc.InvalidInput", "Query contains invalid Unicode."); }
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 500 || page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
        {
            return Result.Failure<SemanticPage>("SemanticPoc.InvalidInput", "Query and pagination are invalid.");
        }

        try
        {
            var vector = await embedding.EmbedAsync(normalized, false, cancellationToken);
            if (!SemanticModel.IsValidVector(vector))
            {
                return Result.Failure<SemanticPage>("SemanticPoc.InvalidVector", "Embedding response violates the pinned vector contract.");
            }

            var candidates = await vectors.SearchAllAsync(vector, cancellationToken);
            if (candidates.Count > SemanticModel.MaxCorpus)
            {
                return Result.Failure<SemanticPage>("SemanticPoc.CorpusTooLarge", "Collection exceeds the exhaustive PoC bound.");
            }

            if (candidates.Any(x => x.TourId <= 0 || !float.IsFinite(x.Score)) || candidates.Select(x => x.TourId).Distinct().Count() != candidates.Count)
            {
                return Result.Failure<SemanticPage>("SemanticPoc.InvalidCandidates", "Vector response contains invalid or duplicate candidates.");
            }

            var documents = await authority.ResolveAsync(candidates.Select(x => x.TourId).ToArray(), cancellationToken);
            var current = documents.Where(x => x.IsPublic).ToDictionary(x => x.TourId);
            var eligible = candidates.Where(x => x.ModelVersion == SemanticModel.Version && current.TryGetValue(x.TourId, out var doc) && doc.Fingerprint == x.Fingerprint)
                .Select(x => new SemanticHit(x.TourId, current[x.TourId].Title, x.Score))
                .OrderByDescending(x => x.Score).ThenBy(x => x.TourId).ToArray();
            return Result.Success(new SemanticPage(eligible.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), eligible.Length, page, pageSize));
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidDataException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return Result.Failure<SemanticPage>("SemanticPoc.DependencyUnavailable", "Semantic dependency unavailable or incompatible; no fallback applied.");
        }
    }
}