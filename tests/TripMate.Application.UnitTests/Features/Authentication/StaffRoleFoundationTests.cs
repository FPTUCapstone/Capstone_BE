using System.Text.Json;
using System.Text.Json.Serialization;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.Login;
using TripMate.Application.Features.Authentication.WebRefresh;
using TripMate.Application.Features.Authentication.WebSignIn;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication;

public class StaffRoleFoundationTests
{
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeJwtTokenService _jwtTokenService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();

    [Fact]
    public void UserRole_PreservesCanonicalNumericValuesWithoutRegression()
    {
        ((int)UserRole.Traveler).Should().Be(1);
        ((int)UserRole.TourOperator).Should().Be(2);
        ((int)UserRole.Administrator).Should().Be(3);
        ((int)UserRole.Staff).Should().Be(4);
    }

    [Theory]
    [InlineData(UserRole.Traveler, "Traveler")]
    [InlineData(UserRole.TourOperator, "TourOperator")]
    [InlineData(UserRole.Administrator, "Administrator")]
    [InlineData(UserRole.Staff, "Staff")]
    public void UserRole_SerializesAndDeserializesCorrectly(UserRole expectedRole, string expectedJsonString)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(expectedRole, options);
        json.Should().Be($"\"{expectedJsonString}\"");

        var deserialized = JsonSerializer.Deserialize<UserRole>(json, options);
        deserialized.Should().Be(expectedRole);
    }

    [Fact]
    public void UserRoleExtensions_ClassifiesAdministrationRolesAccurately()
    {
        UserRole.Administrator.IsAdministrationRole().Should().BeTrue();
        UserRole.Staff.IsAdministrationRole().Should().BeTrue();
        UserRole.Traveler.IsAdministrationRole().Should().BeFalse();
        UserRole.TourOperator.IsAdministrationRole().Should().BeFalse();

        UserRole.Administrator.IsAdministrator().Should().BeTrue();
        UserRole.Staff.IsAdministrator().Should().BeFalse();

        UserRole.Staff.IsStaff().Should().BeTrue();
        UserRole.Administrator.IsStaff().Should().BeFalse();

        UserRoleExtensions.AdministratorOnly.Should().Be("Administrator");
        UserRoleExtensions.StaffOnly.Should().Be("Staff");
        UserRoleExtensions.StaffOrAdministrator.Should().Be("Staff,Administrator");
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Staff)]
    public async Task AdministrationLogin_AcceptsBothStaffAndAdministrator(UserRole role)
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, $"{role.ToString().ToLowerInvariant()}@tripmate.vn", "AdminPass123!", AccountStatus.Active, role);
        var handler = CreateLoginHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand(user.Email!, "AdminPass123!") { AdministratorOnly = true },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(role);
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.ApplicationStatus.Should().BeNull();
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task AdministrationLogin_RejectsTravelerAndTourOperator(UserRole role)
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, $"{role.ToString().ToLowerInvariant()}@tripmate.vn", "UserPass123!", AccountStatus.Active, role);
        var handler = CreateLoginHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand(user.Email!, "UserPass123!") { AdministratorOnly = true },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AdminAccessRequired);
        dbContext.RefreshTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task MobileLogin_RejectsStaffAccountsAsWebOnly()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "staff@tripmate.vn", "StaffPass123!", AccountStatus.Active, UserRole.Staff);
        var handler = CreateLoginHandler(dbContext);

        var result = await handler.Handle(
            LoginCommand.ForMobile("staff@tripmate.vn", "StaffPass123!"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.AdminMobileSignInDisabled);
        dbContext.RefreshTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task WebRefresh_RestoresStaffSessionWithStaffRole()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "staff@tripmate.vn", "StaffPass123!", AccountStatus.Active, UserRole.Staff);
        var rawRefreshToken = "valid-staff-refresh-token";

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = _jwtTokenService.HashRefreshToken(rawRefreshToken),
            ExpiresAtUtc = _dateTimeProvider.UtcNow.AddDays(7),
            CreatedAtUtc = _dateTimeProvider.UtcNow,
        });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        var refreshHandler = new WebRefreshCommandHandler(dbContext, _jwtTokenService, _dateTimeProvider);
        var result = await refreshHandler.Handle(new WebRefreshCommand(rawRefreshToken), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(UserRole.Staff);
        result.Value.UserId.Should().Be(user.Id);
        result.Value.Status.Should().Be(AccountStatus.Active);

        var webDto = WebAuthResponseDto.From(result.Value);
        webDto.Role.Should().Be(UserRole.Staff);
    }

    private LoginCommandHandler CreateLoginHandler(TestDbContext dbContext) =>
        new(dbContext, _passwordHasher, _jwtTokenService, _dateTimeProvider,
            NullLogger<LoginCommandHandler>.Instance);

    private async Task<User> SeedUser(
        TestDbContext dbContext,
        string email,
        string password,
        AccountStatus status,
        UserRole role)
    {
        var user = new User
        {
            Email = email,
            FullName = $"Test {role}",
            PasswordHash = _passwordHasher.Hash(password),
            Role = role,
            Status = status,
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            UpdatedAtUtc = _dateTimeProvider.UtcNow,
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(CancellationToken.None);
        return user;
    }
}
