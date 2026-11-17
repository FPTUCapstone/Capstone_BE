using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.ServiceProviderRequests;

public sealed class ProviderRequestHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T04:00:00Z");

    [Fact]
    public async Task Direct_handler_call_denies_temporary_password_accounts()
    {
        await using var fixture = await Fixture.Create();
        (await fixture.Database.Users.FindAsync(30L))!.MustChangePassword = true;
        await fixture.Database.SaveChangesAsync();
        var handler = new GetServiceProviderRequestHandler(fixture.Port.Object, fixture.Current.Object);
        (await handler.Handle(new GetServiceProviderRequest(fixture.Booking.Id), default))
            .ErrorCode.Should().Be(ProviderRequestErrors.Forbidden);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("locked")]
    [InlineData("role")]
    public async Task Mutation_rechecks_account_authority_after_waiting_for_the_booking_lock(string change)
    {
        await using var fixture = await Fixture.Create();
        fixture.Lock.Setup(value => value.AcquireBookingAsync(fixture.Booking.Id, It.IsAny<CancellationToken>()))
            .Returns(async (long _, CancellationToken token) =>
            {
                var actor = (await fixture.Database.Users.FindAsync(30L))!;
                if (change == "password") actor.MustChangePassword = true;
                if (change == "locked") actor.Status = AccountStatus.Locked;
                if (change == "role") actor.Role = UserRole.Traveler;
                await fixture.Database.SaveChangesAsync(token);
            });
        var result = await fixture.DecisionHandler().Handle(new DecideServiceProviderRequest(
            fixture.Booking.Id, ServiceBooking.Confirmed, null,
            Convert.ToBase64String(fixture.Booking.RowVersion), Guid.NewGuid()), default);
        result.ErrorCode.Should().Be(ProviderRequestErrors.Forbidden);
        fixture.Booking.Status.Should().Be(ServiceBooking.PendingConfirmation);
        (await fixture.Database.AuditLogs.CountAsync()).Should().Be(0);
        (await fixture.Database.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Worker_rerun_expires_request_once_with_one_audit_and_notification()
    {
        await using var fixture = await Fixture.Create();
        typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.RequestSubmittedAtUtc))!
            .SetValue(fixture.Booking, Now.AddHours(-24));
        await fixture.Database.SaveChangesAsync();
        var worker = new ServiceRequestLifecycleProcessor(fixture.Port.Object, fixture.Lock.Object, fixture.Clock.Object);
        (await worker.ProcessAsync(default)).Should().Be(1);
        (await worker.ProcessAsync(default)).Should().Be(0);
        fixture.Booking.Status.Should().Be(ServiceBooking.Expired);
        (await fixture.Database.AuditLogs.CountAsync()).Should().Be(1);
        (await fixture.Database.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Linked_poi_owner_sees_legacy_provider_listing_and_other_provider_cannot()
    {
        await using var fixture = await Fixture.Create();
        var query = new ListServiceProviderRequests();
        var handler = new ListServiceProviderRequestsHandler(fixture.Port.Object, fixture.Current.Object);

        var result = await handler.Handle(query, default);
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().ServiceName.Should().Be("Legacy listing");

        fixture.Current.SetupGet(current => current.UserId).Returns(999);
        var other = await handler.Handle(query, default);
        other.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Decision_retry_replays_original_response_after_later_outcome_without_duplicate_events()
    {
        await using var fixture = await Fixture.Create();
        var handler = fixture.DecisionHandler();
        var key = Guid.NewGuid();
        var command = new DecideServiceProviderRequest(fixture.Booking.Id, "Confirmed", null,
            Convert.ToBase64String(fixture.Booking.RowVersion), key);
        var first = await handler.Handle(command, default);
        first.IsSuccess.Should().BeTrue();

        fixture.Booking.TryRecordOutcome("Completed", 99999m, 30, Now.AddDays(2));
        await fixture.Database.SaveChangesAsync();
        var retry = await handler.Handle(command, default);
        retry.Value.Should().BeEquivalentTo(first.Value);
        (await fixture.Database.AuditLogs.CountAsync()).Should().Be(1);
        (await fixture.Database.Notifications.CountAsync()).Should().Be(1);
        (await fixture.Database.Set<ServiceProviderRequestOperation>().CountAsync()).Should().Be(1);

        var mismatch = await handler.Handle(command with { Reason = "Changed" }, default);
        mismatch.ErrorCode.Should().Be(ProviderRequestErrors.IdempotencyMismatch);
    }

    [Fact]
    public async Task Stale_version_cannot_create_decision_or_durable_events()
    {
        await using var fixture = await Fixture.Create();
        var result = await fixture.DecisionHandler().Handle(new DecideServiceProviderRequest(
            fixture.Booking.Id, "Confirmed", null, "AAAAAAAAAAE=", Guid.NewGuid()), default);
        result.ErrorCode.Should().Be(ProviderRequestErrors.VersionConflict);
        (await fixture.Database.AuditLogs.CountAsync()).Should().Be(0);
        (await fixture.Database.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Requested_date_filter_uses_Vietnam_day_boundary()
    {
        await using var fixture = await Fixture.Create();
        var handler = new ListServiceProviderRequestsHandler(fixture.Port.Object, fixture.Current.Object);
        var match = await handler.Handle(new ListServiceProviderRequests(
            RequestedDateFrom: new DateOnly(2026, 11, 14), RequestedDateTo: new DateOnly(2026, 11, 14)), default);
        match.Value.TotalCount.Should().Be(1);
        var minimum = await handler.Handle(new ListServiceProviderRequests(RequestedDateFrom: DateOnly.MinValue), default);
        minimum.IsSuccess.Should().BeTrue();
        minimum.Value.TotalCount.Should().Be(1);
        var miss = await handler.Handle(new ListServiceProviderRequests(
            RequestedDateFrom: new DateOnly(2026, 11, 15)), default);
        miss.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task List_prioritizes_pending_then_earliest_date_with_stable_paging()
    {
        await using var fixture = await Fixture.Create();
        var later = ServiceBooking.CreatePendingRequest(10, fixture.Booking.ServiceId,
            fixture.Booking.StartAtUtc.AddDays(1), null, 1, 1000, Now);
        var earlierConfirmed = ServiceBooking.CreatePendingRequest(10, fixture.Booking.ServiceId,
            fixture.Booking.StartAtUtc.AddDays(-1), null, 1, 1000, Now);
        earlierConfirmed.TryDecide(ServiceBooking.Confirmed, null, 30, Now);
        fixture.Database.ServiceBookings.AddRange(later, earlierConfirmed);
        await fixture.Database.SaveChangesAsync();
        var handler = new ListServiceProviderRequestsHandler(fixture.Port.Object, fixture.Current.Object);
        var result = await handler.Handle(new ListServiceProviderRequests(), default);
        result.Value.Items.Select(item => item.Id).Should().Equal(fixture.Booking.Id, later.Id, earlierConfirmed.Id);
        var second = await handler.Handle(new ListServiceProviderRequests(PageNumber: 2, PageSize: 1), default);
        second.Value.Items.Single().Id.Should().Be(later.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ApplicationDbContext Database { get; } = new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Mock<IApplicationDbContext> Port { get; } = new();
        public Mock<ICurrentUserService> Current { get; } = new();
        public Mock<IDateTimeProvider> Clock { get; } = new();
        public Mock<IServiceProviderRequestLock> Lock { get; } = new();
        public ServiceBooking Booking { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            var db = fixture.Database;
            var provider = ServiceProvider.Create("New owner", "Hotel", "Active");
            typeof(ServiceProvider).GetProperty("UserId")!.SetValue(provider, (long?)30);
            var legacy = ServiceProvider.Create("Legacy provider", "Hotel", "Active");
            var service = CommercialService.Create(legacy, "Hotel", "Legacy listing", 1000, "PerNight",
                "Available", "VND", false, null, null, null, Now, poiId: 40);
            db.Users.AddRange(new User { Id = 30, FullName = "Provider", Role = UserRole.ServiceProvider, Status = AccountStatus.Active },
                new User { Id = 10, FullName = "Traveler", Email = "traveler@example.com", Status = AccountStatus.Active });
            db.CommercialServices.Add(service);
            db.ServiceProviders.Add(provider);
            await db.SaveChangesAsync();
            var poi = PointOfInterest.Create(PoiCategory.Create("Hotel", null), "Linked hotel", 16, 108, 30, Now);
            poi.Id = 40;
            var link = ProviderPoiLink.Create(provider, poi);
            db.Set<ProviderPoiLink>().Add(link);
            fixture.Booking = ServiceBooking.CreatePendingRequest(10, service.Id,
                DateTimeOffset.Parse("2026-11-13T18:00:00Z"), null, 1, 1000, Now);
            typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.RowVersion))!.SetValue(fixture.Booking, new byte[8]);
            db.ServiceBookings.Add(fixture.Booking);
            await db.SaveChangesAsync();
            fixture.Current.SetupGet(current => current.UserId).Returns(30);
            fixture.Current.SetupGet(current => current.Role).Returns("ServiceProvider");
            fixture.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);
            fixture.Port.SetupGet(port => port.Users).Returns(db.Users);
            fixture.Port.SetupGet(port => port.ServiceProviders).Returns(db.ServiceProviders);
            fixture.Port.SetupGet(port => port.ServiceBookings).Returns(db.ServiceBookings);
            fixture.Port.SetupGet(port => port.ProviderPoiLinks).Returns(db.Set<ProviderPoiLink>());
            fixture.Port.SetupGet(port => port.ServiceProviderRequestOperations).Returns(db.Set<ServiceProviderRequestOperation>());
            fixture.Port.SetupGet(port => port.AuditLogs).Returns(db.AuditLogs);
            fixture.Port.SetupGet(port => port.Notifications).Returns(db.Notifications);
            fixture.Port.Setup(port => port.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken token) => db.SaveChangesAsync(token));
            fixture.Port.Setup(port => port.ExecuteInTransactionAsync(
                    It.IsAny<Func<CancellationToken, Task<TripMate.Application.Common.Models.Result<ProviderRequestDetail>>>>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Func<CancellationToken, Task<TripMate.Application.Common.Models.Result<ProviderRequestDetail>>> action,
                    CancellationToken token) => action(token));
            fixture.Port.Setup(port => port.ExecuteInTransactionAsync(
                    It.IsAny<Func<CancellationToken, Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns((Func<CancellationToken, Task<bool>> action, CancellationToken token) => action(token));
            return fixture;
        }

        public DecideServiceProviderRequestHandler DecisionHandler() => new(Port.Object, Current.Object,
            new ProviderRequestMutationService(Port.Object, Lock.Object, Clock.Object));

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}