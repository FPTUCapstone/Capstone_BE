using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Public.LandingContent;

/// <summary>
/// Public read model for the home page. It deliberately excludes unpublished drafts,
/// version history, actor identities, and all authoring metadata.
/// </summary>
public sealed record PublicLandingContentDto(int VersionNumber, DateTimeOffset PublishedAtUtc, JsonElement Content);

public sealed record GetPublishedLandingContentQuery : IRequest<Result<PublicLandingContentDto>>;

public sealed class GetPublishedLandingContentQueryHandler(IApplicationDbContext db) :
    IRequestHandler<GetPublishedLandingContentQuery, Result<PublicLandingContentDto>>
{
    public async Task<Result<PublicLandingContentDto>> Handle(GetPublishedLandingContentQuery request, CancellationToken ct)
    {
        var published = await db.LandingContentVersions.AsNoTracking()
            .Where(version => version.IsPublished)
            .OrderByDescending(version => version.VersionNumber)
            .Select(version => new { version.VersionNumber, version.PublishedAtUtc, version.ContentJson })
            .FirstOrDefaultAsync(ct);

        if (published is null || !published.PublishedAtUtc.HasValue)
        {
            return Result.Failure<PublicLandingContentDto>(PublicLandingContentErrorCodes.NotAvailable,
                "Landing content is not available.");
        }

        using var document = JsonDocument.Parse(published.ContentJson);
        return Result.Success(new PublicLandingContentDto(published.VersionNumber,
            published.PublishedAtUtc.Value, document.RootElement.Clone()));
    }
}

public static class PublicLandingContentErrorCodes
{
    public const string NotAvailable = "public_landing_content.not_available";
}