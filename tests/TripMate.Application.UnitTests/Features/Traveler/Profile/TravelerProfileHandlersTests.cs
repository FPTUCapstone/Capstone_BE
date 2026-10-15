using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using Moq;

using TripMate.Application.Common.Media;
using TripMate.Application.Features.Traveler.Profile.Common;
using TripMate.Application.Features.Traveler.Profile.Get;
using TripMate.Application.Features.Traveler.Profile.Update;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Profile;

public sealed class TravelerProfileHandlersTests
{
    private const long TravelerId = 70001;
    private readonly TestDbContext db = TestDbContext.Create();
    private readonly FakeCurrentUserService currentUser = new()
    {
        UserId = TravelerId,
        Role = nameof(UserRole.Traveler),
    };
    private readonly FakeDateTimeProvider clock = new()
    {
        UtcNow = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Update_ActiveTraveler_UpdatesIdentityAndCreatesProfile()
    {
        db.Users.Add(ActiveTraveler());
        await db.SaveChangesAsync();

        var result = await new UpdateTravelerProfileCommandHandler(db, currentUser, clock,
            new Mock<ITravelerAvatarStorage>().Object, new Mock<ITravelerAvatarCleanupJournal>().Object)
            .Handle(new UpdateTravelerProfileCommand("  Nguyen Van A  ", " 0912345678 ", new DateOnly(2000, 1, 1)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.MessageCode.Should().Be(TravelerProfileErrorCodes.Success);
        var user = db.Users.Single(candidate => candidate.Id == TravelerId);
        user.FullName.Should().Be("Nguyen Van A");
        user.PhoneNumber.Should().Be("0912345678");
        user.Email.Should().Be("traveler@example.com");
        user.Role.Should().Be(UserRole.Traveler);
        var profile = db.TravelerProfiles.Single(candidate => candidate.UserId == TravelerId);
        profile.DateOfBirth.Should().Be(new DateOnly(2000, 1, 1));
    }

    [Fact]
    public async Task Update_ConcurrentFirstProfileCreate_RetriesAsUpdate()
    {
        db.Users.Add(ActiveTraveler());
        await db.SaveChangesAsync();

        var duplicate = new DbUpdateException("Duplicate TravelerProfile.",
            new SqlException(2627,
                "Violation of PRIMARY KEY constraint 'PK_TravelerProfiles'. Cannot insert duplicate key in object 'dbo.TravelerProfiles'."));
        db.ThrowOnTransaction = duplicate;
        db.OnTransactionCompleted = () =>
        {
            if (!ReferenceEquals(db.ThrowOnTransaction, duplicate)) return;

            db.ThrowOnTransaction = null;
            db.TravelerProfiles.Add(TravelerProfile.Create(TravelerId, null, clock.UtcNow));
            db.SaveChanges();
        };

        var result = await new UpdateTravelerProfileCommandHandler(db, currentUser, clock,
            new Mock<ITravelerAvatarStorage>().Object, new Mock<ITravelerAvatarCleanupJournal>().Object)
            .Handle(new UpdateTravelerProfileCommand("Updated Traveler", "0912345678", new DateOnly(2000, 1, 1)),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.TransactionExecutionCount.Should().Be(2);
        db.TravelerProfiles.Should().ContainSingle(candidate => candidate.UserId == TravelerId)
            .Which.DateOfBirth.Should().Be(new DateOnly(2000, 1, 1));
        db.Users.Single(candidate => candidate.Id == TravelerId).FullName.Should().Be("Updated Traveler");
    }

    [Fact]
    public async Task Update_DuplicateOutsideTravelerProfile_DoesNotRetry()
    {
        db.Users.Add(ActiveTraveler());
        await db.SaveChangesAsync();
        db.ThrowOnTransaction = new DbUpdateException("Duplicate user.",
            new SqlException(2627,
                "Violation of UNIQUE KEY constraint 'UX_Users_email'. Cannot insert duplicate key in object 'dbo.Users'."));

        var result = await new UpdateTravelerProfileCommandHandler(db, currentUser, clock,
            new Mock<ITravelerAvatarStorage>().Object, new Mock<ITravelerAvatarCleanupJournal>().Object)
            .Handle(new UpdateTravelerProfileCommand("Updated Traveler", null, null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelerProfileErrorCodes.Unavailable);
        db.TransactionExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task Update_LockedTraveler_ReturnsForbiddenWithoutMutation()
    {
        var user = ActiveTraveler();
        user.Status = AccountStatus.Locked;
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await new UpdateTravelerProfileCommandHandler(db, currentUser, clock,
            new Mock<ITravelerAvatarStorage>().Object, new Mock<ITravelerAvatarCleanupJournal>().Object)
            .Handle(new UpdateTravelerProfileCommand("Changed", "0912345678", null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TravelerProfileErrorCodes.Forbidden);
        db.TravelerProfiles.Should().BeEmpty();
        db.Users.Single().FullName.Should().Be("Original Traveler");
    }

    [Fact]
    public async Task Update_PhoneOwnedByAnotherUser_ReturnsCatalogConflictBeforeAvatarUpload()
    {
        db.Users.Add(ActiveTraveler());
        db.Users.Add(new User
        {
            Id = TravelerId + 1,
            FullName = "Another Traveler",
            Email = "another@example.com",
            PhoneNumber = "0912345678",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        });
        await db.SaveChangesAsync();
        var storage = new Mock<ITravelerAvatarStorage>();
        var journal = new Mock<ITravelerAvatarCleanupJournal>();

        var result = await new UpdateTravelerProfileCommandHandler(db, currentUser, clock,
            storage.Object, journal.Object)
            .Handle(new UpdateTravelerProfileCommand("Updated", "0912345678", null,
                [137, 80, 78, 71, 13, 10, 26, 10], "image/png", false), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelerProfileErrorCodes.PhoneAlreadyExists);
        db.Users.Single(candidate => candidate.Id == TravelerId).FullName.Should().Be("Original Traveler");
        storage.VerifyNoOtherCalls();
        journal.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_ActiveTraveler_ReturnsPersistedDateOfBirth()
    {
        db.Users.Add(ActiveTraveler());
        db.TravelerProfiles.Add(TravelerProfile.Create(TravelerId, null, clock.UtcNow));
        await db.SaveChangesAsync();
        db.TravelerProfiles.Single().UpdateDateOfBirth(new DateOnly(1999, 5, 1), clock.UtcNow);
        await db.SaveChangesAsync();

        var result = await new GetTravelerProfileQueryHandler(db, currentUser)
            .Handle(new GetTravelerProfileQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DateOfBirth.Should().Be(new DateOnly(1999, 5, 1));
    }

    [Fact]
    public async Task Update_NewAvatar_ReservesBeforeUploadAndFinalizesAfterSave()
    {
        var user = ActiveTraveler();
        user.AvatarPublicId = "tripmate/avatars/70001/old";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        const string newId = "tripmate/avatars/70001/new";
        var events = new List<string>();
        var storage = new Mock<ITravelerAvatarStorage>();
        var journal = new Mock<ITravelerAvatarCleanupJournal>();
        storage.Setup(s => s.AllocatePublicId(TravelerId)).Returns(newId);
        journal.Setup(j => j.ReserveAsync(newId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("reserved")).Returns(Task.CompletedTask);
        storage.Setup(s => s.UploadAsync(It.IsAny<TravelerAvatarUpload>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("uploaded"))
            .ReturnsAsync(TravelerAvatarUploadResult.Succeeded(new Uri("https://res.cloudinary.com/avatar.png")));

        var result = await new UpdateTravelerProfileCommandHandler(db, currentUser, clock,
            storage.Object, journal.Object)
            .Handle(new UpdateTravelerProfileCommand("Nguyen Van A", null, null,
                [137, 80, 78, 71, 13, 10, 26, 10], "image/png", false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        events.Should().Equal("reserved", "uploaded");
        db.FinalizedTravelerAvatarPublicIds.Should().ContainSingle().Which.Should().Be(newId);
        db.EnqueuedTravelerAvatarPublicIds.Should().ContainSingle().Which.Should().Be("tripmate/avatars/70001/old");
        db.Users.Single().AvatarPublicId.Should().Be(newId);
    }

    [Fact]
    public async Task Update_UploadedAvatarThenSaveFails_KeepsCleanupReservationForRetry()
    {
        db.Users.Add(ActiveTraveler());
        await db.SaveChangesAsync();
        db.ThrowOnSaveConcurrency = true;
        const string newId = "tripmate/avatars/70001/new";
        var storage = new Mock<ITravelerAvatarStorage>();
        var journal = new Mock<ITravelerAvatarCleanupJournal>();
        storage.Setup(s => s.AllocatePublicId(TravelerId)).Returns(newId);
        journal.Setup(j => j.ReserveAsync(newId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        journal.Setup(j => j.RetryNowAsync(newId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        storage.Setup(s => s.UploadAsync(It.IsAny<TravelerAvatarUpload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TravelerAvatarUploadResult.Succeeded(new Uri("https://res.cloudinary.com/avatar.png")));

        var result = await new UpdateTravelerProfileCommandHandler(db, currentUser, clock,
            storage.Object, journal.Object)
            .Handle(new UpdateTravelerProfileCommand("Nguyen Van A", null, null,
                [137, 80, 78, 71, 13, 10, 26, 10], "image/png", false), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelerProfileErrorCodes.Unavailable);
        journal.Verify(j => j.RetryNowAsync(newId, It.IsAny<CancellationToken>()), Times.Once);
        db.FinalizedTravelerAvatarPublicIds.Should().BeEmpty();
    }

    private static User ActiveTraveler() => new()
    {
        Id = TravelerId,
        FullName = "Original Traveler",
        Email = "traveler@example.com",
        Role = UserRole.Traveler,
        Status = AccountStatus.Active,
    };

    private sealed class SqlException(int number, string message) : Exception(message)
    {
        public int Number { get; } = number;
    }
}