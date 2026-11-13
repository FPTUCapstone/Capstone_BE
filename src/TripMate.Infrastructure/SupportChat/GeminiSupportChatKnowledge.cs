using System.Text.Json;
using System.Text.RegularExpressions;

using TripMate.Application.Features.SupportChat;

namespace TripMate.Infrastructure.SupportChat;

internal sealed class GeminiSupportChatKnowledge(GeminiSupportChatClient client) : ISupportChatKnowledge, IDisposable
{
    // Keeps 3,072-dimensional responses below the shared one-megabyte response bound.
    private const int EmbeddingBatchSize = 8;
    private readonly SemaphoreSlim initialization = new(1);
    private double[][]? corpusVectors;

    public async Task<IReadOnlyList<KnowledgePassage>> SearchAsync(string question, CancellationToken cancellationToken)
    {
        client.EnsureEnabled();
        // Only static corpus vectors are retained. Query vectors and user text are request-local.
        var documents = await GetCorpusVectorsAsync(cancellationToken);
        var query = await client.SendAsync(client.Options.EmbeddingModel, "embedContent", new
        {
            model = $"models/{client.Options.EmbeddingModel}",
            content = new { parts = new[] { new { text = question } } },
            taskType = "RETRIEVAL_QUERY"
        }, root => ReadVector(root.GetProperty("embedding")), cancellationToken);
        if (query.Length != documents[0].Length)
            throw new SupportChatUnavailableException();
        cancellationToken.ThrowIfCancellationRequested();

        var explicitRules = Regex.Matches(question, @"\b(?:BR|CR)-[0-9]{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Value.ToUpperInvariant()).Distinct().Take(3).ToArray();
        return documents.Select((vector, index) => new
        {
            Passage = SupportChatCorpus.Passages[index],
            Score = vector.Zip(query, (left, right) => left * right).Sum(),
            DirectRule = explicitRules.Any(id => SupportChatCorpus.Passages[index].Content.Contains($"{id} —", StringComparison.Ordinal)),
        })
            .Where(match => match.DirectRule || match.Score >= client.Options.SimilarityThreshold)
            .OrderByDescending(match => match.DirectRule)
            .ThenByDescending(match => match.Score)
            .Take(3)
            .Select(match => match.Passage)
            .ToArray();
    }

    private async Task<double[][]> GetCorpusVectorsAsync(CancellationToken cancellationToken)
    {
        await initialization.WaitAsync(cancellationToken);
        try
        {
            if (corpusVectors is not null)
                return corpusVectors;
            var allVectors = new List<double[]>();
            foreach (var batch in SupportChatCorpus.Passages.Chunk(EmbeddingBatchSize))
            {
                var vectors = await client.SendAsync(client.Options.EmbeddingModel, "batchEmbedContents", new
                {
                    requests = batch.Select(passage => new
                    {
                        model = $"models/{client.Options.EmbeddingModel}",
                        content = new { parts = new[] { new { text = passage.RetrievalText ?? passage.Content } } },
                        taskType = "RETRIEVAL_DOCUMENT",
                        title = passage.Source.Title
                    }).ToArray()
                }, root => ReadCorpusVectors(root, batch.Length), cancellationToken);
                allVectors.AddRange(vectors);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (allVectors.Any(vector => vector.Length != allVectors[0].Length))
                throw new SupportChatUnavailableException();
            // Publish only a complete, validated batch. Failures leave initialization retryable.
            corpusVectors = allVectors.ToArray();
            return corpusVectors;
        }
        finally
        {
            initialization.Release();
        }
    }

    private static double[][] ReadCorpusVectors(JsonElement root, int expectedCount)
    {
        var embeddings = root.GetProperty("embeddings");
        if (embeddings.GetArrayLength() != expectedCount)
            throw new SupportChatUnavailableException();
        var vectors = embeddings.EnumerateArray().Select(ReadVector).ToArray();
        if (vectors.Any(vector => vector.Length != vectors[0].Length))
            throw new SupportChatUnavailableException();
        return vectors;
    }

    private static double[] ReadVector(JsonElement embedding)
    {
        var vector = embedding.GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        var squaredNorm = vector.Sum(value => value * value);
        if (vector.Length == 0 || vector.Any(value => !double.IsFinite(value))
            || !double.IsFinite(squaredNorm) || squaredNorm <= 0)
            throw new SupportChatUnavailableException();
        var norm = Math.Sqrt(squaredNorm);
        return vector.Select(value => value / norm).ToArray();
    }

    public void Dispose() => initialization.Dispose();
}
