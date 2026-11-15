using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.PushDevices;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripAlerts;

public sealed class PushDeviceCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Register_SameInstallationForAnotherAccountTransfersOwnershipAndTokenRotation()
    {
        await using var db = TestDbContext.Create();
        db.Users.AddRange(User(1), User(2));
        await db.SaveChangesAsync();
        var installationId = Guid.NewGuid();
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var handler = new RegisterPushDeviceCommandHandler(db, new Protector(), clock);

        (await handler.Handle(
            new RegisterPushDeviceCommand(1, installationId, "token-one"), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        clock.UtcNow = Now.AddMinutes(1);
        (await handler.Handle(
            new RegisterPushDeviceCommand(2, installationId, "token-two"), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        var stored = await db.PushDeviceTokens.SingleAsync();
        stored.UserId.Should().Be(2);
        stored.InstallationId.Should().Be(installationId);
        stored.ProtectedToken.Should().Be("protected:token-two");
        stored.TokenHash.Should().NotContain("token-two");
        stored.UpdatedAtUtc.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public async Task Remove_IsIdempotentAndDeactivatesOnlyCurrentUsersInstallation()
    {
        await using var db = TestDbContext.Create();
        db.Users.AddRange(User(1), User(2));
        await db.SaveChangesAsync();
        var installationId = Guid.NewGuid();
        var register = new RegisterPushDeviceCommandHandler(
            db, new Protector(), new FakeDateTimeProvider { UtcNow = Now });
        await register.Handle(
            new RegisterPushDeviceCommand(1, installationId, "token"), CancellationToken.None);
        var remove = new RemovePushDeviceCommandHandler(
            db, new FakeDateTimeProvider { UtcNow = Now.AddMinutes(1) });

        (await remove.Handle(new RemovePushDeviceCommand(2, installationId), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await db.PushDeviceTokens.SingleAsync()).DeactivatedAtUtc.Should().BeNull();
        (await remove.Handle(new RemovePushDeviceCommand(1, installationId), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await remove.Handle(new RemovePushDeviceCommand(1, installationId), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await db.PushDeviceTokens.SingleAsync()).DeactivatedAtUtc.Should().Be(Now.AddMinutes(1));
    }

    private static User User(long id) => new()
    {
        Id = id,
        Email = $"push-{id}@example.com",
        FullName = $"Push {id}",
        Role = UserRole.Traveler,
        Status = AccountStatus.Active,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    private sealed class Protector : IPushTokenProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue[10..];
    }
}