using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.GetInvitation;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TravelGroups.ManageInvitation;

public sealed class GroupInvitationCommandHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new();
    private readonly RecordingGroupInvitationLock _lock = new();

    [Fact]
    public async Task GetOrCreate_WhenCallerIsNotHost_ReturnsHostPermissionRequired()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var handler = CreateGetOrCreateHandler(db);

        var result = await handler.Handle(
            new GetOrCreateGroupInvitationCommand(group.Id, group.HostUserId + 1, Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.HostPermissionRequired);
    }

    [Fact]
    public async Task GetOrCreate_WhenUsableInvitationExists_ReturnsItWithoutCreatingAnother()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "CURRENT01",
            _clock.UtcNow.AddDays(10),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var result = await CreateGetOrCreateHandler(db).Handle(
            new GetOrCreateGroupInvitationCommand(group.Id, group.HostUserId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.InviteCode.Should().Be("CURRENT01");
        (await db.GroupInvitations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreate_WhenSeveralUsableInvitationsExist_KeepsNewestAndExpiresTheOthers()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var older = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "OLDER001",
            _clock.UtcNow.AddDays(10),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow.AddMinutes(-1));
        var newer = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "NEWER002",
            _clock.UtcNow.AddDays(10),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.AddRange(older, newer);
        await db.SaveChangesAsync();

        var result = await CreateGetOrCreateHandler(db).Handle(
            new GetOrCreateGroupInvitationCommand(group.Id, group.HostUserId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.InviteCode.Should().Be("NEWER002");
        older.ExpiresAtUtc.Should().Be(_clock.UtcNow);
        newer.IsUsableAt(_clock.UtcNow).Should().BeTrue();
    }

    [Fact]
    public async Task Regenerate_ExpiresCurrentInvitationAndCreatesReplacement()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var current = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "CURRENT01",
            _clock.UtcNow.AddDays(10),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(current);
        await db.SaveChangesAsync();

        var result = await CreateRegenerateHandler(db).Handle(
            new RegenerateGroupInvitationCommand(group.Id, group.HostUserId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.InviteCode.Should().NotBe("CURRENT01");
        current.ExpiresAtUtc.Should().Be(_clock.UtcNow);
        (await db.GroupInvitations.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GetOrCreate_WhenIdempotencyKeyIsRetried_ReturnsTheOriginalInvitation()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var key = Guid.NewGuid();
        var handler = CreateGetOrCreateHandler(db);

        var first = await handler.Handle(
            new GetOrCreateGroupInvitationCommand(group.Id, group.HostUserId, key),
            CancellationToken.None);
        var retry = await handler.Handle(
            new GetOrCreateGroupInvitationCommand(group.Id, group.HostUserId, key),
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        retry.IsSuccess.Should().BeTrue();
        retry.Value.InviteCode.Should().Be(first.Value.InviteCode);
        (await db.GroupInvitationOperations.CountAsync()).Should().Be(1);
        _lock.AcquiredResources.Should().Contain((group.Id, group.HostUserId, key));
    }

    [Fact]
    public async Task GetOrCreate_CreatesAnEightCharacterInvitationForThirtyDaysWithoutBusinessQuota()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);

        var result = await CreateGetOrCreateHandler(db).Handle(
            new GetOrCreateGroupInvitationCommand(group.Id, group.HostUserId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.InviteCode.Should().HaveLength(TravelGroupConstants.InvitationCodeLength);
        result.Value.ExpiresAt.Should().Be(_clock.UtcNow.AddDays(TravelGroupConstants.InvitationCodeExpiryDays));
        var invitation = await db.GroupInvitations.SingleAsync();
        invitation.MaxUses.Should().Be(TravelGroupConstants.UnlimitedInvitationUses);
    }

    [Fact]
    public async Task GetOrCreate_AcquiresCandidateCodeLockBeforeGroupLock()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var generator = new SequenceGroupInvitationCodeGenerator("LOCKCODE");
        var handler = new GetOrCreateGroupInvitationCommandHandler(db, _clock, _lock, generator);

        var result = await handler.Handle(
            new GetOrCreateGroupInvitationCommand(group.Id, group.HostUserId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _lock.AcquisitionOrder.Should().StartWith("code:LOCKCODE", $"group:{group.Id}");
    }

    [Fact]
    public async Task GetOrCreate_WhenGeneratedCodeCollides_RetriesWithAnotherCode()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        db.GroupInvitations.Add(GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "COLLIDE1",
            _clock.UtcNow.AddDays(-1),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow.AddDays(-30)));
        await db.SaveChangesAsync();
        var generator = new SequenceGroupInvitationCodeGenerator("COLLIDE1", "UNIQUE02");
        var handler = new GetOrCreateGroupInvitationCommandHandler(db, _clock, _lock, generator);

        var result = await handler.Handle(
            new GetOrCreateGroupInvitationCommand(group.Id, group.HostUserId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.InviteCode.Should().Be("UNIQUE02");
        generator.GeneratedCodes.Take(2).Should().Equal("COLLIDE1", "UNIQUE02");
    }

    [Fact]
    public async Task GetOrCreate_WhenKeyWasUsedForDifferentOperation_ReturnsPayloadMismatch()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var key = Guid.NewGuid();
        var handler = CreateGetOrCreateHandler(db);

        _ = await handler.Handle(
            new GetOrCreateGroupInvitationCommand(group.Id, group.HostUserId, key),
            CancellationToken.None);
        var mismatch = await CreateRegenerateHandler(db).Handle(
            new RegenerateGroupInvitationCommand(group.Id, group.HostUserId, key),
            CancellationToken.None);

        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch);
    }

    [Fact]
    public async Task GetOrCreate_WhenKeyWasUsedForDifferentGroup_ReturnsPayloadMismatch()
    {
        await using var db = TestDbContext.Create();
        var firstGroup = await AddGroupAsync(db);
        var secondGroup = await AddGroupAsync(db, firstGroup.HostUserId);
        var key = Guid.NewGuid();

        _ = await CreateGetOrCreateHandler(db).Handle(
            new GetOrCreateGroupInvitationCommand(firstGroup.Id, firstGroup.HostUserId, key),
            CancellationToken.None);
        var mismatch = await CreateGetOrCreateHandler(db).Handle(
            new GetOrCreateGroupInvitationCommand(secondGroup.Id, secondGroup.HostUserId, key),
            CancellationToken.None);

        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch);
    }

    private GetOrCreateGroupInvitationCommandHandler CreateGetOrCreateHandler(TestDbContext db) =>
        new(db, _clock, _lock, new RandomGroupInvitationCodeGenerator());

    private RegenerateGroupInvitationCommandHandler CreateRegenerateHandler(TestDbContext db) =>
        new(db, _clock, _lock, new RandomGroupInvitationCodeGenerator());

    private async Task<TravelGroup> AddGroupAsync(TestDbContext db, long? existingHostUserId = null)
    {
        var hostUserId = existingHostUserId;
        if (!hostUserId.HasValue)
        {
            var host = new User
            {
                Email = $"host-{Guid.NewGuid():N}@example.com",
                FullName = "Invitation Host",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
            };
            db.Users.Add(host);
            await db.SaveChangesAsync();
            hostUserId = host.Id;
        }

        var group = TravelGroup.Create(1, hostUserId.Value, "Invitation Test Group", _clock.UtcNow);
        db.TravelGroups.Add(group);
        await db.SaveChangesAsync();
        return group;
    }
}

internal sealed class RecordingGroupInvitationLock : IGroupInvitationLock
{
    public List<(long GroupId, long TravelerUserId, Guid IdempotencyKey)> AcquiredResources { get; } = [];

    public List<string> AcquisitionOrder { get; } = [];

    public Task AcquireAsync(long groupId, long travelerUserId, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        AcquiredResources.Add((groupId, travelerUserId, idempotencyKey));
        AcquisitionOrder.Add($"group:{groupId}");
        return Task.CompletedTask;
    }

    public Task AcquireCodeAsync(string inviteCode, CancellationToken cancellationToken)
    {
        AcquisitionOrder.Add($"code:{inviteCode}");
        return Task.CompletedTask;
    }
}

internal sealed class SequenceGroupInvitationCodeGenerator(params string[] codes) : IGroupInvitationCodeGenerator
{
    private readonly Queue<string> _codes = new(codes);

    public List<string> GeneratedCodes { get; } = [];

    public string Generate()
    {
        var code = _codes.Count > 0 ? _codes.Dequeue() : $"ZZZZ{GeneratedCodes.Count:D4}";
        GeneratedCodes.Add(code);
        return code;
    }
}