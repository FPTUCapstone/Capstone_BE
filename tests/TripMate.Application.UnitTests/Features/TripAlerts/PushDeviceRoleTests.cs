using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.PushDevices;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripAlerts;

public sealed class PushDeviceRoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private static RegisterPushDeviceCommandHandler Handler(TestDbContext db) =>
        new(db, new Protector(), new FakeDateTimeProvider { UtcNow = Now });

    private static async Task SeedOperatorAsync(
        TestDbContext db,
        long id,
        OperatorApprovalStatus approval,
        AccountStatus status = AccountStatus.Active)
    {
        var user = User(id, UserRole.TourOperator, status);
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = id,
            User = user,
            CompanyName = $"Operator {id}",
            TaxCode = $"TAX-{id}",
            BusinessLicenseNo = $"LICENSE-{id}",
            ApprovalStatus = approval,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Register_ApprovedOperator_StoresOnlyProtectedToken()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorAsync(db, 1, OperatorApprovalStatus.Approved);

        var result = await Handler(db).Handle(
            new RegisterPushDeviceCommand(1, Guid.NewGuid(), "raw-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var stored = await db.PushDeviceTokens.SingleAsync();
        stored.UserId.Should().Be(1);
        stored.ProtectedToken.Should().NotBe("raw-token");
        stored.TokenHash.Should().NotContain("raw-token");
    }

    [Theory]
    [InlineData(OperatorApprovalStatus.PendingApproval, AccountStatus.Active)]
    [InlineData(OperatorApprovalStatus.Rejected, AccountStatus.Active)]
    [InlineData(OperatorApprovalStatus.Approved, AccountStatus.Locked)]
    public async Task Register_IneligibleOperator_IsDenied(
        OperatorApprovalStatus approval,
        AccountStatus status)
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorAsync(db, 1, approval, status);

        var result = await Handler(db).Handle(
            new RegisterPushDeviceCommand(1, Guid.NewGuid(), "raw-token"), CancellationToken.None);

        result.ErrorCode.Should().Be(TripAlertErrorCodes.AccessDenied);
        (await db.PushDeviceTokens.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Staff)]
    public async Task Register_AdministrationRole_IsDenied(UserRole role)
    {
        await using var db = TestDbContext.Create();
        db.Users.Add(User(1, role));
        await db.SaveChangesAsync();

        var result = await Handler(db).Handle(
            new RegisterPushDeviceCommand(1, Guid.NewGuid(), "raw-token"), CancellationToken.None);

        result.ErrorCode.Should().Be(TripAlertErrorCodes.AccessDenied);
    }

    [Fact]
    public async Task Register_TravelerIsUnchanged()
    {
        await using var db = TestDbContext.Create();
        db.Users.Add(User(1, UserRole.Traveler));
        await db.SaveChangesAsync();

        (await Handler(db).Handle(
            new RegisterPushDeviceCommand(1, Guid.NewGuid(), "raw-token"), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Register_ReplayOfSameTokenKeepsOneRow()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorAsync(db, 1, OperatorApprovalStatus.Approved);
        var installation = Guid.NewGuid();

        await Handler(db).Handle(new RegisterPushDeviceCommand(1, installation, "t"), CancellationToken.None);
        await Handler(db).Handle(new RegisterPushDeviceCommand(1, installation, "t"), CancellationToken.None);

        (await db.PushDeviceTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Register_RotationKeepsTheInstallationAndDropsTheOldToken()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorAsync(db, 1, OperatorApprovalStatus.Approved);
        var installation = Guid.NewGuid();

        await Handler(db).Handle(new RegisterPushDeviceCommand(1, installation, "old"), CancellationToken.None);
        await Handler(db).Handle(new RegisterPushDeviceCommand(1, installation, "new"), CancellationToken.None);

        var stored = await db.PushDeviceTokens.SingleAsync();
        stored.ProtectedToken.Should().Be("protected:new");
    }

    [Fact]
    public async Task Register_AccountSwitchMovesThePhysicalTokenToTheNewOwnerOnly()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorAsync(db, 1, OperatorApprovalStatus.Approved);
        db.Users.Add(User(2, UserRole.Traveler));
        await db.SaveChangesAsync();

        await Handler(db).Handle(
            new RegisterPushDeviceCommand(1, Guid.NewGuid(), "device-token"), CancellationToken.None);
        await Handler(db).Handle(
            new RegisterPushDeviceCommand(2, Guid.NewGuid(), "device-token"), CancellationToken.None);

        var stored = await db.PushDeviceTokens.SingleAsync();
        stored.UserId.Should().Be(2);
    }

    [Fact]
    public async Task Remove_ForeignOperatorCannotDeactivateAnotherOperatorsDevice()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorAsync(db, 1, OperatorApprovalStatus.Approved);
        await SeedOperatorAsync(db, 2, OperatorApprovalStatus.Approved);
        var installation = Guid.NewGuid();
        await Handler(db).Handle(new RegisterPushDeviceCommand(1, installation, "t"), CancellationToken.None);

        var remove = new RemovePushDeviceCommandHandler(db, new FakeDateTimeProvider { UtcNow = Now });
        (await remove.Handle(new RemovePushDeviceCommand(2, installation), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        (await db.PushDeviceTokens.SingleAsync()).DeactivatedAtUtc.Should().BeNull();
    }

    private static User User(long id, UserRole role, AccountStatus status = AccountStatus.Active) => new()
    {
        Id = id,
        Email = $"push-role-{id}@example.com",
        FullName = $"Push {id}",
        Role = role,
        Status = status,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    private sealed class Protector : IPushTokenProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue[10..];
    }
}
