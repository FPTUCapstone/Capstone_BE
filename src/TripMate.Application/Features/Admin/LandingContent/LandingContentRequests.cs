using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.LandingContent.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.LandingContent;

public sealed record LandingContentVersionDto(string VersionId, int VersionNumber, string PublishedByName,
    DateTimeOffset PublishedAtUtc, JsonElement Content);
public sealed record LandingContentDraftDto(int BaseVersion, JsonElement Content);
public sealed record LandingContentStateDto(LandingContentVersionDto Published, LandingContentDraftDto? Draft,
    IReadOnlyList<LandingContentVersionDto> History);

public sealed record GetLandingContentQuery : IRequest<Result<LandingContentStateDto>>;
public sealed record SaveLandingContentDraftCommand(int BaseVersion, JsonElement Content) : IRequest<Result<LandingContentDraftDto>>;
public sealed record PublishLandingContentDraftCommand(int BaseVersion) : IRequest<Result<LandingContentVersionDto>>;
public sealed record RestoreLandingContentVersionCommand(string VersionId) : IRequest<Result<LandingContentDraftDto>>;
public sealed record GetLandingContentApprovedToursQuery : IRequest<Result<IReadOnlyList<LandingContentTourOptionDto>>>;
public sealed record LandingContentTourOptionDto(string TourId, string Title);
public sealed record LandingContentMediaDto(string MediaId, string Url);
public sealed record UploadLandingContentMediaCommand(TourMediaImageSource Image) : IRequest<Result<LandingContentMediaDto>>;

public sealed class LandingContentHandler(IApplicationDbContext db, ICurrentUserService currentUser,
    IDateTimeProvider clock, ITourMediaImageInspector imageInspector, ILandingContentMediaStorage mediaStorage) :
    IRequestHandler<GetLandingContentQuery, Result<LandingContentStateDto>>,
    IRequestHandler<SaveLandingContentDraftCommand, Result<LandingContentDraftDto>>,
    IRequestHandler<PublishLandingContentDraftCommand, Result<LandingContentVersionDto>>,
    IRequestHandler<RestoreLandingContentVersionCommand, Result<LandingContentDraftDto>>,
    IRequestHandler<GetLandingContentApprovedToursQuery, Result<IReadOnlyList<LandingContentTourOptionDto>>>,
    IRequestHandler<UploadLandingContentMediaCommand, Result<LandingContentMediaDto>>
{
    public async Task<Result<LandingContentStateDto>> Handle(GetLandingContentQuery request, CancellationToken ct)
    {
        if (!CanManage()) return Forbidden<LandingContentStateDto>();
        var versions = await db.LandingContentVersions.AsNoTracking()
            .Where(x => x.IsPublished).OrderByDescending(x => x.VersionNumber).ToListAsync(ct);
        var published = versions.FirstOrDefault();
        if (published is null) return Result.Failure<LandingContentStateDto>(LandingContentErrorCodes.NotInitialized, "Landing content is not initialized.");
        var draft = await db.LandingContentVersions.AsNoTracking().SingleOrDefaultAsync(x => !x.IsPublished, ct);
        var names = await PublishedByNamesAsync(versions, ct);
        return Result.Success(new LandingContentStateDto(ToDto(published, names), draft is null ? null : ToDraft(draft),
            versions.Select(version => ToDto(version, names)).ToArray()));
    }

    public async Task<Result<LandingContentDraftDto>> Handle(SaveLandingContentDraftCommand request, CancellationToken ct)
    {
        if (!CanManage()) return Forbidden<LandingContentDraftDto>();
        return await db.ExecuteInSerializableTransactionAsync(async transactionCt =>
        {
            if (!IsContent(request.Content) || !await HasOnlySelectableToursAsync(request.Content, transactionCt))
                return Result.Failure<LandingContentDraftDto>(LandingContentErrorCodes.InvalidContent, "Landing content is invalid or references an unavailable tour.");
            var published = await CurrentPublished(transactionCt);
            if (published is null) return Result.Failure<LandingContentDraftDto>(LandingContentErrorCodes.NotInitialized, "Landing content is not initialized.");
            if (published.VersionNumber != request.BaseVersion) return Stale<LandingContentDraftDto>();
            var draft = await db.LandingContentVersions.SingleOrDefaultAsync(x => !x.IsPublished, transactionCt);
            var json = request.Content.GetRawText();
            if (draft is null) { draft = LandingContentVersion.CreateDraft(json, request.BaseVersion, currentUser.UserId!.Value, clock.UtcNow); db.LandingContentVersions.Add(draft); }
            else draft.ReplaceDraft(json, request.BaseVersion, currentUser.UserId!.Value, clock.UtcNow);
            await db.SaveChangesAsync(transactionCt);
            return Result.Success(ToDraft(draft));
        }, ct);
    }

    public async Task<Result<LandingContentVersionDto>> Handle(PublishLandingContentDraftCommand request, CancellationToken ct)
    {
        if (!CanManage()) return Forbidden<LandingContentVersionDto>();
        return await db.ExecuteInSerializableTransactionAsync(async transactionCt =>
        {
            var published = await CurrentPublished(transactionCt);
            var draft = await db.LandingContentVersions.SingleOrDefaultAsync(x => !x.IsPublished, transactionCt);
            if (published is null || draft is null) return Result.Failure<LandingContentVersionDto>(LandingContentErrorCodes.NotInitialized, "A published version and draft are required.");
            if (published.VersionNumber != request.BaseVersion || draft.BaseVersionNumber != request.BaseVersion) return Stale<LandingContentVersionDto>();
            using var content = JsonDocument.Parse(draft.ContentJson);
            if (!IsContent(content.RootElement) || !await HasOnlySelectableToursAsync(content.RootElement, transactionCt))
                return Result.Failure<LandingContentVersionDto>(LandingContentErrorCodes.InvalidContent, "Landing content is invalid or references an unavailable tour.");
            var snapshot = draft.PublishAsNextVersion(currentUser.UserId!.Value, published.VersionNumber + 1, clock.UtcNow);
            db.LandingContentVersions.Add(snapshot);
            db.LandingContentVersions.Remove(draft);
            db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(currentUser.UserId, "LandingContentPublish", "LandingContentVersion", null, clock.UtcNow, AuditOutcome.Success, afterData: $"{{\"version\":{snapshot.VersionNumber}}}"));
            await db.SaveChangesAsync(transactionCt);
            var names = await PublishedByNamesAsync([snapshot], transactionCt);
            return Result.Success(ToDto(snapshot, names));
        }, ct);
    }

    public async Task<Result<LandingContentDraftDto>> Handle(RestoreLandingContentVersionCommand request, CancellationToken ct)
    {
        if (!CanManage()) return Forbidden<LandingContentDraftDto>();
        if (!long.TryParse(request.VersionId, out var id) || id <= 0) return Result.Failure<LandingContentDraftDto>(LandingContentErrorCodes.VersionNotFound, "Version not found.");
        return await db.ExecuteInSerializableTransactionAsync(async transactionCt =>
        {
            var source = await db.LandingContentVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsPublished, transactionCt);
            var current = await CurrentPublished(transactionCt);
            if (source is null) return Result.Failure<LandingContentDraftDto>(LandingContentErrorCodes.VersionNotFound, "Version not found.");
            if (current is null) return Result.Failure<LandingContentDraftDto>(LandingContentErrorCodes.NotInitialized, "Landing content is not initialized.");
            using var content = JsonDocument.Parse(source.ContentJson);
            if (!IsContent(content.RootElement) || !await HasOnlySelectableToursAsync(content.RootElement, transactionCt))
                return Result.Failure<LandingContentDraftDto>(LandingContentErrorCodes.InvalidContent, "The selected version contains invalid or unavailable content.");
            var draft = await db.LandingContentVersions.SingleOrDefaultAsync(x => !x.IsPublished, transactionCt);
            if (draft is null) { draft = LandingContentVersion.CreateDraft(source.ContentJson, current.VersionNumber, currentUser.UserId!.Value, clock.UtcNow); db.LandingContentVersions.Add(draft); }
            else draft.ReplaceDraft(source.ContentJson, current.VersionNumber, currentUser.UserId!.Value, clock.UtcNow);
            await db.SaveChangesAsync(transactionCt);
            return Result.Success(ToDraft(draft));
        }, ct);
    }

    public async Task<Result<IReadOnlyList<LandingContentTourOptionDto>>> Handle(GetLandingContentApprovedToursQuery request, CancellationToken ct)
    {
        if (!CanManage()) return Forbidden<IReadOnlyList<LandingContentTourOptionDto>>();
        var now = clock.UtcNow;
        var tours = await db.Tours.AsNoTracking()
            .Where(tour => tour.Status == TourStatus.Published ||
                (tour.Status == TourStatus.Approved && tour.PublishedAtUtc.HasValue && tour.PublishedAtUtc <= now))
            .OrderBy(tour => tour.Title).ThenBy(tour => tour.Id)
            .Select(tour => new LandingContentTourOptionDto(tour.Id.ToString(), tour.Title))
            .ToArrayAsync(ct);
        return Result.Success<IReadOnlyList<LandingContentTourOptionDto>>(tours);
    }

    public async Task<Result<LandingContentMediaDto>> Handle(UploadLandingContentMediaCommand request, CancellationToken ct)
    {
        if (!CanManage()) return Forbidden<LandingContentMediaDto>();
        if (request.Image.Length > 5 * 1024 * 1024)
            return Result.Failure<LandingContentMediaDto>(LandingContentErrorCodes.InvalidContent, "The image is too large.");
        var inspected = await imageInspector.InspectAsync(request.Image, ct);
        if (!inspected.IsAccepted || inspected.Image!.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
            return Result.Failure<LandingContentMediaDto>(LandingContentErrorCodes.InvalidContent, "The uploaded image is invalid.");
        var publicId = mediaStorage.AllocatePublicId();
        var stored = await mediaStorage.UploadAsync(new LandingContentMediaUpload(publicId, inspected.Image.ContentType, inspected.Image.Bytes), ct);
        return stored.IsSuccess
            ? Result.Success(new LandingContentMediaDto(publicId, stored.DeliveryUrl!.ToString()))
            : Result.Failure<LandingContentMediaDto>(LandingContentErrorCodes.NotInitialized, "The media service is unavailable.");
    }

    private bool CanManage() => currentUser.UserId.HasValue && currentUser.Role is nameof(UserRole.Administrator) or nameof(UserRole.Staff);
    private async Task<LandingContentVersion?> CurrentPublished(CancellationToken ct) => await db.LandingContentVersions.Where(x => x.IsPublished).OrderByDescending(x => x.VersionNumber).FirstOrDefaultAsync(ct);
    private static Result<T> Forbidden<T>() => Result.Failure<T>(LandingContentErrorCodes.Forbidden, "You do not have permission to access this function.");
    private static Result<T> Stale<T>() => Result.Failure<T>(LandingContentErrorCodes.StaleVersion, "Landing content has changed. Reload and try again.");
    private static LandingContentDraftDto ToDraft(LandingContentVersion x) => new(x.BaseVersionNumber!.Value, CloneContent(x.ContentJson));
    private async Task<IReadOnlyDictionary<long, string>> PublishedByNamesAsync(IEnumerable<LandingContentVersion> versions, CancellationToken ct)
    {
        var ids = versions.Where(version => version.PublishedByUserId.HasValue).Select(version => version.PublishedByUserId!.Value).Distinct().ToArray();
        return await db.Users.AsNoTracking().Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.FullName, ct);
    }

    private static LandingContentVersionDto ToDto(LandingContentVersion version, IReadOnlyDictionary<long, string> names) => new(
        version.Id.ToString(), version.VersionNumber,
        version.PublishedByUserId is long userId && names.TryGetValue(userId, out var name) ? name : "System",
        version.PublishedAtUtc!.Value, CloneContent(version.ContentJson));

    private static JsonElement CloneContent(string contentJson)
    {
        using var document = JsonDocument.Parse(contentJson);
        return document.RootElement.Clone();
    }

    private async Task<bool> HasOnlySelectableToursAsync(JsonElement content, CancellationToken ct)
    {
        var ids = content.GetProperty("featuredTourIds").EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var id) ? id : 0)
            .ToArray();
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length) return false;
        if (ids.Length == 0) return true;
        var now = clock.UtcNow;
        var available = await db.Tours.AsNoTracking().CountAsync(tour => ids.Contains(tour.Id) &&
            (tour.Status == TourStatus.Published || (tour.Status == TourStatus.Approved && tour.PublishedAtUtc.HasValue && tour.PublishedAtUtc <= now)), ct);
        return available == ids.Length;
    }

    private static bool IsContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("hero", out var hero) ||
            !content.TryGetProperty("destinations", out var destinations) || !content.TryGetProperty("featuredTourIds", out var featured) ||
            !content.TryGetProperty("banner", out var banner) || hero.ValueKind != JsonValueKind.Object ||
            destinations.ValueKind != JsonValueKind.Array || featured.ValueKind != JsonValueKind.Array || banner.ValueKind != JsonValueKind.Object)
            return false;

        return IsRequiredString(hero, "headline") && IsString(hero, "subHeadline") && IsImageOrNull(hero, "backgroundImage") &&
            IsRequiredString(hero, "ctaLabel") && IsRequiredString(hero, "ctaTarget") &&
            destinations.EnumerateArray().All(destination => destination.ValueKind == JsonValueKind.Object && IsString(destination, "id") &&
                IsRequiredString(destination, "name") && IsRequiredString(destination, "description") && IsImage(destination, "image")) &&
            featured.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String) && IsImageOrNull(banner, "image") &&
            IsString(banner, "link") && IsString(banner, "displayStart") && IsString(banner, "displayEnd") && IsValidBanner(banner);
    }

    private static bool IsValidBanner(JsonElement banner)
    {
        var imagePresent = banner.GetProperty("image").ValueKind != JsonValueKind.Null;
        var link = banner.GetProperty("link").GetString()!.Trim();
        var start = banner.GetProperty("displayStart").GetString()!.Trim();
        var end = banner.GetProperty("displayEnd").GetString()!.Trim();
        if (!imagePresent && link.Length == 0 && start.Length == 0 && end.Length == 0) return true;
        return imagePresent && link.Length > 0 && DateTimeOffset.TryParse(start, out var startAt) &&
            DateTimeOffset.TryParse(end, out var endAt) && endAt >= startAt;
    }

    private static bool IsImage(JsonElement parent, string property) => parent.TryGetProperty(property, out var image) &&
        image.ValueKind == JsonValueKind.Object && IsString(image, "mediaId") && IsString(image, "url") &&
        image.GetProperty("mediaId").GetString()!.Trim().Length > 0 && image.GetProperty("url").GetString()!.Trim().Length > 0;
    private static bool IsImageOrNull(JsonElement parent, string property) => parent.TryGetProperty(property, out var image) &&
        (image.ValueKind == JsonValueKind.Null || IsImage(parent, property));
    private static bool IsString(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String;
    private static bool IsRequiredString(JsonElement parent, string property) => IsString(parent, property) && parent.GetProperty(property).GetString()!.Trim().Length > 0;
}