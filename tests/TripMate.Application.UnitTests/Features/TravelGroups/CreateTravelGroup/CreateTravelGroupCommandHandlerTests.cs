using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.CreateTravelGroup;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.TravelGroups.CreateTravelGroup;

/**
 * [UC-17] Unit tests for CreateTravelGroupCommandHandler
 */
public class CreateTravelGroupCommandHandlerTests
{
    private readonly FakeDateTimeProvider _dateTimeProvider = new();

    [Fact]
    public async Task Handle_WithNonExistentItinerary_ReturnsItineraryNotFound()
    {
        await using var dbContext = TestDbContext.Create();
        var handler = new CreateTravelGroupCommandHandler(dbContext, _dateTimeProvider);

        var command = new CreateTravelGroupCommand(999, "Da Nang Trip", 1);
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.ItineraryNotFound);
    }

    [Fact]
    public async Task Handle_WhenItineraryBelongsToAnotherTraveler_ReturnsItineraryNotFound()
    {
        await using var dbContext = TestDbContext.Create();
        var itinerary = new Itinerary
        {
            TravelerUserId = 100,
            Title = "Another Traveler Trip",
            Status = "Active",
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            UpdatedAtUtc = _dateTimeProvider.UtcNow
        };
        dbContext.Itineraries.Add(itinerary);
        await dbContext.SaveChangesAsync();

        var handler = new CreateTravelGroupCommandHandler(dbContext, _dateTimeProvider);
        var command = new CreateTravelGroupCommand(itinerary.Id, "Shared Trip Group", 200);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.ItineraryNotFound);
    }

    [Fact]
    public async Task Handle_WithValidRequest_CreatesGroupWithHostWithoutInvitation()
    {
        await using var dbContext = TestDbContext.Create();

        // Seed user and itinerary
        var user = new User
        {
            Email = "traveler@example.com",
            FullName = "Nguyen Van A",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var itinerary = new Itinerary
        {
            TravelerUserId = user.Id,
            Title = "Da Nang Beach Day",
            Status = "Active",
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            UpdatedAtUtc = _dateTimeProvider.UtcNow
        };
        dbContext.Itineraries.Add(itinerary);
        await dbContext.SaveChangesAsync();

        var handler = new CreateTravelGroupCommandHandler(dbContext, _dateTimeProvider);
        var command = new CreateTravelGroupCommand(itinerary.Id, "Da Nang Summer Trip", user.Id);

        var result = await handler.Handle(command, CancellationToken.None);

        // Assert Result contract
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.GroupName.Should().Be("Da Nang Summer Trip");
        result.Value.ItineraryId.Should().Be(itinerary.Id);
        result.Value.HostUserId.Should().Be(user.Id);

        // Assert Database persistence
        var groupInDb = await dbContext.TravelGroups
            .Include(g => g.GroupMembers)
            .Include(g => g.GroupInvitations)
            .FirstOrDefaultAsync(g => g.Id == result.Value.GroupId);

        groupInDb.Should().NotBeNull();
        groupInDb!.Name.Should().Be("Da Nang Summer Trip");
        groupInDb.HostUserId.Should().Be(user.Id);

        // Assert Creator is initial Host member
        groupInDb.GroupMembers.Should().HaveCount(1);
        var hostMember = groupInDb.GroupMembers.First();
        hostMember.UserId.Should().Be(user.Id);
        hostMember.Status.Should().Be(GroupMemberStatus.Active);
        hostMember.LocationSharingEnabled.Should().BeFalse();

        // UC-17 creates only the group and initial Host membership.
        // Invitation generation belongs to UC-18.
        groupInDb.GroupInvitations.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenHostMembershipPersistenceFails_DoesNotRetainTravelGroup()
    {
        var databaseName = Guid.NewGuid().ToString();
        var seedOptions = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        await using (var seedContext = new TestDbContext(seedOptions))
        {
            seedContext.Itineraries.Add(new Itinerary
            {
                TravelerUserId = 200,
                Title = "Da Nang Trip",
                Status = "Active",
                CreatedAtUtc = _dateTimeProvider.UtcNow,
                UpdatedAtUtc = _dateTimeProvider.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        await using (var failingContext = new FailingSaveChangesTestDbContext(seedOptions))
        {
            var itinerary = await failingContext.Itineraries.SingleAsync();
            var handler = new CreateTravelGroupCommandHandler(failingContext, _dateTimeProvider);
            var command = new CreateTravelGroupCommand(itinerary.Id, "Da Nang Group", 200);

            await FluentActions.Invoking(() => handler.Handle(command, CancellationToken.None))
                .Should().ThrowAsync<DbUpdateException>();
        }

        await using var verificationContext = new TestDbContext(seedOptions);
        (await verificationContext.TravelGroups.CountAsync()).Should().Be(0);
    }
}

internal sealed class FailingSaveChangesTestDbContext(DbContextOptions<TestDbContext> options)
    : TestDbContext(options)
{
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new DbUpdateException("Simulated GroupMember persistence failure.");
}
