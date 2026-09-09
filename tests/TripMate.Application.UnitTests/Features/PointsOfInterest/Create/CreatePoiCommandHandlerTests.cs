using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.PointsOfInterest.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Create;

public class CreatePoiCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 2, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WithValidMinimalCommand_CreatesActivePoiWithDefaults()
    {
        await using var dbContext = TestDbContext.Create();
        var (administrator, category) = await SeedAdministratorAndCategory(dbContext);
        var handler = CreateHandler(dbContext, administrator.Id);

        var result = await handler.Handle(ValidCommand(category.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().BePositive();
        result.Value.Name.Should().Be("Da Lat Flower Park");
        result.Value.Latitude.Should().Be(11.941755m);
        result.Value.Longitude.Should().Be(108.438278m);
        result.Value.Status.Should().Be(PointOfInterestStatus.Active);
        result.Value.IndoorOutdoor.Should().Be(IndoorOutdoorType.Outdoor);
        result.Value.AverageVisitDurationMinutes.Should()
            .Be(PointOfInterest.DefaultAverageVisitDurationMinutes);
        result.Value.HasShelter.Should().BeFalse();
        result.Value.ScenicScore.Should().BeNull();
        result.Value.PhotoRating.Should().BeNull();
        dbContext.TransactionExecutionCount.Should().Be(1);
        (await dbContext.PointsOfInterest.SingleAsync()).Id.Should().Be(result.Value.Id);
    }

    [Theory]
    [InlineData(UserRole.Traveler, AccountStatus.Active)]
    [InlineData(UserRole.Administrator, AccountStatus.Inactive)]
    public async Task Handle_WithoutActiveAdministrator_ReturnsForbiddenAndDoesNotWrite(
        UserRole role,
        AccountStatus status)
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, role, status);
        var category = await SeedCategory(dbContext);
        var handler = CreateHandler(dbContext, user.Id);

        var result = await handler.Handle(ValidCommand(category.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PoiErrorCodes.AdminAccessRequired);
        (await dbContext.PointsOfInterest.CountAsync()).Should().Be(0);
        (await dbContext.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithMissingCategory_ReturnsReferenceNotFoundAndDoesNotWrite()
    {
        await using var dbContext = TestDbContext.Create();
        var administrator = await SeedUser(
            dbContext,
            UserRole.Administrator,
            AccountStatus.Active);
        var handler = CreateHandler(dbContext, administrator.Id);

        var result = await handler.Handle(ValidCommand(999), CancellationToken.None);

        result.ErrorCode.Should().Be(PoiErrorCodes.ReferenceNotFound);
        (await dbContext.PointsOfInterest.CountAsync()).Should().Be(0);
        (await dbContext.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithMissingTag_ReturnsReferenceNotFoundAndDoesNotWrite()
    {
        await using var dbContext = TestDbContext.Create();
        var (administrator, category) = await SeedAdministratorAndCategory(dbContext);
        var existingTag = Tag.Create("Garden");
        dbContext.Tags.Add(existingTag);
        await dbContext.SaveChangesAsync();
        var handler = CreateHandler(dbContext, administrator.Id);
        var command = ValidCommand(category.Id) with { TagIds = [existingTag.Id, 999] };

        var result = await handler.Handle(command, CancellationToken.None);

        result.ErrorCode.Should().Be(PoiErrorCodes.ReferenceNotFound);
        (await dbContext.PointsOfInterest.CountAsync()).Should().Be(0);
        (await dbContext.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithPossibleDuplicate_ReturnsConflictMetadataAndDoesNotWrite()
    {
        await using var dbContext = TestDbContext.Create();
        var (administrator, category) = await SeedAdministratorAndCategory(dbContext);
        var existing = PointOfInterest.Create(
            category,
            "Da Lat Flower Park",
            11.9417554m,
            108.4382784m,
            administrator.Id,
            Now);
        dbContext.PointsOfInterest.Add(existing);
        await dbContext.SaveChangesAsync();
        var handler = CreateHandler(dbContext, administrator.Id);
        var command = ValidCommand(category.Id) with
        {
            Name = "  da lat flower park  ",
            Latitude = 11.94175549m,
            Longitude = 108.43827849m,
        };

        var result = await handler.Handle(command, CancellationToken.None);

        result.ErrorCode.Should().Be(PoiErrorCodes.PossibleDuplicate);
        result.ErrorMetadata["existingPoiId"].Should().Be(existing.Id);
        (await dbContext.PointsOfInterest.CountAsync()).Should().Be(1);
        (await dbContext.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithConfirmedDuplicate_CreatesAnotherPoi()
    {
        await using var dbContext = TestDbContext.Create();
        var (administrator, category) = await SeedAdministratorAndCategory(dbContext);
        dbContext.PointsOfInterest.Add(PointOfInterest.Create(
            category,
            "Da Lat Flower Park",
            11.941755m,
            108.438278m,
            administrator.Id,
            Now));
        await dbContext.SaveChangesAsync();
        var handler = CreateHandler(dbContext, administrator.Id);
        var command = ValidCommand(category.Id) with { ConfirmDuplicate = true };

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await dbContext.PointsOfInterest.CountAsync()).Should().Be(2);
        (await dbContext.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_WithChildren_PersistsOpeningHoursAndTags()
    {
        await using var dbContext = TestDbContext.Create();
        var (administrator, category) = await SeedAdministratorAndCategory(dbContext);
        var tags = new[] { Tag.Create("Garden"), Tag.Create("Family") };
        dbContext.Tags.AddRange(tags);
        await dbContext.SaveChangesAsync();
        var handler = CreateHandler(dbContext, administrator.Id);
        var command = ValidCommand(category.Id) with
        {
            OpeningHours =
            [
                new CreatePoiOpeningHourInput(
                    0,
                    OpenTime: null,
                    CloseTime: null,
                    IsClosed: true),
                new CreatePoiOpeningHourInput(
                    1,
                    new TimeOnly(8, 0),
                    new TimeOnly(17, 0),
                    IsClosed: false),
            ],
            TagIds = tags.Select(tag => tag.Id).ToArray(),
        };

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.OpeningHours.Should().HaveCount(2);
        result.Value.TagIds.Should().BeEquivalentTo(tags.Select(tag => tag.Id));
        (await dbContext.PoiOpeningHours.CountAsync()).Should().Be(2);
        (await dbContext.PoiTags.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Handle_WithValidCommand_RecordsCreatorTimestampsAndAuditAggregateJson()
    {
        await using var dbContext = TestDbContext.Create();
        var (administrator, category) = await SeedAdministratorAndCategory(dbContext);
        var handler = CreateHandler(dbContext, administrator.Id);

        var result = await handler.Handle(ValidCommand(category.Id), CancellationToken.None);

        result.Value.CreatedById.Should().Be(administrator.Id);
        result.Value.CreatedAtUtc.Should().Be(Now);
        result.Value.UpdatedAtUtc.Should().Be(Now);

        var audit = await dbContext.AuditLogs.SingleAsync();
        audit.ActorUserId.Should().Be(administrator.Id);
        audit.ActionType.Should().Be(AuditActionTypes.PoiCreate);
        audit.AffectedEntity.Should().Be(AuditEntityTypes.PointOfInterest);
        audit.AffectedEntityId.Should().Be(result.Value.Id);
        audit.BeforeData.Should().BeNull();
        audit.IpAddress.Should().BeNull();
        audit.CreatedAtUtc.Should().Be(Now);

        using var afterData = JsonDocument.Parse(audit.AfterData!);
        afterData.RootElement.GetProperty("id").GetInt64().Should().Be(result.Value.Id);
        afterData.RootElement.GetProperty("name").GetString().Should().Be(result.Value.Name);
        afterData.RootElement.GetProperty("openingHours").GetArrayLength().Should().Be(0);
        afterData.RootElement.GetProperty("tagIds").GetArrayLength().Should().Be(0);
    }

    private static CreatePoiCommandHandler CreateHandler(
        TestDbContext dbContext,
        long? currentUserId) =>
        new(
            dbContext,
            new FakeCurrentUserService
            {
                UserId = currentUserId,
                Role = UserRole.Administrator.ToString(),
            },
            new FakeDateTimeProvider { UtcNow = Now });

    private static CreatePoiCommand ValidCommand(int categoryId) =>
        new(
            Name: "  Da Lat Flower Park  ",
            CategoryId: categoryId,
            Latitude: 11.9417554m,
            Longitude: 108.4382784m);

    private static async Task<(User Administrator, PoiCategory Category)>
        SeedAdministratorAndCategory(TestDbContext dbContext)
    {
        var administrator = await SeedUser(
            dbContext,
            UserRole.Administrator,
            AccountStatus.Active);
        var category = await SeedCategory(dbContext);
        return (administrator, category);
    }

    private static async Task<User> SeedUser(
        TestDbContext dbContext,
        UserRole role,
        AccountStatus status)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            FullName = "Test User",
            Role = role,
            Status = status,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private static async Task<PoiCategory> SeedCategory(TestDbContext dbContext)
    {
        var category = PoiCategory.Create("Attraction", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        return category;
    }
}