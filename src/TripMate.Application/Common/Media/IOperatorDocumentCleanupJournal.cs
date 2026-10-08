namespace TripMate.Application.Common.Media;

/// <summary>
/// Independently commits a recovery intent before any operator document reaches Cloudinary.
/// The intent remains durable if the registration transaction or the API process fails.
/// </summary>
public interface IOperatorDocumentCleanupJournal
{
    Task ReserveAsync(string publicId, string contentType, DateTimeOffset notBeforeAtUtc,
        CancellationToken cancellationToken);

    Task CompleteAsync(string publicId, CancellationToken cancellationToken);

    Task RetryNowAsync(string publicId, CancellationToken cancellationToken);
}

public static class OperatorDocumentReference
{
    public static Uri Create(string publicId, string contentType)
    {
        var (resourceType, format) = contentType.ToLowerInvariant() switch
        {
            "application/pdf" => ("raw", "pdf"),
            "image/jpeg" => ("image", "jpg"),
            "image/png" => ("image", "png"),
            _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
        };
        return new Uri($"cloudinary-operator://asset/{resourceType}/{format}/{Uri.EscapeDataString(publicId)}");
    }
}