using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.List;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.List;

public sealed class ListOperatorToursQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsOnlyCurrentOperatorsTours()
    {
        await using var db = TestDbContext.Create();
        var owner = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        var other = await SeedApprovedOperatorAsync(db, 8);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var create = new CreateTourPackageCommandHandler(db,
            new FakeCurrentUserService { UserId = owner.Id }, clock);
        await create.Handle(Command(88, clock.UtcNow), CancellationToken.None);
        var handler = new ListOperatorToursQueryHandler(db,
            new FakeCurrentUserService { UserId = other.Id });

        var result = await handler.Handle(new ListOperatorToursQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_FiltersByStatusAndPagesNewestFirst()
    {
        await using var db = TestDbContext.Create();
        var owner = await SeedApprovedOperatorAsync(db, 7);
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await SeedPoiAsync(db, 88);
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };
        var user = new FakeCurrentUserService { UserId = owner.Id };
        var create = new CreateTourPackageCommandHandler(db, user, clock);
        for (var index = 0; index < 3; index++)
        {
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            (await create.Handle(Command(88, clock.UtcNow), CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        var tours = await db.Tours.OrderBy(tour => tour.Id).ToListAsync();
        tours[0].SubmitForReview(clock.UtcNow);
        await db.SaveChangesAsync();
        var handler = new ListOperatorToursQueryHandler(db, user);

        var drafts = await handler.Handle(new ListOperatorToursQuery("draft"), CancellationToken.None);
        var pending = await handler.Handle(new ListOperatorToursQuery("Pending"), CancellationToken.None);
        var firstPage = await handler.Handle(new ListOperatorToursQuery(null, 1, 2), CancellationToken.None);
        var secondPage = await handler.Handle(new ListOperatorToursQuery(null, 2, 2), CancellationToken.None);

        drafts.Value.TotalCount.Should().Be(2);
        drafts.Value.Items.Should().OnlyContain(item => item.Status == "Draft");
        pending.Value.Items.Should().ContainSingle(item => item.Id == tours[0].Id);
        firstPage.Value.TotalCount.Should().Be(3);
        firstPage.Value.Items.Should().HaveCount(2);
        secondPage.Value.Items.Should().HaveCount(1);
        firstPage.Value.Items.Concat(secondPage.Value.Items).Select(item => item.Id).Should().OnlyHaveUniqueItems();
        firstPage.Value.Items[0].Destination.Should().Be("Da Nang");
    }

    [Theory]
    [InlineData("Nonsense", 1, 20, false)]
    [InlineData("1", 1, 20, false)]
    [InlineData("Draft", 0, 20, false)]
    [InlineData("Draft", 1, 0, false)]
    [InlineData("Draft", 1, 101, false)]
    [InlineData("Draft", 1, 100, true)]
    [InlineData(null, 1, 20, true)]
    [InlineData("", 1, 20, true)]
    public void Validator_RejectsBadStatusAndPaging(string? status, int page, int pageSize, bool valid)
    {
        new ListOperatorToursQueryValidator().Validate(new ListOperatorToursQuery(status, page, pageSize))
            .IsValid.Should().Be(valid);
    }

    private static CreateTourPackageCommand Command(long poiId, DateTimeOffset now) => new(
        "Da Nang day tour", "A full day visiting Da Nang landmarks.", null, 1_250_000m, 1,
        [TourAuthoringTestData.DestinationId], TourAuthoringTestData.PolicyTemplateId, null, null,
        [new CreateTourItineraryItemRequest(1, 1, poiId, new TimeOnly(8, 0), 90, null)],
        [new CreateTourScheduleRequest(now.AddDays(2), now.AddDays(2).AddHours(8), "City center", 20)],
        Guid.NewGuid());

    private static async Task<User> SeedApprovedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User { Id = id, Email = $"{id}@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile { UserId = id, User = user, CompanyName = "Tour Co", TaxCode = $"Tax{id}", BusinessLicenseNo = $"Lic{id}", ApprovalStatus = OperatorApprovalStatus.Approved });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task SeedPoiAsync(TestDbContext db, long id)
    {
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, id);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Status))!.SetValue(poi, PointOfInterestStatus.Active);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
    }
}