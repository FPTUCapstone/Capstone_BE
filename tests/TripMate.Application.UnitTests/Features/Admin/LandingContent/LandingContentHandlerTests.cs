using System.Text.Json;

using FluentAssertions;

using Moq;

using TripMate.Application.Common.Media;
using TripMate.Application.Features.Admin.LandingContent;
using TripMate.Application.Features.Admin.LandingContent.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Admin.LandingContent;

public sealed class LandingContentHandlerTests
{
    private readonly TestDbContext db = TestDbContext.Create();
    private readonly FakeCurrentUserService user = new() { UserId = 7, Role = nameof(UserRole.Administrator) };
    private readonly FakeDateTimeProvider clock = new() { UtcNow = new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.Zero) };

    [Fact]
    public async Task SaveDraft_ThenPublish_CreatesImmutableNextSnapshotAndClearsDraft()
    {
        var published = LandingContentVersion.CreateDraft(Content("old"), 1, 1, clock.UtcNow.AddDays(-1))
            .PublishAsNextVersion(1, 1, clock.UtcNow.AddDays(-1));
        db.LandingContentVersions.Add(published);
        await db.SaveChangesAsync(CancellationToken.None);
        var handler = CreateHandler();

        var saved = await handler.Handle(new SaveLandingContentDraftCommand(1, Document("new").RootElement), CancellationToken.None);
        var result = await handler.Handle(new PublishLandingContentDraftCommand(1), CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.Value.VersionNumber.Should().Be(2);
        result.Value.Content.GetProperty("hero").GetProperty("headline").GetString().Should().Be("new");
        db.LandingContentVersions.Count(x => !x.IsPublished).Should().Be(0);
        db.LandingContentVersions.Count(x => x.IsPublished).Should().Be(2);
        db.TransactionExecutionCount.Should().Be(2);
    }

    [Fact]
    public async Task SaveDraft_WhenBaseVersionIsStale_ReturnsConflictWithoutDraft()
    {
        db.LandingContentVersions.Add(LandingContentVersion.CreateDraft(Content("old"), 1, 1, clock.UtcNow)
            .PublishAsNextVersion(1, 2, clock.UtcNow));
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler().Handle(new SaveLandingContentDraftCommand(1, Document("new").RootElement), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LandingContentErrorCodes.StaleVersion);
        db.LandingContentVersions.Should().OnlyContain(x => x.IsPublished);
    }

    [Fact]
    public async Task SaveDraft_WhenContentHasAnIncompleteDestination_ReturnsValidationFailure()
    {
        db.LandingContentVersions.Add(LandingContentVersion.CreateDraft(Content("old"), 1, 1, clock.UtcNow)
            .PublishAsNextVersion(1, 1, clock.UtcNow));
        await db.SaveChangesAsync(CancellationToken.None);
        using var invalid = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            hero = new { headline = "Welcome", subHeadline = "", backgroundImage = (object?)null, ctaLabel = "Explore", ctaTarget = "/tours" },
            destinations = new[] { new { id = "da-nang", name = "Da Nang", description = "Coast", image = (object?)null } },
            featuredTourIds = Array.Empty<string>(),
            banner = new { image = (object?)null, link = "", displayStart = "", displayEnd = "" },
        }));

        var result = await CreateHandler().Handle(new SaveLandingContentDraftCommand(1, invalid.RootElement), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(LandingContentErrorCodes.InvalidContent);
        db.LandingContentVersions.Should().OnlyContain(version => version.IsPublished);
    }

    private LandingContentHandler CreateHandler() => new(db, user, clock,
        new Mock<ITourMediaImageInspector>(MockBehavior.Strict).Object,
        new Mock<ILandingContentMediaStorage>(MockBehavior.Strict).Object);

    private static JsonDocument Document(string headline) => JsonDocument.Parse(Content(headline));

    private static string Content(string headline) => JsonSerializer.Serialize(new
    {
        hero = new { headline, subHeadline = string.Empty, backgroundImage = (object?)null, ctaLabel = "Explore", ctaTarget = "/tours" },
        destinations = Array.Empty<object>(),
        featuredTourIds = Array.Empty<string>(),
        banner = new { image = (object?)null, link = string.Empty, displayStart = string.Empty, displayEnd = string.Empty },
    });
}