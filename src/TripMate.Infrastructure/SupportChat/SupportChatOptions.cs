using System.Text.RegularExpressions;

namespace TripMate.Infrastructure.SupportChat;

public sealed class SupportChatOptions
{
    public const string SectionName = "SupportChat";
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxConcurrency { get; set; } = 4;
    public double SimilarityThreshold { get; set; } = 0.55;

    internal bool IsValid() =>
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme == Uri.UriSchemeHttps
        && endpoint.Host == "generativelanguage.googleapis.com"
        && endpoint.IsDefaultPort
        && endpoint.AbsolutePath == "/v1beta/"
        && endpoint.UserInfo.Length == 0
        && endpoint.Query.Length == 0
        && endpoint.Fragment.Length == 0
        && TimeoutSeconds is >= 1 and <= 60
        && MaxConcurrency is >= 1 and <= 16
        && double.IsFinite(SimilarityThreshold)
        && SimilarityThreshold is >= 0 and <= 1
        && (!Enabled || (!string.IsNullOrWhiteSpace(ApiKey)
            && !ApiKey.Any(char.IsControl)
            && IsModelName(Model) && IsModelName(EmbeddingModel)));

    private static bool IsModelName(string? model) => model is not null
        && Regex.IsMatch(model, "\\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant);
}