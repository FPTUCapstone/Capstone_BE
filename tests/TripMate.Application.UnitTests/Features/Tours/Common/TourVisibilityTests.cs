using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Tours.Shared;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Tours.Common;

public sealed class TourVisibilityTests
{
    private static readonly DateTimeOffset Now = PublicTourTestData.Now;

    [Theory]
    [InlineData(TourStatus.Published, true)]
    [InlineData(TourStatus.Draft, false)]
    [InlineData(TourStatus.Pending, false)]
    [InlineData(TourStatus.Rejected, false)]
    [InlineData(TourStatus.Approved, false)]
    [InlineData(TourStatus.Inactive, false)]
    public async Task PublicTour_DependsOnLifecycleStatus(TourStatus status, bool expectedVisible)
    {
        await using var db = TestDbContext.Create();
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await PublicTourTestData.SeedOperatorAsync(db, 7);
        var tour = await PublicTourTestData.AddTourAsync(db, 7, status);

        var visible = await db.Tours.Where(TourVisibility.PublicTour(Now)).Select(item => item.Id).ToListAsync();

        visible.Contains(tour.Id).Should().Be(expectedVisible);
    }

    [Fact]
    public async Task PublicTour_ApprovedWithAPastPublicationDate_IsVisible()
    {
        await using var db = TestDbContext.Create();
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await PublicTourTestData.SeedOperatorAsync(db, 7);
        var tour = await PublicTourTestData.AddApprovedWithPublicationDateAsync(db, 7, Now.AddMinutes(-1));

        (await db.Tours.Where(TourVisibility.PublicTour(Now)).Select(item => item.Id).ToListAsync())
            .Should().Contain(tour.Id);
    }

    [Fact]
    public async Task PublicTour_ApprovedWithAFuturePublicationDate_IsNotVisibleYet()
    {
        await using var db = TestDbContext.Create();
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await PublicTourTestData.SeedOperatorAsync(db, 7);
        var tour = await PublicTourTestData.AddApprovedWithPublicationDateAsync(db, 7, Now.AddMinutes(1));

        (await db.Tours.Where(TourVisibility.PublicTour(Now)).Select(item => item.Id).ToListAsync())
            .Should().NotContain(tour.Id);
    }

    [Theory]
    [InlineData(OperatorApprovalStatus.PendingApproval, AccountStatus.Active)]
    [InlineData(OperatorApprovalStatus.Rejected, AccountStatus.Active)]
    [InlineData(OperatorApprovalStatus.Approved, AccountStatus.Locked)]
    public async Task PublicTour_HidesToursOfAnUnapprovedOrInactiveOperator(
        OperatorApprovalStatus approval, AccountStatus accountStatus)
    {
        await using var db = TestDbContext.Create();
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await PublicTourTestData.SeedOperatorAsync(db, 7, approval, accountStatus);
        var tour = await PublicTourTestData.AddTourAsync(db, 7, TourStatus.Published);

        (await db.Tours.Where(TourVisibility.PublicTour(Now)).Select(item => item.Id).ToListAsync())
            .Should().NotContain(tour.Id);
    }
}