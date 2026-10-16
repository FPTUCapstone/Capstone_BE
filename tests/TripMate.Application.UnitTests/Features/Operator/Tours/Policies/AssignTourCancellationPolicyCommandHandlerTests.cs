using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.Policies;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Policies;

public sealed class AssignTourCancellationPolicyCommandHandlerTests
{
    [Fact]
    public async Task Handle_ForOwnedDraft_SnapshotsActivePolicyRules()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var policy = CancellationPolicyTemplate.Create("Strict", "Strict cancellation terms", DateTimeOffset.UtcNow,
            [CancellationPolicyTemplateRule.Create(45, 0), CancellationPolicyTemplateRule.Create(0, 100)]);
        typeof(CancellationPolicyTemplate).GetProperty(nameof(CancellationPolicyTemplate.Id))!.SetValue(policy, 2L);
        db.CancellationPolicyTemplates.Add(policy);
        await db.SaveChangesAsync();
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var created = await new CreateTourPackageCommandHandler(db, new FakeCurrentUserService { UserId = actor.Id }, clock)
            .Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var handler = new AssignTourCancellationPolicyCommandHandler(db,
            new FakeCurrentUserService { UserId = actor.Id }, clock);

        var result = await handler.Handle(new AssignTourCancellationPolicyCommand(created.Value.TourId, policy.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.Tours.SingleAsync()).CancellationPolicyTemplateId.Should().Be(policy.Id);
        (await db.TourCancellationPolicyRules.Select(rule => rule.DaysBeforeDeparture).ToListAsync())
            .Should().BeEquivalentTo([45, 0]);
    }

    private static CreateTourPackageCommand Command(long poiId, DateTimeOffset now) => new("Da Nang day tour", "Description", null,
        1_000_000m, 1, [TourAuthoringTestData.DestinationId], TourAuthoringTestData.PolicyTemplateId, null, null,
        [new CreateTourItineraryItemRequest(1, 1, poiId, null, 60, null)],
        [new CreateTourScheduleRequest(now.AddDays(2), now.AddDays(2).AddHours(8), null, 20)], Guid.NewGuid());
    private static async Task<User> SeedApprovedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User { Id = id, Email = $"{id}@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user); db.OperatorProfiles.Add(new OperatorProfile { UserId = id, User = user, CompanyName = "Tour Co", TaxCode = "Tax7", BusinessLicenseNo = "Lic7", ApprovalStatus = OperatorApprovalStatus.Approved });
        await db.SaveChangesAsync(); return user;
    }
    private static async Task SeedPoiAsync(TestDbContext db, long id)
    {
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, id);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!.SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi); await db.SaveChangesAsync();
    }
}