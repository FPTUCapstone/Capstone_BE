using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.AccountProvisioning;
using TripMate.Application.Features.Notifications.GetInbox;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.AccountProvisioning;

public sealed class AccountProvisioningConcurrencySqlServerTests
{
    [SqlServerFact]
    public async Task StaffProvisionedProviderReceivesNewBookingNoticeForExistingCatalog()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var db = database.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var staff = new User { FullName = "Staff", Email = "staff@example.test", Role = UserRole.Staff, Status = AccountStatus.Active };
        var traveler = new User { FullName = "Traveler", Email = "traveler@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
        db.Users.AddRange(staff, traveler);
        await db.SaveChangesAsync();

        var category = await db.PoiCategories.SingleOrDefaultAsync(item => item.Name == CommercialService.CategoryHotel)
            ?? PoiCategory.Create(CommercialService.CategoryHotel, null);
        var poi = PointOfInterest.Create(category, "Existing hotel", 16, 108, staff.Id, now);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        var listingOwner = ServiceProvider.Create("Legacy listing", CommercialService.CategoryHotel, ServiceProvider.StatusActive);
        var service = CommercialService.Create(listingOwner, CommercialService.CategoryHotel, "Double room", 120_000m,
            CommercialService.PriceUnitPerNight, CommercialService.AvailabilityAvailable, "VND", true,
            null, null, null, now, poiId: poi.Id, capacity: 2);
        db.CommercialServices.Add(service);
        await db.SaveChangesAsync();

        var email = new CountingEmailSender();
        var result = await Handler(db, staff, email).Handle(new CreateServiceProviderAccountCommand(
            "Hotel manager", "hotel-manager@example.test", "0901234567", "Existing hotel",
            CommercialService.CategoryHotel, [poi.Id]), default);
        result.IsSuccess.Should().BeTrue();
        email.Count.Should().Be(1);
        var providerUserId = result.Value.UserId;
        (await db.ProviderPoiLinks.SingleAsync()).PointOfInterestId.Should().Be(poi.Id);

        var account = await db.Users.SingleAsync(item => item.Id == providerUserId);
        account.MustChangePassword.Should().BeTrue();
        var first = ServiceBooking.CreatePendingRequest(traveler.Id, service.Id, now.AddDays(1), null, 1, service.PriceAmount, now);
        db.ServiceBookings.Add(first);
        await db.SaveChangesAsync();
        (await db.Notifications.AnyAsync(item => item.UserId == providerUserId)).Should().BeFalse();

        // Model the persisted account state after onboarding for this provisioning/notification test.
        account.MustChangePassword = false;
        await db.SaveChangesAsync();
        var second = ServiceBooking.CreatePendingRequest(traveler.Id, service.Id, now.AddDays(2), null, 1, service.PriceAmount, now);
        db.ServiceBookings.Add(second);
        await db.SaveChangesAsync();

        var inbox = await new GetNotificationInboxQueryHandler(db).Handle(new(providerUserId), default);
        var notice = inbox.Value.Items.Should().ContainSingle().Which;
        notice.Type.Should().Be("MSG205");
        notice.RelatedEntityId.Should().Be(second.Id);
        service.ProviderId.Should().Be(listingOwner.Id);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("email", ProvisioningErrors.DuplicateEmail)]
    [InlineData("phone", ProvisioningErrors.DuplicatePhone)]
    [InlineData("poi", ProvisioningErrors.PoiAlreadyLinked)]
    public async Task ConcurrentActorsCannotProvisionTheSameIdentifierOrPoi(string collision, string expectedCode)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var seed = database.CreateDbContext();
        var role = collision == "poi" ? UserRole.Staff : UserRole.Administrator;
        var actor1 = new User { FullName = "Actor 1", Email = "actor1@example.test", Role = role, Status = AccountStatus.Active };
        var actor2 = new User { FullName = "Actor 2", Email = "actor2@example.test", Role = role, Status = AccountStatus.Active };
        seed.Users.AddRange(actor1, actor2);
        await seed.SaveChangesAsync();
        long[] poiIds = [];
        if (collision == "poi")
        {
            var poi = PointOfInterest.Create(PoiCategory.Create("Hotel", null), "Hotel without legacy listing",
                16, 108, actor1.Id, DateTimeOffset.UtcNow);
            seed.PointsOfInterest.Add(poi);
            await seed.SaveChangesAsync();
            poiIds = [poi.Id];
        }

        await using var db1 = database.CreateDbContext();
        await using var db2 = database.CreateDbContext();
        var emailSender = new CountingEmailSender();
        var handler1 = Handler(db1, actor1, emailSender);
        var handler2 = Handler(db2, actor2, emailSender);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await start.Task;
            return await Create(handler1, "first@example.test", "0901234567");
        });
        var second = Task.Run(async () =>
        {
            await start.Task;
            return await Create(handler2, collision == "email" ? "FIRST@example.test" : "second@example.test",
                collision == "phone" ? "0901234567" : "0901234568");
        });
        start.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(45));
        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result => result.ErrorCode == expectedCode);
        emailSender.Count.Should().Be(1);
        await using var verify = database.CreateDbContext();
        (await verify.Users.CountAsync()).Should().Be(3);
        (await verify.AuditLogs.CountAsync()).Should().Be(1);
        if (collision == "poi") (await verify.ProviderPoiLinks.CountAsync()).Should().Be(1);

        Task<Result<ProvisionedAccountDto>> Create(AccountProvisioningHandler handler, string email, string phone) =>
            collision == "poi"
                ? handler.Handle(new CreateServiceProviderAccountCommand("Owner", email, phone, "Hotel", "Hotel", poiIds), default)
                : handler.Handle(new CreateStaffAccountCommand("Staff", email, phone), default);
    }

    private static AccountProvisioningHandler Handler(ApplicationDbContext db, User actor, CountingEmailSender email) =>
        new(db, new ActingUser(actor), new PasswordHasherService(), email, new SqlServerAccountProvisioningLock(db),
            new SqlServerAccountProvisioningPersistenceErrors(), new Clock(), NullLogger<AccountProvisioningHandler>.Instance);

    private sealed class ActingUser(User actor) : ICurrentUserService
    {
        public long? UserId => actor.Id;
        public string? Role => actor.Role.ToString();
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class CountingEmailSender : IAccountProvisioningEmailSender
    {
        private int count;
        public int Count => count;

        public Task<EmailDeliveryResult> SendTemporaryPasswordAsync(string email, string temporaryPassword,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(EmailDeliveryResult.Delivered);
        }
    }
}
