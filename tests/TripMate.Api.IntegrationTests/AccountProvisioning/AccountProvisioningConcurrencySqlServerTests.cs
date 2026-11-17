using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.AccountProvisioning;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.AccountProvisioning;

public sealed class AccountProvisioningConcurrencySqlServerTests
{
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