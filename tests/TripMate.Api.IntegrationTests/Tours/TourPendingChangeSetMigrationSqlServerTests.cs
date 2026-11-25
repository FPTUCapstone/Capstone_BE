using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

[Collection(nameof(TripMateApiFactory))]
public sealed class TourPendingChangeSetMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_IsIdempotent_AndTableAndIndexExist()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        await database.ExecuteScriptAsync(MigrationPath());
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.tables WHERE name='TourPendingChangeSets' AND schema_id=SCHEMA_ID('commerce');"))
            .Should().Be(1);
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name='UX_TourPendingChangeSets_OpenPerTour' " +
            "AND object_id=OBJECT_ID(N'commerce.TourPendingChangeSets') AND is_unique=1;"))
            .Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UniqueFilteredIndex_RejectsASecondOpenChangeSetForTheSameTour_ButAllowsASecondAppliedOne()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var context = database.CreateDbContext();
        var (tourId, operatorUserId, policyId) = await SeedTourAsync(context);

        var first = TourPendingChangeSet.CreateDraft(
            tourId, operatorUserId, 1, "First draft", "Description", 100_000m, 1,
            null, null, null, null, Tour.DefaultTimeZoneId, "[]", "Da Nang", policyId, "[]", "[]",
            DateTimeOffset.UtcNow);
        context.TourPendingChangeSets.Add(first);
        await context.SaveChangesAsync();

        var second = TourPendingChangeSet.CreateDraft(
            tourId, operatorUserId, 1, "Second draft", "Description", 100_000m, 1,
            null, null, null, null, Tour.DefaultTimeZoneId, "[]", "Da Nang", policyId, "[]", "[]",
            DateTimeOffset.UtcNow);
        context.TourPendingChangeSets.Add(second);
        var insertSecondOpen = async () => await context.SaveChangesAsync();

        await insertSecondOpen.Should().ThrowAsync<DbUpdateException>();

        context.ChangeTracker.Clear();
        var tracked = await context.TourPendingChangeSets.SingleAsync(c => c.TourId == tourId);
        tracked.Submit(DateTimeOffset.UtcNow);
        tracked.MarkApplied(operatorUserId, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();

        var afterApplied = TourPendingChangeSet.CreateDraft(
            tourId, operatorUserId, 1, "Third draft", "Description", 100_000m, 1,
            null, null, null, null, Tour.DefaultTimeZoneId, "[]", "Da Nang", policyId, "[]", "[]",
            DateTimeOffset.UtcNow);
        context.TourPendingChangeSets.Add(afterApplied);
        await context.SaveChangesAsync();

        (await context.TourPendingChangeSets.CountAsync(c => c.TourId == tourId)).Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task EfCore_RoundTripsAChangeSetThroughEveryLifecycleField()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var writeContext = database.CreateDbContext();
        var (tourId, operatorUserId, policyId) = await SeedTourAsync(writeContext);

        var changeSet = TourPendingChangeSet.CreateDraft(
            tourId, operatorUserId, 2, "Round trip draft", "A full description.", 250_000m, 2,
            "Breakfast", null, "Beach", null, Tour.DefaultTimeZoneId, "[1,2]", "Da Nang, Hoi An",
            policyId, "[{\"dayNo\":1}]", "[{\"id\":null}]", DateTimeOffset.UtcNow);
        writeContext.TourPendingChangeSets.Add(changeSet);
        await writeContext.SaveChangesAsync();
        changeSet.Submit(DateTimeOffset.UtcNow);
        await writeContext.SaveChangesAsync();

        await using var readContext = database.CreateDbContext();
        var reloaded = await readContext.TourPendingChangeSets.AsNoTracking()
            .SingleAsync(c => c.Id == changeSet.Id);

        reloaded.TourId.Should().Be(tourId);
        reloaded.SubmittedByUserId.Should().Be(operatorUserId);
        reloaded.Status.Should().Be(TourPendingChangeStatus.PendingReview);
        reloaded.Title.Should().Be("Round trip draft");
        reloaded.DestinationIdsJson.Should().Be("[1,2]");
        reloaded.ItineraryItemsJson.Should().Be("[{\"dayNo\":1}]");
        reloaded.SchedulesJson.Should().Be("[{\"id\":null}]");
        reloaded.SubmittedAtUtc.Should().NotBeNull();
        reloaded.CancellationPolicyTemplateId.Should().Be(policyId);
    }

    private static async Task<(long TourId, long OperatorUserId, long PolicyId)> SeedTourAsync(
        TripMate.Infrastructure.Persistence.ApplicationDbContext context)
    {
        var user = new User
        {
            Email = $"operator-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var policy = CancellationPolicyTemplate.Create(
            $"Policy-{Guid.NewGuid():N}", null, DateTimeOffset.UtcNow,
            [CancellationPolicyTemplateRule.Create(30, 0)]);
        context.CancellationPolicyTemplates.Add(policy);
        await context.SaveChangesAsync();

        var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
        typeof(Destination).GetProperty(nameof(Destination.Name))!.SetValue(destination, "Da Nang");
        context.Destinations.Add(destination);
        await context.SaveChangesAsync();

        var poiCategory = PoiCategory.Create($"Category-{Guid.NewGuid():N}", null);
        context.PoiCategories.Add(poiCategory);
        await context.SaveChangesAsync();
        var poi = PointOfInterest.Create(poiCategory, "Seed POI", 16.0m, 108.2m, user.Id, DateTimeOffset.UtcNow);
        context.PointsOfInterest.Add(poi);
        await context.SaveChangesAsync();

        var tour = Tour.CreateDraft(
            user.Id, null, "Seed tour", "Seed description", 100_000m, 1,
            destinationIds: [destination.Id], destinationSummary: "Da Nang",
            cancellationPolicy: policy,
            itineraryItems: [TourItineraryItem.Create(1, 1, poi.Id, null, 60, null)],
            schedules: [TourSchedule.Create(
                DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(6), null, 10,
                DateTimeOffset.UtcNow, adultPrice: 100_000m)],
            nowUtc: DateTimeOffset.UtcNow);
        context.Tours.Add(tour);
        await context.SaveChangesAsync();

        return (tour.Id, user.Id, policy.Id);
    }

    private static string MigrationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository not found."),
            "database", "migrations", "20261125_tour_pending_change_sets.sql");
    }
}
