using FluentAssertions;

using TripMate.Application.Features.Operator.Profile.Common;
using TripMate.Application.Features.Operator.Profile.Get;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Profile;

public sealed class GetOperatorProfileQueryHandlerTests
{
    private const long OperatorId = 810;
    private readonly TestDbContext db = TestDbContext.Create();
    private readonly FakeCurrentUserService currentUser = new()
    {
        UserId = OperatorId,
        Role = nameof(UserRole.TourOperator),
    };

    private GetOperatorProfileQueryHandler Handler() => new(db, currentUser);

    [Fact]
    public async Task ApprovedOperator_ReceivesEditableAndReadOnlyFields()
    {
        await SeedAsync(profile =>
        {
            profile.UpdateBusinessProfile("Sun Tours", "Family tours", "1 Beach Rd", "0901234567",
                "hello@suntours.vn", "https://suntours.vn", DateTimeOffset.UnixEpoch);
            profile.SetLogo("https://cdn.example.test/logo.png", "tripmate/operator-logos/810/a", DateTimeOffset.UnixEpoch);
        });

        var result = await Handler().Handle(new GetOperatorProfileQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new OperatorProfileDto(
            OperatorId, "Sun Tours", "Family tours", "1 Beach Rd", "0901234567", "hello@suntours.vn",
            "https://suntours.vn", "https://cdn.example.test/logo.png",
            "79-1234/2026/TCDL-GPLHQT", "0312345678", "Approved", "owner@suntours.vn"));
    }

    [Fact]
    public async Task UnsetOptionalFields_AreEmptyStringsAndOmittedUrls()
    {
        await SeedAsync();

        var result = await Handler().Handle(new GetOperatorProfileQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;
        dto.BusinessName.Should().Be("Sun Tours");
        dto.BusinessDescription.Should().BeEmpty();
        dto.BusinessAddress.Should().BeEmpty();
        dto.ContactPhone.Should().BeEmpty();
        dto.ContactEmail.Should().BeEmpty();
        dto.Website.Should().BeNull();
        dto.LogoUrl.Should().BeNull();
    }

    [Fact]
    public async Task CommissionAndBankAccount_AreNeverExposed()
    {
        await SeedAsync(profile =>
            profile.UpdateBeneficiaryBankAccount("Vietcombank", "Holder Name", "****9876", "cipher-secret-xyz", DateTimeOffset.UnixEpoch));

        var result = await Handler().Handle(new GetOperatorProfileQuery(), CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        json.Should().NotContainEquivalentOf("commission").And.NotContainEquivalentOf("bank").And.NotContain("Vietcombank")
            .And.NotContain("cipher-secret-xyz").And.NotContain("9876").And.NotContain("12.5");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(OperatorId, nameof(UserRole.Traveler))]
    [InlineData(OperatorId, nameof(UserRole.Administrator))]
    [InlineData(OperatorId, nameof(UserRole.ServiceProvider))]
    public async Task NonOperatorCallers_AreRefused(long? userId, string? role)
    {
        await SeedAsync();
        currentUser.UserId = userId;
        currentUser.Role = role;

        var result = await Handler().Handle(new GetOperatorProfileQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
    }

    [Theory]
    [InlineData(OperatorApprovalStatus.PendingApproval, AccountStatus.PendingApproval)]
    [InlineData(OperatorApprovalStatus.Rejected, AccountStatus.Rejected)]
    [InlineData(OperatorApprovalStatus.Approved, AccountStatus.Locked)]
    [InlineData(OperatorApprovalStatus.Approved, AccountStatus.Inactive)]
    [InlineData(OperatorApprovalStatus.Approved, AccountStatus.PendingContractSignature)]
    public async Task OperatorsThatAreNotApprovedAndActive_AreRefused(
        OperatorApprovalStatus approval, AccountStatus account)
    {
        await SeedAsync(approval: approval, account: account);

        var result = await Handler().Handle(new GetOperatorProfileQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
    }

    [Fact]
    public async Task AnotherOperatorsProfile_IsNeverReturned()
    {
        await SeedAsync();
        db.Users.Add(new User
        {
            Id = 811,
            Email = "other@x.vn",
            FullName = "Other",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        });
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = 811,
            CompanyName = "Other Co",
            TaxCode = "0999999999",
            BusinessLicenseNo = "11-1/2026/TCDL-GPLHQT",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await db.SaveChangesAsync();

        var result = await Handler().Handle(new GetOperatorProfileQuery(), CancellationToken.None);

        result.Value.UserId.Should().Be(OperatorId);
        result.Value.BusinessName.Should().Be("Sun Tours");
    }

    [Fact]
    public async Task OperatorUserWithoutProfileRow_IsRefused()
    {
        db.Users.Add(new User
        {
            Id = OperatorId,
            Email = "noprofile@x.vn",
            FullName = "No Profile",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        });
        await db.SaveChangesAsync();

        var result = await Handler().Handle(new GetOperatorProfileQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
    }

    [Fact]
    public async Task AccountWithoutAnEmail_ReturnsAnEmptyAccountEmail()
    {
        await SeedAsync();
        db.Users.Single(user => user.Id == OperatorId).Email = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Handler().Handle(new GetOperatorProfileQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccountEmail.Should().BeEmpty();
    }

    private async Task SeedAsync(
        Action<OperatorProfile>? configure = null,
        OperatorApprovalStatus approval = OperatorApprovalStatus.Approved,
        AccountStatus account = AccountStatus.Active)
    {
        var user = new User
        {
            Id = OperatorId,
            Email = "owner@suntours.vn",
            FullName = "Owner",
            Role = UserRole.TourOperator,
            Status = account,
        };
        var profile = new OperatorProfile
        {
            UserId = OperatorId,
            User = user,
            CompanyName = "Sun Tours",
            TaxCode = "0312345678",
            BusinessLicenseNo = "79-1234/2026/TCDL-GPLHQT",
            CommissionRate = 12.5m,
            ApprovalStatus = approval,
        };
        configure?.Invoke(profile);
        db.Users.Add(user);
        db.OperatorProfiles.Add(profile);
        await db.SaveChangesAsync();
    }
}