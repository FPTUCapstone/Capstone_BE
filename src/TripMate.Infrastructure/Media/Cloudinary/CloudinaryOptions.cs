using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Media.Cloudinary;

public sealed class CloudinaryOptions
{
    public const string SectionName = "Cloudinary";

    public string CloudName { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public string TourMediaFolderRoot { get; set; } = string.Empty;

    public string OperatorDocumentsFolderRoot { get; set; } = string.Empty;
}

public sealed class CloudinaryOptionsValidator : IValidateOptions<CloudinaryOptions>
{
    public ValidateOptionsResult Validate(string? name, CloudinaryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        AddRequiredFailure(options.CloudName, nameof(CloudinaryOptions.CloudName), failures);
        AddRequiredFailure(options.ApiKey, nameof(CloudinaryOptions.ApiKey), failures);
        AddRequiredFailure(options.ApiSecret, nameof(CloudinaryOptions.ApiSecret), failures);
        AddRequiredFailure(
            options.TourMediaFolderRoot,
            nameof(CloudinaryOptions.TourMediaFolderRoot),
            failures);

        AddRequiredFailure(
            options.OperatorDocumentsFolderRoot,
            nameof(CloudinaryOptions.OperatorDocumentsFolderRoot),
            failures);

        if (!string.IsNullOrWhiteSpace(options.TourMediaFolderRoot) &&
            !IsSafeFolderRoot(options.TourMediaFolderRoot))
        {
            failures.Add(
                $"{nameof(CloudinaryOptions.TourMediaFolderRoot)} must contain only safe " +
                "slash-separated folder segments without leading or trailing slashes.");
        }

        if (!string.IsNullOrWhiteSpace(options.OperatorDocumentsFolderRoot) &&
            !IsSafeFolderRoot(options.OperatorDocumentsFolderRoot))
        {
            failures.Add(
                $"{nameof(CloudinaryOptions.OperatorDocumentsFolderRoot)} must contain only safe " +
                "slash-separated folder segments without leading or trailing slashes.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsSafeFolderRoot(string value)
    {
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.StartsWith('/') ||
            value.EndsWith('/') ||
            value.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = value.Split('/');
        return segments.All(segment =>
            segment.Length > 0 &&
            segment is not "." and not ".." &&
            segment.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    private static void AddRequiredFailure(
        string value,
        string propertyName,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{propertyName} is required.");
        }
    }
}