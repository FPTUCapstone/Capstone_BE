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
    public async Task Handle_WithValidRequest_CreatesGroupWithHostAndInviteCode()
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
        result.Value.InviteCode.Should().NotBeNullOrWhiteSpace();
        result.Value.InviteCode.Length.Should().Be(8);

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

        // Assert 30-day Invitation code generated
        groupInDb.GroupInvitations.Should().HaveCount(1);
        var invitation = groupInDb.GroupInvitations.First();
        invitation.InviteCode.Should().Be(result.Value.InviteCode);
        invitation.CreatedBy.Should().Be(user.Id);
        invitation.ExpiresAtUtc.Should().Be(_dateTimeProvider.UtcNow.AddDays(30));
        invitation.MaxUses.Should().Be(50);
        invitation.UsedCount.Should().Be(0);
    }
}