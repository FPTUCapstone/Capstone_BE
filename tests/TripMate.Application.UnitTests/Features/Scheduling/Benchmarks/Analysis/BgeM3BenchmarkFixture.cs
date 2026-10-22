using System.Security.Cryptography;
using System.Text.Json;

using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public sealed record BgeM3CanonicalFixtureDocument(
    string SchemaVersion,
    DateTimeOffset CaptureTimestamp,
    string ScenarioId,
    string ModelName,
    string EndpointMode,
    string Provenance,
    IReadOnlyList<BgeM3CanonicalPersonaEntry> Personas);

public sealed record BgeM3CanonicalPersonaEntry(
    string PersonaId,
    IReadOnlyList<string> PreferenceTokens,
    IReadOnlyDictionary<string, decimal> Scores);

public sealed record VerifiedBgeM3Fixture(
    BgeM3CanonicalFixtureDocument Document,
    string Sha256);

public static class BgeM3BenchmarkFixture
{
    public const string ModelName = "bge-m3:latest";
    public const string ScenarioId = "danang-real-1day";
    public const int PersonaCount = 5;
    public const int PoiCount = 40;
    public const string Provenance = "Captured from live TripMate AI /v1/rank-pois on 2026-10-21; configured/expected model bge-m3:latest; runtime model identity is not exposed by the provider contract; DefaultGeneralist scores use the production cold-start fallback, not a raw provider response";
    public const string ExpectedFixtureSha256 = "D50E09CC6900D908D93B62068BDE138EDB35DE12FB5B4241D3EB8110986BC4AC";

    public static string ResolveFixturePath()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current) && !File.Exists(Path.Combine(current, "TripMate.slnx")))
        {
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        var fixturePath = Path.Combine(current, "tests", "TripMate.Application.UnitTests", "Features", "Scheduling", "Benchmarks", "Analysis", "Fixtures", "bge-m3-danang-v3b-2026-10-21.json");
        if (File.Exists(fixturePath))
        {
            return fixturePath;
        }

        throw new FileNotFoundException($"Canonical BGE-M3 benchmark fixture not found at {fixturePath}");
    }

    public static VerifiedBgeM3Fixture LoadAndVerify(
        CityBenchmarkScenarioData cityData,
        string? fixturePath = null)
    {
        var path = fixturePath ?? ResolveFixturePath();
        var bytes = File.ReadAllBytes(path);
        var actualSha256 = ComputeCanonicalSha256(bytes);
        if (!string.Equals(actualSha256, ExpectedFixtureSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Canonical BGE-M3 fixture SHA-256 mismatch. Expected {ExpectedFixtureSha256}, got {actualSha256}.");
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var document = JsonSerializer.Deserialize<BgeM3CanonicalFixtureDocument>(bytes, options)
            ?? throw new InvalidOperationException("Failed to deserialize canonical BGE-M3 fixture.");

        BgeM3CanonicalFixtureValidator.Validate(cityData, document);
        return new VerifiedBgeM3Fixture(document, actualSha256);
    }

    private static string ComputeCanonicalSha256(ReadOnlySpan<byte> bytes)
    {
        var normalized = new byte[bytes.Length];
        var length = 0;

        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == '\r' && index + 1 < bytes.Length && bytes[index + 1] == '\n')
            {
                normalized[length++] = (byte)'\n';
                index++;
                continue;
            }

            normalized[length++] = bytes[index];
        }

        return Convert.ToHexString(SHA256.HashData(normalized.AsSpan(0, length)));
    }
}