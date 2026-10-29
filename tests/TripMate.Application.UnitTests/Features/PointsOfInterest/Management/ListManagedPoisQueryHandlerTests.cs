using System.Globalization;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.PointsOfInterest.Management;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Management;

public class ListManagedPoisQueryHandlerTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Staff)]
    public async Task Handle_CurrentActiveAdministrationActor_ReturnsBothStatusesWithoutWrites(UserRole role)
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, role);
        var category = await SeedCategory(context, "Museum");
        var active = await SeedPoi(context, category, "Alpha", PointOfInterestStatus.Active);
        var inactive = await SeedPoi(context, category, "Beta", PointOfInterestStatus.Inactive);
        var savesBefore = context.SaveChangesAsyncCallCount;
        context.ChangeTracker.Clear();

        var result = await Handler(context, actor.Id, "Traveler").Handle(new(), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.Id).Should().Equal(Id(active), Id(inactive));
        result.Value.Items.Select(item => item.Status).Should().Equal("Active", "Inactive");
        context.SaveChangesAsyncCallCount.Should().Be(savesBefore);
        context.ChangeTracker.Entries().Should().BeEmpty();
        (await context.AuditLogs.CountAsync()).Should().Be(0);
        (await context.PointsOfInterest.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(UserRole.Traveler, AccountStatus.Active)]
    [InlineData(UserRole.TourOperator, AccountStatus.Active)]
    [InlineData(UserRole.Administrator, AccountStatus.Inactive)]
    [InlineData(UserRole.Staff, AccountStatus.Inactive)]
    [InlineData(UserRole.Administrator, AccountStatus.PendingEmailVerification)]
    [InlineData(UserRole.Staff, AccountStatus.Locked)]
    public async Task Handle_DisallowedPersistedActor_RejectsEvenWithAdminClaim(UserRole role, AccountStatus status)
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, role, status);
        await SeedCategory(context, "Must not disclose");
        var savesBefore = context.SaveChangesAsyncCallCount;

        var result = await Handler(context, actor.Id, "Administrator").Handle(new(), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ManagedPoiErrors.AccessRequired);
        context.SaveChangesAsyncCallCount.Should().Be(savesBefore);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(123L)]
    public async Task Handle_MissingActor_RejectsWithoutWriting(long? actorId)
    {
        await using var context = TestDbContext.Create();

        var result = await Handler(context, actorId).Handle(new(), default);

        result.ErrorCode.Should().Be(ManagedPoiErrors.AccessRequired);
        context.SaveChangesAsyncCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_RevokedPersistedActor_DoesNotTrustPreviouslyActiveTrackedInstance()
    {
        var store = Guid.NewGuid().ToString();
        await using var context = TestDbContext.Create(store);
        var actor = await SeedActor(context, UserRole.Administrator);
        await using (var revoker = TestDbContext.Create(store))
        {
            var persisted = await revoker.Users.SingleAsync();
            persisted.Status = AccountStatus.Inactive;
            await revoker.SaveChangesAsync();
        }

        var result = await Handler(context, actor.Id).Handle(new(), default);

        result.ErrorCode.Should().Be(ManagedPoiErrors.AccessRequired);
    }

    [Theory]
    [InlineData("Active", "Matching Active")]
    [InlineData("Inactive", "Matching Inactive")]
    public async Task Handle_CombinesSearchCategoryAndStatusBeforePaging(string status, string expectedName)
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, UserRole.Staff);
        var selected = await SeedCategory(context, "Selected");
        var other = await SeedCategory(context, "Other");
        await SeedPoi(context, selected, "Matching Active", PointOfInterestStatus.Active);
        await SeedPoi(context, selected, "Matching Inactive", PointOfInterestStatus.Inactive);
        await SeedPoi(context, selected, "Other name", PointOfInterestStatus.Active);
        await SeedPoi(context, other, "Matching outside category", PointOfInterestStatus.Active);

        var result = await Handler(context, actor.Id).Handle(new()
        {
            Search = "  Matching  ", CategoryId = selected.Id, Status = status, PageSize = 1,
        }, default);

        result.Value.TotalCount.Should().Be(1);
        result.Value.TotalPages.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.Name.Should().Be(expectedName);
        result.Value.Metadata.Categories.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_SearchMatchesNullableAddressOrName_AndWhitespaceMeansOmitted()
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, UserRole.Staff);
        var category = await SeedCategory(context, "Museum");
        await SeedPoi(context, category, "River museum", address: null);
        await SeedPoi(context, category, "Gallery", address: "River avenue");
        await SeedPoi(context, category, "Elsewhere", address: null);

        var matching = await Handler(context, actor.Id).Handle(new() { Search = " River " }, default);
        var all = await Handler(context, actor.Id).Handle(new() { Search = " \t\r\n " }, default);

        matching.Value.Items.Select(item => item.Name).Should().Equal("Gallery", "River museum");
        matching.Value.Items.Single(item => item.Name == "River museum").Address.Should().BeNull();
        all.Value.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Handle_NameThenNumericIdOrdering_PagesTheWholeResult()
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, UserRole.Administrator);
        var category = await SeedCategory(context, "Museum");
        await SeedPoi(context, category, "Zulu", id: 1);
        await SeedPoi(context, category, "Alpha", id: 10);
        await SeedPoi(context, category, "Alpha", id: 2);
        await SeedPoi(context, category, "Beta", id: 11);

        var first = await Handler(context, actor.Id).Handle(new() { PageSize = 2 }, default);
        var second = await Handler(context, actor.Id).Handle(new() { Page = 2, PageSize = 2 }, default);

        first.Value.Items.Select(item => item.Id).Should().Equal("2", "10");
        second.Value.Items.Select(item => item.Id).Should().Equal("11", "1");
        second.Value.Page.Should().Be(2);
        second.Value.PageSize.Should().Be(2);
        second.Value.TotalCount.Should().Be(4);
        second.Value.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task Handle_MetadataContainsAllCategoriesWithNoPoisAndStableNameIdOrder()
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, UserRole.Staff);
        var zulu = await SeedCategory(context, "Zulu");
        var firstAlpha = await SeedCategory(context, "Alpha");
        var secondAlpha = await SeedCategory(context, "Alpha");
        await SeedPoi(context, zulu, "Only POI");

        var result = await Handler(context, actor.Id).Handle(new() { Search = "No match", Page = 2 }, default);

        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Metadata.Categories.Select(category => category.Id).Should().Equal(firstAlpha.Id, secondAlpha.Id, zulu.Id);
        result.Value.Metadata.Categories.Select(category => category.Name).Should().Equal("Alpha", "Alpha", "Zulu");
    }

    [Fact]
    public async Task Handle_KnownCategoryWithoutPois_SucceedsWithCompleteMetadata()
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, UserRole.Staff);
        var empty = await SeedCategory(context, "Empty");
        var populated = await SeedCategory(context, "Populated");
        await SeedPoi(context, populated, "Other POI");

        var result = await Handler(context, actor.Id).Handle(new() { CategoryId = empty.Id }, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
        result.Value.Metadata.Categories.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_UnknownCategory_ReturnsCategoryFailureRatherThanEmptySuccess()
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, UserRole.Staff);
        await SeedCategory(context, "Known");

        var result = await Handler(context, actor.Id).Handle(new() { CategoryId = int.MaxValue }, default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ManagedPoiErrors.CategoryNotFound);
    }

    [Fact]
    public async Task Handle_RepresentablePageBeyondLast_KeepsActualTotalsAndMetadata()
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, UserRole.Staff);
        var category = await SeedCategory(context, "Museum");
        await SeedPoi(context, category, "Only POI");

        var result = await Handler(context, actor.Id).Handle(new() { Page = int.MaxValue, PageSize = 1 }, default);

        result.Value.Page.Should().Be(int.MaxValue);
        result.Value.TotalCount.Should().Be(1);
        result.Value.TotalPages.Should().Be(1);
        result.Value.Items.Should().BeEmpty();
        result.Value.Metadata.Categories.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_EmptyCatalogue_ReturnsRealEmptyItemsAndMetadata()
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, UserRole.Administrator);

        var result = await Handler(context, actor.Id).Handle(new(), default);

        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
        result.Value.Metadata.Categories.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_DtoSerializationPreservesInt64IdsStatusAndStructuredHours()
    {
        await using var context = TestDbContext.Create();
        var actor = await SeedActor(context, UserRole.Administrator);
        var category = await SeedCategory(context, "Museum");
        var high = await SeedPoi(context, category, "High ID", id: 9_007_199_254_740_993);
        high.AddOpeningHour(PoiOpeningHour.Create(6, new TimeOnly(9, 10, 11).Add(TimeSpan.FromTicks(1)), new TimeOnly(17, 30), false));
        high.AddOpeningHour(PoiOpeningHour.Create(0, null, null, true));
        await context.SaveChangesAsync();
        await SeedPoi(context, category, "Maximum ID", PointOfInterestStatus.Inactive, id: long.MaxValue);
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            var result = await Handler(context, actor.Id).Handle(new(), default);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var root = json.RootElement;
            root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("page", "pageSize", "totalCount", "totalPages", "metadata", "items");
            var items = root.GetProperty("items");
            items[0].GetProperty("id").ValueKind.Should().Be(JsonValueKind.String);
            items[0].GetProperty("id").GetString().Should().Be("9007199254740993");
            items[1].GetProperty("id").GetString().Should().Be("9223372036854775807");
            items[1].GetProperty("status").GetString().Should().Be("Inactive");
            items[0].GetProperty("status").GetString().Should().Be("Active");
            items[0].GetProperty("categoryId").GetInt32().Should().Be(category.Id);
            items[0].GetProperty("categoryName").GetString().Should().Be(category.Name);
            items[0].GetProperty("latitude").GetDecimal().Should().Be(16.04m);
            items[0].GetProperty("longitude").GetDecimal().Should().Be(108.22m);
            items[0].GetProperty("address").ValueKind.Should().Be(JsonValueKind.Null);
            var hours = items[0].GetProperty("openingHours");
            hours[0].GetProperty("dayOfWeek").GetInt32().Should().Be(0);
            hours[0].GetProperty("isClosed").GetBoolean().Should().BeTrue();
            hours[0].GetProperty("openTime").ValueKind.Should().Be(JsonValueKind.Null);
            hours[0].GetProperty("closeTime").ValueKind.Should().Be(JsonValueKind.Null);
            hours[1].GetProperty("dayOfWeek").GetInt32().Should().Be(6);
            hours[1].GetProperty("openTime").GetString().Should().Be("09:10:11.0000001");
            hours[1].GetProperty("closeTime").GetString().Should().Be("17:30:00");
            items[1].GetProperty("openingHours").GetArrayLength().Should().Be(0);
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
        }
    }

    private static ListManagedPoisQueryHandler Handler(TestDbContext context, long? actorId, string? role = "Administrator") =>
        new(context, new FakeCurrentUserService { UserId = actorId, Role = role });

    private static async Task<User> SeedActor(TestDbContext context, UserRole role, AccountStatus status = AccountStatus.Active)
    {
        var actor = new User { Role = role, Status = status, FullName = "Management reader", CreatedAtUtc = SeedTime, UpdatedAtUtc = SeedTime };
        context.Users.Add(actor);
        await context.SaveChangesAsync();
        return actor;
    }

    private static async Task<PoiCategory> SeedCategory(TestDbContext context, string name)
    {
        var category = PoiCategory.Create(name, null);
        context.PoiCategories.Add(category);
        await context.SaveChangesAsync();
        return category;
    }

    private static async Task<PointOfInterest> SeedPoi(TestDbContext context, PoiCategory category, string name,
        PointOfInterestStatus status = PointOfInterestStatus.Active, string? address = null, long? id = null)
    {
        var poi = PointOfInterest.Create(category, name, 16.04m, 108.22m, 1, SeedTime, address);
        if (id.HasValue) { poi.Id = id.Value; }
        context.PointsOfInterest.Add(poi);
        context.Entry(poi).Property(candidate => candidate.Status).CurrentValue = status;
        await context.SaveChangesAsync();
        return poi;
    }

    private static string Id(PointOfInterest poi) => poi.Id.ToString(CultureInfo.InvariantCulture);
}
