using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Immutable published snapshots and the single mutable draft for UC-66.
/// Content is retained as one validated JSON document so a published landing page is always a
/// coherent snapshot rather than a mixture of independently updated sections.
/// </summary>
public sealed class LandingContentVersion : BaseEntity
{
    private LandingContentVersion()
    {
    }

    public int VersionNumber { get; private set; }

    public bool IsPublished { get; private set; }

    /// <summary>The published version this mutable draft was derived from; null on immutable snapshots.</summary>
    public int? BaseVersionNumber { get; private set; }

    public string ContentJson { get; private set; } = string.Empty;

    public long? PublishedByUserId { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public long? UpdatedByUserId { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static LandingContentVersion CreateDraft(string contentJson, int baseVersionNumber, long actorUserId, DateTimeOffset nowUtc) =>
        new()
        {
            VersionNumber = 0,
            IsPublished = false,
            BaseVersionNumber = baseVersionNumber,
            ContentJson = RequireContent(contentJson),
            UpdatedByUserId = RequireActor(actorUserId),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };

    public void ReplaceDraft(string contentJson, int baseVersionNumber, long actorUserId, DateTimeOffset nowUtc)
    {
        if (IsPublished) throw new InvalidOperationException("Published content cannot be edited.");
        ContentJson = RequireContent(contentJson);
        BaseVersionNumber = baseVersionNumber;
        UpdatedByUserId = RequireActor(actorUserId);
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public LandingContentVersion PublishAsNextVersion(long actorUserId, int nextVersion, DateTimeOffset nowUtc)
    {
        if (nextVersion <= 0) throw new ArgumentOutOfRangeException(nameof(nextVersion));
        return new LandingContentVersion
        {
            VersionNumber = nextVersion,
            IsPublished = true,
            BaseVersionNumber = null,
            ContentJson = ContentJson,
            PublishedByUserId = RequireActor(actorUserId),
            PublishedAtUtc = nowUtc.ToUniversalTime(),
            UpdatedByUserId = actorUserId,
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    private static string RequireContent(string contentJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentJson);
        return contentJson;
    }

    private static long RequireActor(long actorUserId) =>
        actorUserId > 0 ? actorUserId : throw new ArgumentOutOfRangeException(nameof(actorUserId));
}