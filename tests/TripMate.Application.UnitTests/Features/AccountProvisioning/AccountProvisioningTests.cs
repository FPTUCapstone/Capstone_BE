using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.AccountProvisioning;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.AccountProvisioning;

public sealed class AccountProvisioningTests
{
    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.ServiceProvider)]
    public async Task NonAdministratorCannotCreateStaff(UserRole role)
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, role);
        var email = new CapturingEmailSender();
        var result = await Handler(db, email).Handle(new CreateStaffAccountCommand("Staff", "staff@example.test", "0901234567"), default);
        result.ErrorCode.Should().Be(ProvisioningErrors.Forbidden);
        email.Password.Should().BeNull();
        db.Users.Should().ContainSingle();
    }

    [Theory]
    [InlineData(EmailDeliveryStatus.DefiniteFailure)]
    [InlineData(EmailDeliveryStatus.Unknown)]
    public async Task DeliveryFailureReportsCommittedAccountMetadata(EmailDeliveryStatus status)
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Administrator);
        var email = new CapturingEmailSender { Outcome = new EmailDeliveryResult(status) };
        var result = await Handler(db, email).Handle(new CreateStaffAccountCommand("Staff", "staff@example.test", "0901234567"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.EmailDeliveryStatus.Should().Be(status);
        db.Users.Should().HaveCount(2);
        db.AuditLogs.Should().ContainSingle();
    }

    [Fact]
    public async Task DatabaseFailurePropagatesBeforeAnyEmailAttempt()
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Administrator);
        var email = new CapturingEmailSender();
        db.SaveChangesFailureFactory = () => new DbUpdateException("Write failure");
        var action = () => Handler(db, email).Handle(new CreateStaffAccountCommand("Staff", "staff@example.test", "0901234567"), default);
        await action.Should().ThrowAsync<DbUpdateException>();
        email.Password.Should().BeNull();
    }

    [Fact]
    public async Task DeadlockRetriesFromFreshTrackedStateAndSendsEmailOnce()
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Administrator);
        var attempts = 0;
        db.SaveChangesFailureFactory = () => ++attempts == 1 ? new SimulatedDeadlockException() : null;
        var email = new CapturingEmailSender();
        var result = await Handler(db, email).Handle(new CreateStaffAccountCommand("Staff", "staff@example.test", "0901234567"), default);
        result.IsSuccess.Should().BeTrue();
        db.Users.Should().HaveCount(2);
        db.AuditLogs.Should().ContainSingle();
        email.SendCount.Should().Be(1);
    }

    [Fact]
    public async Task ExhaustedDeadlocksReturnUnavailableWithoutSendingEmail()
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Administrator);
        db.SaveChangesFailureFactory = () => new SimulatedDeadlockException();
        var email = new CapturingEmailSender();
        var result = await Handler(db, email).Handle(new CreateStaffAccountCommand("Staff", "staff@example.test", "0901234567"), default);
        result.ErrorCode.Should().Be("MSG127");
        email.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task KnownUniqueViolationReloadsConcurrentWinnerAndReturnsDuplicateEmail()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = ProvisioningDbContext.CreateProvisioning(databaseName);
        await SeedActor(db, UserRole.Administrator);
        db.SaveChangesFailureFactory = () =>
        {
            using var concurrent = ProvisioningDbContext.CreateProvisioning(databaseName);
            concurrent.Users.Add(new User
            {
                FullName = "Winner",
                Email = "staff@example.test",
                Role = UserRole.Staff,
                Status = AccountStatus.Active,
                MustChangePassword = true
            });
            concurrent.SaveChanges();
            return new DbUpdateException("Simulated named unique conflict");
        };
        var email = new CapturingEmailSender();
        var result = await Handler(db, email, new FixedEmailConflict()).Handle(
            new CreateStaffAccountCommand("Staff", "staff@example.test", "0901234567"), default);
        result.ErrorCode.Should().Be(ProvisioningErrors.DuplicateEmail);
        email.SendCount.Should().Be(0);
        (await db.Users.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ClassifiedUniqueFailureWithoutPersistedCollisionStillPropagates()
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Administrator);
        db.SaveChangesFailureFactory = () => new DbUpdateException("Simulated named unique conflict");
        var email = new CapturingEmailSender();
        var action = () => Handler(db, email, new FixedEmailConflict()).Handle(
            new CreateStaffAccountCommand("Staff", "staff@example.test", "0901234567"), default);
        await action.Should().ThrowAsync<DbUpdateException>();
        email.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task DuplicateNormalizedEmailCannotCreateAnotherAccount()
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Administrator);
        var email = new CapturingEmailSender();
        var result = await Handler(db, email).Handle(new CreateStaffAccountCommand("Staff", " ACTOR@EXAMPLE.TEST ", "0901234567"), default);
        result.ErrorCode.Should().Be(ProvisioningErrors.DuplicateEmail);
        email.Password.Should().BeNull();
        db.Users.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Beach", false)]
    [InlineData("Hotel", true)]
    public async Task InactiveAndNonCommercialPoisAreRejected(string categoryName, bool deactivate)
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Staff);
        var poi = PointOfInterest.Create(PoiCategory.Create(categoryName, null), "POI", 16, 108, 1, new FakeDateTimeProvider().UtcNow);
        if (deactivate) poi.TryDeactivate(new FakeDateTimeProvider().UtcNow);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        var email = new CapturingEmailSender();
        var handler = Handler(db, email);
        (await handler.Handle(new GetAvailableProviderPoisQuery(), default)).Value.Should().BeEmpty();
        var result = await handler.Handle(new CreateServiceProviderAccountCommand("Owner", "owner@example.test", "0901234567",
            "Business", "Hotel", [poi.Id]), default);
        result.ErrorCode.Should().Be(ProvisioningErrors.InvalidPoi);
        db.ServiceProviders.Should().BeEmpty();
        email.Password.Should().BeNull();
    }

    [Fact]
    public async Task ActiveCommercialPoiWithoutLegacyListingIsAvailableAndCanBeProvisioned()
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Staff);
        var poi = PointOfInterest.Create(PoiCategory.Create("Hotel", null), "New hotel", 16, 108, 1,
            new FakeDateTimeProvider().UtcNow);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        var handler = Handler(db, new CapturingEmailSender());
        var available = await handler.Handle(new GetAvailableProviderPoisQuery(), default);
        available.Value.Should().ContainSingle(x => x.Id == poi.Id);
        var result = await handler.Handle(new CreateServiceProviderAccountCommand("Owner", "owner@example.test",
            "0901234567", "Hotel business", "Hotel", [poi.Id]), default);
        result.IsSuccess.Should().BeTrue();
        var provider = await db.ServiceProviders.Include(x => x.PoiLinks).Include(x => x.User).SingleAsync();
        provider.User!.Id.Should().Be(result.Value.UserId);
        provider.User.Role.Should().Be(UserRole.ServiceProvider);
        provider.PoiLinks.Should().ContainSingle(x => x.PointOfInterestId == poi.Id);
        db.CommercialServices.Should().BeEmpty();
        (await handler.Handle(new GetAvailableProviderPoisQuery(), default)).Value.Should().BeEmpty();
        var second = await handler.Handle(new CreateServiceProviderAccountCommand("Second owner", "second@example.test",
            "0901234568", "Another business", "Restaurant", [poi.Id]), default);
        second.ErrorCode.Should().Be(ProvisioningErrors.PoiAlreadyLinked);
        db.ServiceProviders.Should().ContainSingle();
    }

    [Fact]
    public async Task VehicleRentalCategoryMapsToExistingProviderCategoryAndAllowsOtherCommercialPoiCategories()
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Staff);
        var vehicle = PointOfInterest.Create(PoiCategory.Create("Vehicle Rental", null), "Rental", 16, 108, 1, new FakeDateTimeProvider().UtcNow);
        var restaurant = PointOfInterest.Create(PoiCategory.Create("Restaurant", null), "Restaurant", 16, 108, 1, new FakeDateTimeProvider().UtcNow);
        db.PointsOfInterest.AddRange(vehicle, restaurant);
        await db.SaveChangesAsync();
        var handler = Handler(db, new CapturingEmailSender());
        (await handler.Handle(new GetAvailableProviderPoisQuery("Vehicle Rental"), default)).Value
            .Should().ContainSingle(x => x.Id == vehicle.Id);
        var result = await handler.Handle(new CreateServiceProviderAccountCommand("Owner", "owner@example.test", "0901234567",
            "Business", "Vehicle Rental", [vehicle.Id, restaurant.Id, vehicle.Id]), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedPoiIds.Should().HaveCount(2);
        (await db.ServiceProviders.SingleAsync()).ServiceCategory.Should().Be(CommercialService.CategoryVehicle);
    }

    [Fact]
    public async Task AdministratorCreatesActiveStaffWithHashAndSecretFreeAuditAndResponse()
    {
        await using var db = ProvisioningDbContext.CreateProvisioning();
        await SeedActor(db, UserRole.Administrator);
        var email = new CapturingEmailSender();

        var result = await Handler(db, email).Handle(new CreateStaffAccountCommand(
            "New Staff", " STAFF@example.test ", "0901234567"), default);

        result.IsSuccess.Should().BeTrue();
        var user = await db.Users.SingleAsync(x => x.Email == "staff@example.test");
        user.Role.Should().Be(UserRole.Staff);
        user.Status.Should().Be(AccountStatus.Active);
        user.MustChangePassword.Should().BeTrue();
        user.PasswordHash.Should().Be(new FakePasswordHasher().Hash(email.Password!));
        email.SawCommittedAccount.Should().BeTrue();
        var audit = await db.AuditLogs.SingleAsync();
        audit.ActorUserId.Should().Be(1);
        audit.AffectedEntityId.Should().Be(user.Id);
        var serialized = JsonSerializer.Serialize(result.Value) + JsonSerializer.Serialize(audit);
        serialized.Should().NotContain(email.Password!).And.NotContain(user.PasswordHash!);
    }

    private static AccountProvisioningHandler Handler(ProvisioningDbContext db, CapturingEmailSender email,
        IAccountProvisioningPersistenceErrors? persistenceErrors = null)
    {
        email.Database = db;
        return new(db, new FakeCurrentUserService { UserId = 1 }, new FakePasswordHasher(),
            email, new NoOpProvisioningLock(), persistenceErrors ?? new NoUniqueConflicts(), new FakeDateTimeProvider(), NullLogger<AccountProvisioningHandler>.Instance);
    }

    private sealed class NoOpProvisioningLock : IAccountProvisioningLock
    {
        public Task AcquireAsync(string normalizedEmail, string normalizedPhone, IReadOnlyCollection<long> poiIds,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoUniqueConflicts : IAccountProvisioningPersistenceErrors
    {
        public string? GetUniqueConflictCode(DbUpdateException exception) => null;
    }

    private sealed class FixedEmailConflict : IAccountProvisioningPersistenceErrors
    {
        public string? GetUniqueConflictCode(DbUpdateException exception) => ProvisioningErrors.DuplicateEmail;
    }

    private static async Task SeedActor(ProvisioningDbContext db, UserRole role)
    {
        db.Users.Add(new User
        {
            Id = 1,
            FullName = "Acting user",
            Email = "actor@example.test",
            Role = role,
            Status = AccountStatus.Active
        });
        await db.SaveChangesAsync();
    }

    private sealed class CapturingEmailSender : IAccountProvisioningEmailSender
    {
        public ProvisioningDbContext Database { get; set; } = null!;
        public string? Password { get; private set; }
        public bool SawCommittedAccount { get; private set; }
        public int SendCount { get; private set; }
        public EmailDeliveryResult Outcome { get; set; } = EmailDeliveryResult.Delivered;

        public async Task<EmailDeliveryResult> SendTemporaryPasswordAsync(string email, string temporaryPassword,
            CancellationToken cancellationToken)
        {
            SendCount++;
            Password = temporaryPassword;
            SawCommittedAccount = await Database.Users.AnyAsync(x => x.Email == email, cancellationToken)
                && await Database.AuditLogs.AnyAsync(cancellationToken);
            return Outcome;
        }
    }

    private sealed class ProvisioningDbContext(DbContextOptions<TestDbContext> options) : TestDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<ProviderPoiLink>().HasKey(x => new { x.ServiceProviderId, x.PointOfInterestId });
            builder.Entity<ProviderPoiLink>().HasOne(x => x.Provider).WithMany(x => x.PoiLinks)
                .HasForeignKey(x => x.ServiceProviderId);
            builder.Entity<ProviderPoiLink>().HasOne(x => x.Poi).WithMany()
                .HasForeignKey(x => x.PointOfInterestId);
        }

        public static ProvisioningDbContext CreateProvisioning(string? databaseName = null) => new(
            new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString()).Options);
    }
}