using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using DomainTourMedia = TripMate.Domain.Entities.TourMedia;

namespace TripMate.Api.IntegrationTests.Admin;

/// <summary>
/// UC-60 Screens 13/14 on real SQL Server: proves LIKE escaping, ordering, paging and the review projection
/// translate to T-SQL (the in-memory provider hides all of these).
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class PendingTourQueueSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "RequiredBehavior")]
    public async Task Queue_ListsOnlyPendingOldestFirst_AndKeywordIsLiteral()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.StaffId, UserRole.Staff);

        // Only Pending tours, oldest submission first; the Approved tour never appears.
        var all = await GetItemsAsync(client, "/api/v1/admin/tours");
        all.Select(i => i.GetProperty("id").GetInt64()).Should().Equal(seed.OlderPendingId, seed.NewerPendingId);
        all[0].GetProperty("operatorName").GetString().Should().Be("SQL Queue Tours");
        all[0].GetProperty("imageCount").GetInt32().Should().Be(1, "the soft-deleted image is not counted");
        all[0].GetRawText().Should().NotContain("0901112223").And.NotContain("TAX-");

        // A normal keyword matches by title, case-insensitively, and by operator company.
        (await GetItemsAsync(client, "/api/v1/admin/tours?keyword=lantern"))
            .Select(i => i.GetProperty("id").GetInt64()).Should().Equal(seed.OlderPendingId);
        (await GetItemsAsync(client, "/api/v1/admin/tours?keyword=sql%20queue"))
            .Should().HaveCount(2);

        // LIKE wildcards typed by the reviewer must be matched literally: none of the titles contain them.
        foreach (var wildcard in new[] { "%25", "_", "%5Ba-z%5D", "%5B" })
        {
            (await GetItemsAsync(client, $"/api/v1/admin/tours?keyword={wildcard}"))
                .Should().BeEmpty(because: $"keyword '{wildcard}' must not act as a wildcard");
        }

        // Paging envelope on the real provider, including a page past the end.
        var page2 = await client.GetAsync("/api/v1/admin/tours?page=2&pageSize=1");
        using var page2Body = JsonDocument.Parse(await page2.Content.ReadAsStringAsync());
        page2Body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(2);
        page2Body.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64().Should().Be(seed.NewerPendingId);
        (await GetItemsAsync(client, "/api/v1/admin/tours?page=9")).Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Classification", "RequiredBehavior")]
    public async Task ReviewDetail_ReturnsChecklistInputsForPending_AndConflictForApproved()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.StaffId, UserRole.Staff);

        var response = await client.GetAsync($"/api/v1/admin/tours/{seed.OlderPendingId}/review");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("overview").GetString().Should().Be("A tour awaiting Staff review on SQL Server.");
        root.GetProperty("includedServices").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Breakfast and guide");
        root.GetProperty("cancellationPolicy").EnumerateArray()
            .Select(r => r.GetProperty("daysBeforeDeparture").GetInt32()).Should().Equal(30, 0);
        root.GetProperty("schedules").GetArrayLength().Should().Be(1);
        root.GetProperty("itinerary")[0].GetProperty("poiName").GetString().Should().Be("Ancient Town");
        var images = root.GetProperty("images").EnumerateArray().ToList();
        images.Should().ContainSingle();
        images[0].GetProperty("url").GetString().Should().Be("https://res.cloudinary.com/tripmate/image/upload/pending.webp");
        root.GetRawText().Should().NotContain("0901112223").And.NotContain("TAX-");

        (await client.GetAsync($"/api/v1/admin/tours/{seed.ApprovedId}/review")).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        (await client.GetAsync("/api/v1/admin/tours/987654/review")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<List<JsonElement>> GetItemsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: url);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private sealed record Seed(long StaffId, long OlderPendingId, long NewerPendingId, long ApprovedId);

    private static async Task<Seed> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var staff = new User
        {
            Role = UserRole.Staff,
            Email = $"staff-{suffix}@tripmate.test",
            FullName = "Staff Queue",
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var operatorUser = new User
        {
            Role = UserRole.TourOperator,
            Email = $"op-{suffix}@tripmate.test",
            FullName = "Operator User",
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.AddRange(staff, operatorUser);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            CompanyName = "SQL Queue Tours",
            TaxCode = $"TAX-{suffix}",
            BusinessLicenseNo = $"BL-{suffix}",
            ContactPhone = "0901112223",
            ContactAddress = "Hoi An",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        var category = PoiCategory.Create("Heritage Site", null);
        context.PoiCategories.Add(category);
        var poi = PointOfInterest.Create(category, "Ancient Town", 15.88m, 108.33m, operatorUser.Id, now);
        context.PointsOfInterest.Add(poi);

        var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
        typeof(Destination).GetProperty(nameof(Destination.Name))!.SetValue(destination, "Hoi An");
        context.Destinations.Add(destination);

        var policy = CancellationPolicyTemplate.Create("Standard Policy", null, now,
            [CancellationPolicyTemplateRule.Create(30, 0), CancellationPolicyTemplateRule.Create(0, 100)]);
        context.CancellationPolicyTemplates.Add(policy);
        await context.SaveChangesAsync();

        Tour NewTour(string title) => Tour.CreateDraft(
            operatorUser.Id,
            categoryId: null,
            title,
            "A tour awaiting Staff review on SQL Server.",
            1_500_000m,
            durationDays: 1,
            destinationIds: [destination.Id],
            destinationSummary: "Hoi An",
            policy,
            [TourItineraryItem.Create(1, 1, poi.Id, new TimeOnly(8, 0), 120, null)],
            [TourSchedule.Create(now.AddDays(10), now.AddDays(11), "Hoi An Gate", 10, now, adultPrice: 1_500_000m)],
            now,
            "Breakfast and guide",
            "Personal expenses");

        var older = NewTour("Hoi An lantern night");
        older.SubmitForReview(now.AddHours(-5));
        var newer = NewTour("Mountain trek");
        newer.SubmitForReview(now.AddHours(-1));
        var approved = NewTour("Already approved tour");
        approved.SubmitForReview(now.AddHours(-9));
        approved.Approve(staff.Id, now.AddHours(-8));
        context.Tours.AddRange(older, newer, approved);
        await context.SaveChangesAsync();

        context.TourMedia.Add(DomainTourMedia.Create(
            older, "tripmate/pending/one", "https://res.cloudinary.com/tripmate/image/upload/pending.webp",
            null, "Pending tour photo", 1, true, now));
        var deleted = DomainTourMedia.Create(
            older, "tripmate/pending/two", "https://res.cloudinary.com/tripmate/image/upload/deleted.webp",
            null, "Deleted photo", 2, false, now);
        deleted.SoftDelete(now);
        context.TourMedia.Add(deleted);
        await context.SaveChangesAsync();

        return new Seed(staff.Id, older.Id, newer.Id, approved.Id);
    }
}