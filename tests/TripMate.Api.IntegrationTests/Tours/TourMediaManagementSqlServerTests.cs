using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TourMedia.Delete;
using TripMate.Application.Features.TourMedia.Reorder;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Tours;

[Collection(nameof(TripMateApiFactory))]
public sealed class TourMediaManagementSqlServerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 27, 13, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ReorderThenDelete_PreservesFilteredIndexesOutboxAndAudit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        await using (var context = database.CreateDbContext())
        {
            var reorder = await new ReorderTourMediaCommandHandler(
                context, new FixedDateTimeProvider(), new SqlServerTourMediaUploadLock(context))
                .Handle(new ReorderTourMediaCommand(
                    seed.TourId, seed.ActorUserId, [seed.SecondMediaId, seed.FirstMediaId], seed.SecondMediaId),
                    CancellationToken.None);
            reorder.IsSuccess.Should().BeTrue();
        }

        await using (var context = database.CreateDbContext())
        {
            var deletion = await new DeleteTourMediaCommandHandler(
                context, new FixedDateTimeProvider(), new SqlServerTourMediaUploadLock(context))
                .Handle(new DeleteTourMediaCommand(seed.TourId, seed.SecondMediaId, seed.ActorUserId),
                    CancellationToken.None);
            deletion.IsSuccess.Should().BeTrue();
        }

        await using var verification = database.CreateDbContext();
        var remaining = await verification.TourMedia.SingleAsync(media => media.Id == seed.FirstMediaId);
        var deleted = await verification.TourMedia
            .IgnoreQueryFilters()
            .SingleAsync(media => media.Id == seed.SecondMediaId);
        remaining.SortOrder.Should().Be(1);
        remaining.IsPrimary.Should().BeFalse();
        deleted.LifecycleStatus.Should().Be(TourMediaLifecycleStatus.Deleted);
        deleted.IsPrimary.Should().BeFalse();
        (await verification.TourMediaCleanupOutbox.CountAsync()).Should().Be(1);
        (await verification.AuditLogs.CountAsync()).Should().Be(2);
    }

    private static async Task<(long ActorUserId, long TourId, long FirstMediaId, long SecondMediaId)> SeedAsync(
        SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var user = new User
        {
            Email = $"tour-media-manage-{Guid.NewGuid():N}@example.com",
            FullName = "Approved operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            CompanyName = "TripMate Test Operator",
            TaxCode = $"TAX{user.Id}{Guid.NewGuid():N}",
            BusinessLicenseNo = $"LIC{user.Id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });
        var tour = CreateTour(user.Id);
        context.Tours.Add(tour);
        await context.SaveChangesAsync();

        var first = TourMedia.Create(
            tour,
            $"tripmate/tours/{tour.Id}/one",
            "https://res.cloudinary.com/tripmate/image/upload/one.webp",
            null,
            "First accessible image",
            1,
            true,
            Now);
        var second = TourMedia.Create(
            tour,
            $"tripmate/tours/{tour.Id}/two",
            "https://res.cloudinary.com/tripmate/image/upload/two.webp",
            null,
            "Second accessible image",
            2,
            false,
            Now);
        context.TourMedia.AddRange(first, second);
        await context.SaveChangesAsync();
        return (user.Id, tour.Id, first.Id, second.Id);
    }

    private static Tour CreateTour(long operatorUserId)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), "Media management tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Draft);
        Set(tour, nameof(Tour.CreatedAtUtc), Now);
        Set(tour, nameof(Tour.UpdatedAtUtc), Now);
        return tour;
    }

    private static void Set<T>(object instance, string propertyName, T value) =>
        instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }
}