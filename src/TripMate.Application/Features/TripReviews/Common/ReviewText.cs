namespace TripMate.Application.Features.TripReviews.Common;

/// <summary>Trim once at command construction. Screening and persistence must use this same immutable value.</summary>
public sealed record ReviewText
{
    private ReviewText(string title, string content) { Title = title; Content = content; }
    public string Title { get; }
    public string Content { get; }
    public static ReviewText Normalize(string? title, string? content) => new(title?.Trim() ?? string.Empty, content?.Trim() ?? string.Empty);
}