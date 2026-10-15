using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Traveler.Profile.Common;
using TripMate.Application.Features.Traveler.Profile.Update;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Traveler;

[Collection(nameof(TripMateApiFactory))]
public sealed class TravelerProfileEndpointTests
{
    private const string Route = "/api/v1/traveler/profile";

    [Fact]
    public async Task GetAndPut_ActiveTraveler_ReadAndPersistOnlyEditableFields()
    {
        await using var factory = new TripMateApiFactory();
        long userId = await SeedAsync(factory, "traveler@example.com");
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        var get = await client.GetAsync(Route);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadFromJsonAsync<TravelerProfileDto>())!.Email
            .Should().Be("traveler@example.com");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Nguyễn Văn A"), "fullName");
        form.Add(new StringContent("0912345678"), "phoneNumber");
        form.Add(new StringContent("2000-01-01"), "dateOfBirth");
        form.Add(new StringContent("changed@example.com"), "email");
        var put = await client.PutAsync(Route, form);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await put.Content.ReadFromJsonAsync<UpdateTravelerProfileResponse>();
        response!.MessageCode.Should().Be(TravelerProfileErrorCodes.Success);
        response.Profile.DateOfBirth.Should().Be(new DateOnly(2000, 1, 1));
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Id == userId);
            user.FullName.Should().Be("Nguyễn Văn A");
            user.Email.Should().Be("traveler@example.com");
            user.PhoneNumber.Should().Be("0912345678");
            user.Role.Should().Be(UserRole.Traveler);
            (await db.TravelerProfiles.SingleAsync(profile => profile.UserId == userId))
                .DateOfBirth.Should().Be(new DateOnly(2000, 1, 1));
            return true;
        });
    }

    [Fact]
    public async Task GetAndPut_RequireAnActiveTraveler()
    {
        await using var factory = new TripMateApiFactory();
        long lockedId = await SeedAsync(factory, "locked@example.com", AccountStatus.Locked);
        using var guest = factory.CreateClient();
        using var operatorClient = factory.CreateAuthenticatedClient(lockedId, UserRole.TourOperator);
        using var lockedClient = factory.CreateAuthenticatedClient(lockedId, UserRole.Traveler);

        (await guest.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await operatorClient.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await lockedClient.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var guestForm = new MultipartFormDataContent();
        guestForm.Add(new StringContent("Changed"), "fullName");
        (await guest.PutAsync(Route, guestForm)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var operatorForm = new MultipartFormDataContent();
        operatorForm.Add(new StringContent("Changed"), "fullName");
        (await operatorClient.PutAsync(Route, operatorForm)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Changed"), "fullName");
        (await lockedClient.PutAsync(Route, form)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Put_DuplicatePhone_ReturnsCatalogConflictWithoutChangingProfile()
    {
        await using var factory = new TripMateApiFactory();
        long userId = await SeedAsync(factory, "traveler@example.com");
        await SeedAsync(factory, "other@example.com", phone: "0912345678");
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Changed"), "fullName");
        form.Add(new StringContent("0912345678"), "phoneNumber");

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("MSG_PHONE_DUP");
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Id == userId);
            user.FullName.Should().Be("Original Traveler");
            user.PhoneNumber.Should().BeNull();
            return true;
        });
    }

    private static Task<long> SeedAsync(TripMateApiFactory factory, string email,
        AccountStatus status = AccountStatus.Active, string? phone = null) =>
        factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                FullName = "Original Traveler",
                Email = email,
                PhoneNumber = phone,
                Role = UserRole.Traveler,
                Status = status,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        });
}