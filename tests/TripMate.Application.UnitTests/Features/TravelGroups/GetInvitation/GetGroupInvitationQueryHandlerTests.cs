using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.GetInvitation;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.TravelGroups.GetInvitation;

/**
 * [UC-18] Unit tests for GetGroupInvitationQueryHandler
 */
public class GetGroupInvitationQueryHandlerTests
{
    private readonly FakeDateTimeProvider _dateTimeProvider = new();

    [Fact]
    public async Task Handle_WhenGroupDoesNotExist_ReturnsGroupNotFound()
    {
        await using var dbContext = TestDbContext.Create();
        var handler = new GetGroupInvitationQueryHandler(dbContext, _dateTimeProvider);

        var query = new GetGroupInvitationQuery(GroupId: 999, CurrentUserId: 1);
        var result = await handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.GroupNotFound);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotHost_ReturnsHostPermissionRequired()
    {
        await using var dbContext = TestDbContext.Create();

        var host = new User
        {
            Email = "host@example.com",
            FullName = "Host User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active
        };
        dbContext.Users.Add(host);
        await dbContext.SaveChangesAsync();

        var group = new TravelGroup
        {
            HostUserId = host.Id,
            Name = "Da Nang Trip",
            CreatedAtUtc = _dateTimeProvider.UtcNow
        };
        dbContext.TravelGroups.Add(group);
        await dbContext.SaveChangesAsync();

        var handler = new GetGroupInvitationQueryHandler(dbContext, _dateTimeProvider);
        var query = new GetGroupInvitationQuery(GroupId: group.Id, CurrentUserId: host.Id + 999);

        var result = await handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.HostPermissionRequired);
    }

    [Fact]
    public async Task Handle_WhenActiveInvitationExists_ReturnsExistingInvitationWithoutInsertingNewRow()
    {
        await using var dbContext = TestDbContext.Create();

        var host = new User
        {
            Email = "host@example.com",
            FullName = "Host User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active
        };
        dbContext.Users.Add(host);
        await dbContext.SaveChangesAsync();

        var group = new TravelGroup
        {
            HostUserId = host.Id,
            Name = "Da Nang Summer Trip",
            CreatedAtUtc = _dateTimeProvider.UtcNow
        };
        dbContext.TravelGroups.Add(group);
        await dbContext.SaveChangesAsync();

        var existingExpiry = _dateTimeProvider.UtcNow.AddDays(15);
        var activeInvitation = new GroupInvitation
        {
            GroupId = group.Id,
            InviteCode = "ABC123XY",
            CreatedBy = host.Id,
            ExpiresAtUtc = existingExpiry,
            MaxUses = 50,
            UsedCount = 5,
            CreatedAtUtc = _dateTimeProvider.UtcNow
        };
        dbContext.GroupInvitations.Add(activeInvitation);
        await dbContext.SaveChangesAsync();

        var handler = new GetGroupInvitationQueryHandler(dbContext, _dateTimeProvider);
        var query = new GetGroupInvitationQuery(GroupId: group.Id, CurrentUserId: host.Id);

        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.GroupId.Should().Be(group.Id);
        result.Value.GroupName.Should().Be("Da Nang Summer Trip");
        result.Value.InviteCode.Should().Be("ABC123XY");
        result.Value.QrData.Should().Be("tripmate://groups/join?code=ABC123XY");
        result.Value.ExpiresAt.Should().Be(existingExpiry);

        var totalInvitations = await dbContext.GroupInvitations.CountAsync(i => i.GroupId == group.Id);
        totalInvitations.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenInvitationMissing_GeneratesAndPersistsNewValidInvitation()
    {
        await using var dbContext = TestDbContext.Create();

        var host = new User
        {
            Email = "host@example.com",
            FullName = "Host User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active
        };
        dbContext.Users.Add(host);
        await dbContext.SaveChangesAsync();

        var group = new TravelGroup
        {
            HostUserId = host.Id,
            Name = "Hanoi Autumn Journey",
            CreatedAtUtc = _dateTimeProvider.UtcNow
        };
        dbContext.TravelGroups.Add(group);
        await dbContext.SaveChangesAsync();

        var handler = new GetGroupInvitationQueryHandler(dbContext, _dateTimeProvider);
        var query = new GetGroupInvitationQuery(GroupId: group.Id, CurrentUserId: host.Id);

        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.GroupId.Should().Be(group.Id);
        result.Value.GroupName.Should().Be("Hanoi Autumn Journey");
        result.Value.InviteCode.Should().NotBeNullOrWhiteSpace();
        result.Value.InviteCode.Length.Should().Be(TravelGroupConstants.InviteCodeLength);
        result.Value.QrData.Should().Be($"tripmate://groups/join?code={result.Value.InviteCode}");
        result.Value.ExpiresAt.Should().Be(_dateTimeProvider.UtcNow.AddDays(TravelGroupConstants.InviteCodeExpirationDays));

        var invitationInDb = await dbContext.GroupInvitations.FirstOrDefaultAsync(i => i.GroupId == group.Id);
        invitationInDb.Should().NotBeNull();
        invitationInDb!.InviteCode.Should().Be(result.Value.InviteCode);
        invitationInDb.CreatedBy.Should().Be(host.Id);
        invitationInDb.MaxUses.Should().Be(TravelGroupConstants.DefaultMaxUses);
        invitationInDb.UsedCount.Should().Be(0);
        invitationInDb.ExpiresAtUtc.Should().Be(_dateTimeProvider.UtcNow.AddDays(TravelGroupConstants.InviteCodeExpirationDays));
    }

    [Fact]
    public async Task Handle_WhenInvitationExpired_GeneratesAndPersistsNewValidInvitation()
    {
        await using var dbContext = TestDbContext.Create();

        var host = new User
        {
            Email = "host@example.com",
            FullName = "Host User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active
        };
        dbContext.Users.Add(host);
        await dbContext.SaveChangesAsync();

        var group = new TravelGroup
        {
            HostUserId = host.Id,
            Name = "Phu Quoc Island",
            CreatedAtUtc = _dateTimeProvider.UtcNow.AddDays(-60)
        };
        dbContext.TravelGroups.Add(group);
        await dbContext.SaveChangesAsync();

        // Expired invitation
        var expiredInvitation = new GroupInvitation
        {
            GroupId = group.Id,
            InviteCode = "EXPIRED1",
            CreatedBy = host.Id,
            ExpiresAtUtc = _dateTimeProvider.UtcNow.AddDays(-1), // Expired yesterday
            MaxUses = 50,
            UsedCount = 2,
            CreatedAtUtc = _dateTimeProvider.UtcNow.AddDays(-31)
        };
        dbContext.GroupInvitations.Add(expiredInvitation);
        await dbContext.SaveChangesAsync();

        var handler = new GetGroupInvitationQueryHandler(dbContext, _dateTimeProvider);
        var query = new GetGroupInvitationQuery(GroupId: group.Id, CurrentUserId: host.Id);

        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.InviteCode.Should().NotBe("EXPIRED1");
        result.Value.InviteCode.Length.Should().Be(TravelGroupConstants.InviteCodeLength);
        result.Value.QrData.Should().Be($"tripmate://groups/join?code={result.Value.InviteCode}");
        result.Value.ExpiresAt.Should().Be(_dateTimeProvider.UtcNow.AddDays(TravelGroupConstants.InviteCodeExpirationDays));

        // Group now has 2 invitations in DB: 1 old expired, 1 new active
        var invitationsInDb = await dbContext.GroupInvitations.Where(i => i.GroupId == group.Id).ToListAsync();
        invitationsInDb.Should().HaveCount(2);

        // Verify that group membership is unaffected (AC-04)
        var members = await dbContext.GroupMembers.Where(m => m.GroupId == group.Id).ToListAsync();
        members.Should().BeEmpty();
    }
}