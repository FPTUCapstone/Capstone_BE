using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using ServiceProvider = TripMate.Domain.Entities.ServiceProvider;

namespace TripMate.Api.IntegrationTests.ServiceProviderRequests;

[Collection(nameof(TripMateApiFactory))]
public sealed class ServiceProviderRequestEndpointTests
{
    private const string Route = "/api/v1/provider/service-booking-requests";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T04:00:00Z");

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Administrator)]
    public async Task List_requires_provider_role(UserRole role)
    {
        using var factory = Factory();
        using var client = factory.CreateAuthenticatedClient(30, role);
        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Seeded_request_list_detail_decision_and_replay_use_real_HTTP_contract()
    {
        using var factory = Factory();
        var id = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(30, UserRole.ServiceProvider);
        var page = await client.GetFromJsonAsync<ProviderRequestPage>(Route);
        page!.TotalCount.Should().Be(1);
        page.Items.Single().Option.Should().Be("Double room");
        var detail = await client.GetFromJsonAsync<ProviderRequestDetail>($"{Route}/{id}");
        detail!.SpecialRequest.Should().Be("Late arrival");
        detail.TravelerEmail.Should().Be("traveler@example.test");
        detail.RequestedDate.Should().Be(new DateOnly(2026, 11, 15));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var body = new { decision = ServiceBooking.Confirmed, rowVersion = detail.RowVersion };
        var first = await client.PostAsJsonAsync($"{Route}/{id}/decision", body);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await client.PostAsJsonAsync($"{Route}/{id}/decision", body);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<ProviderRequestDetail>())
            .Should().BeEquivalentTo(await first.Content.ReadFromJsonAsync<ProviderRequestDetail>());
        await factory.WithDbContextAsync(async db =>
        {
            (await db.AuditLogs.CountAsync()).Should().Be(1);
            (await db.Notifications.CountAsync()).Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task Detail_conceals_other_providers_contacts_and_invalid_header_is_rejected()
    {
        using var factory = Factory();
        var id = await SeedAsync(factory);
        await factory.WithDbContextAsync(async db =>
        {
            var account = new User { Id = 31, Role = UserRole.ServiceProvider, Status = AccountStatus.Active, FullName = "Other" };
            var other = ServiceProvider.Create("Other", "Hotel", "Active");
            other.AssignAccount(account);
            db.ServiceProviders.Add(other);
            await db.SaveChangesAsync();
            return true;
        });
        using var otherClient = factory.CreateAuthenticatedClient(31, UserRole.ServiceProvider);
        var hidden = await otherClient.GetAsync($"{Route}/{id}");
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await hidden.Content.ReadAsStringAsync()).Should().NotContain("traveler@example.test");
        using var owner = factory.CreateAuthenticatedClient(30, UserRole.ServiceProvider);
        var missingKey = await owner.PostAsJsonAsync($"{Route}/{id}/decision", new
        { decision = ServiceBooking.Confirmed, rowVersion = "AAAAAAAAAAA=" });
        missingKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static TripMateApiFactory Factory() => new(dateTimeProviderFactory: _ => new FixedClock(),
        configureTestServices: services =>
        {
            services.RemoveAll<IServiceProviderRequestLock>();
            services.AddSingleton<IServiceProviderRequestLock, InMemoryLock>();
        });

    private static Task<long> SeedAsync(TripMateApiFactory factory) => factory.WithDbContextAsync(async db =>
    {
        var account = new User { Id = 30, Role = UserRole.ServiceProvider, Status = AccountStatus.Active, FullName = "Provider" };
        var traveler = new User
        {
            Id = 10,
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            FullName = "Traveler",
            Email = "traveler@example.test",
            PhoneNumber = "0901234567"
        };
        db.Users.AddRange(account, traveler);
        var provider = ServiceProvider.Create("Linked provider", "Hotel", "Active");
        provider.AssignAccount(account);
        var poi = PointOfInterest.Create(PoiCategory.Create("Hotel", null), "Hotel", 16, 108, account.Id, Now);
        db.ProviderPoiLinks.Add(ProviderPoiLink.Create(provider, poi));
        await db.SaveChangesAsync();
        var legacy = ServiceProvider.Create("Legacy listing provider", "Hotel", "Active");
        var service = CommercialService.Create(legacy, "Hotel", "Room", 123456.78m, "PerNight",
            "Available", "VND", false, null, null, null, Now, poiId: poi.Id);
        db.CommercialServices.Add(service);
        await db.SaveChangesAsync();
        var request = ServiceBooking.CreatePendingRequest(traveler.Id, service.Id, Now.AddDays(1), null,
            1, 123456.78m, Now, serviceOption: "Double room", specialRequest: "Late arrival");
        // EF InMemory has no SQL Server rowversion generator.
        typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.RowVersion))!.SetValue(request, new byte[8]);
        db.ServiceBookings.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    });

    private sealed class FixedClock : IDateTimeProvider { public DateTimeOffset UtcNow => Now; }
    private sealed class InMemoryLock : IServiceProviderRequestLock
    {
        public Task AcquireOperationAsync(long actorUserId, Guid key, CancellationToken token) => Task.CompletedTask;
        public Task AcquireBookingAsync(long bookingId, CancellationToken token) => Task.CompletedTask;
    }
}