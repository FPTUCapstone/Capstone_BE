namespace TripMate.Domain.Entities;

public sealed class TripReviewMediaOperation
{
    public const string Reserved = "Reserved", Uploaded = "Uploaded", Adopted = "Adopted", CleanupPending = "CleanupPending", Cleaned = "Cleaned";
    public const long MaximumInputBytes = 5_000_000, MaximumPixels = 24_000_000;
    public Guid Id { get; private set; }
    public Guid BatchId { get; private set; }
    public long? BookingId { get; private set; }
    public long? ServiceBookingId { get; private set; }
    public long TravelerUserId { get; private set; }
    public byte SortOrder { get; private set; }
    public string PublicId { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public string Extension { get; private set; } = string.Empty;
    public long InputByteLength { get; private set; }
    public long? StoredByteLength { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? DeliveryUrl { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? UploadedAtUtc { get; private set; }
    public DateTimeOffset? AdoptedAtUtc { get; private set; }
    public DateTimeOffset? CleanedAtUtc { get; private set; }
    public byte[] Version { get; private set; } = [];
    private TripReviewMediaOperation() { }
    public static TripReviewMediaOperation Reserve(Guid id, Guid batch, long booking, long traveler, byte slot, string publicId, string mime, string extension, long bytes, int width, int height, DateTimeOffset now)
        => ReserveForParent(id, batch, booking, null, traveler, slot, publicId, mime,
            extension, bytes, width, height, now);

    public static TripReviewMediaOperation ReserveForService(Guid id, Guid batch,
        long serviceBooking, long traveler, byte slot, string publicId, string mime,
        string extension, long bytes, int width, int height, DateTimeOffset now)
        => ReserveForParent(id, batch, null, serviceBooking, traveler, slot, publicId,
            mime, extension, bytes, width, height, now);

    private static TripReviewMediaOperation ReserveForParent(Guid id, Guid batch,
        long? booking, long? serviceBooking, long traveler, byte slot, string publicId,
        string mime, string extension, long bytes, int width, int height, DateTimeOffset now)
    {
        var validParent = (booking is > 0 && serviceBooking is null)
            || (booking is null && serviceBooking is > 0);
        if (id == Guid.Empty || batch == Guid.Empty || !validParent || traveler <= 0 || slot > 4 ||
            string.IsNullOrWhiteSpace(publicId) || publicId.Length > 255 || publicId.Any(c => c > 127) ||
            bytes < 1 || bytes > MaximumInputBytes || width <= 0 || height <= 0 || (long)width * height > MaximumPixels ||
            !((mime == "image/jpeg" && extension == ".jpg") || (mime == "image/png" && extension == ".png") || (mime == "image/webp" && extension == ".webp")))
            throw new ArgumentException("Invalid review media reservation metadata.");
        return new()
        {
            Id = id,
            BatchId = batch,
            BookingId = booking,
            ServiceBookingId = serviceBooking,
            TravelerUserId = traveler,
            SortOrder = slot,
            PublicId = publicId,
            State = Reserved,
            ContentType = mime,
            Extension = extension,
            InputByteLength = bytes,
            Width = width,
            Height = height,
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime()
        };
    }
    public static bool ValidUpload(string url, long bytes) => bytes > 0 && !string.IsNullOrWhiteSpace(url) && url.Length <= 2048 &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
    public bool TryRecordUploadDispatch(DateTimeOffset now)
    {
        if (State != Reserved) return false;
        UpdatedAtUtc = now.ToUniversalTime(); return true;
    }
    public bool TryRecordUpload(string url, long bytes, DateTimeOffset now)
    {
        if (State != Reserved || !ValidUpload(url, bytes)) return false;
        State = Uploaded; DeliveryUrl = url; StoredByteLength = bytes; UploadedAtUtc = now.ToUniversalTime(); UpdatedAtUtc = now.ToUniversalTime(); return true;
    }
    public bool TryAdopt(DateTimeOffset now)
    {
        if (State != Uploaded) return false;
        State = Adopted; AdoptedAtUtc = now.ToUniversalTime(); UpdatedAtUtc = now.ToUniversalTime(); return true;
    }
    public bool TryMarkCleanupPending(DateTimeOffset now)
    {
        if (State != Reserved && State != Uploaded) return false;
        State = CleanupPending; UpdatedAtUtc = now.ToUniversalTime(); return true;
    }
}