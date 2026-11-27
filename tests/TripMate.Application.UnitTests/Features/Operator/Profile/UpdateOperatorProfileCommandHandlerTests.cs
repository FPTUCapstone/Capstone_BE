using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Features.Operator.Profile.Common;
using TripMate.Application.Features.Operator.Profile.Update;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Profile;

public sealed class UpdateOperatorProfileCommandHandlerTests
{
    private const long OperatorId = 820;
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 4, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext db = TestDbContext.Create();
    private readonly FakeCurrentUserService currentUser = new()
    {
        UserId = OperatorId,
        Role = nameof(UserRole.TourOperator),
    };
    private readonly FakeDateTimeProvider clock = new() { UtcNow = Now };

    private readonly FakeOperatorLogoStorage storage = new();

    private UpdateOperatorProfileCommandHandler Handler() =>
        new(db, currentUser, clock, storage, NullLogger<UpdateOperatorProfileCommandHandler>.Instance);

    [Fact]
    public async Task ApprovedOperator_UpdatesEditableFieldsAndReturnsTheProfile()
    {
        await SeedAsync();

        var result = await Handler().Handle(Command() with
        {
            BusinessName = "  Moon Tours  ",
            BusinessDescription = " Night tours ",
            BusinessAddress = " 2 River St ",
            ContactPhone = " 0911111111 ",
            ContactEmail = " Hello@Moon.vn ",
            Website = " https://moon.vn ",
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be(OperatorProfileErrorCodes.Updated);
        result.Value.Profile.Should().BeEquivalentTo(new OperatorProfileDto(
            OperatorId, "Moon Tours", "Night tours", "2 River St", "0911111111", "Hello@Moon.vn",
            "https://moon.vn", null, "79-1234/2026/TCDL-GPLHQT", "0312345678", "Approved", "owner@suntours.vn"));

        var stored = db.OperatorProfiles.Single(profile => profile.UserId == OperatorId);
        stored.CompanyName.Should().Be("Moon Tours");
        stored.Description.Should().Be("Night tours");
        stored.ContactAddress.Should().Be("2 River St");
        stored.ContactPhone.Should().Be("0911111111");
        stored.ContactEmail.Should().Be("Hello@Moon.vn", "the contact email is trimmed but not lower-cased");
        stored.WebsiteUrl.Should().Be("https://moon.vn");
        stored.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task LegalApprovalCommissionBankAndAccountFields_AreNeverChanged()
    {
        await SeedAsync(profile =>
        {
            profile.UpdateBeneficiaryBankAccount("Vietcombank", "Holder Name", "****9876", "cipher-secret-xyz",
                DateTimeOffset.UnixEpoch);
        });
        var before = db.OperatorProfiles.AsNoTracking().Single(profile => profile.UserId == OperatorId);
        var reviewedAt = before.ReviewedAtUtc;

        var result = await Handler().Handle(Command() with { BusinessName = "Renamed" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var after = db.OperatorProfiles.Single(profile => profile.UserId == OperatorId);
        after.TaxCode.Should().Be("0312345678");
        after.BusinessLicenseNo.Should().Be("79-1234/2026/TCDL-GPLHQT");
        after.ApprovalStatus.Should().Be(OperatorApprovalStatus.Approved);
        after.CommissionRate.Should().Be(12.5m);
        after.RejectionReason.Should().BeNull();
        after.ReviewedBy.Should().Be(99);
        after.ReviewedAtUtc.Should().Be(reviewedAt);
        after.BankName.Should().Be("Vietcombank");
        after.AccountHolderName.Should().Be("Holder Name");
        after.AccountNumberMasked.Should().Be("****9876");
        after.AccountNumberProtected.Should().Be("cipher-secret-xyz");
        var user = db.Users.Single(candidate => candidate.Id == OperatorId);
        user.Email.Should().Be("owner@suntours.vn");
        user.FullName.Should().Be("Owner");
        user.Status.Should().Be(AccountStatus.Active);
    }

    [Fact]
    public async Task BlankOptionalFields_ClearPreviouslyStoredValues()
    {
        await SeedAsync(profile => profile.UpdateBusinessProfile(
            "Sun Tours", "Old text", "Old address", "0900000000", "old@x.vn", "https://old.vn", DateTimeOffset.UnixEpoch));

        var result = await Handler().Handle(Command() with
        {
            BusinessDescription = "  ",
            BusinessAddress = "",
            ContactPhone = null,
            ContactEmail = " ",
            Website = null,
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var stored = db.OperatorProfiles.Single(profile => profile.UserId == OperatorId);
        stored.Description.Should().BeNull();
        stored.ContactAddress.Should().BeNull();
        stored.ContactPhone.Should().BeNull();
        stored.ContactEmail.Should().BeNull();
        stored.WebsiteUrl.Should().BeNull();
        result.Value.Profile.BusinessDescription.Should().BeEmpty();
        result.Value.Profile.Website.Should().BeNull();
    }

    [Fact]
    public async Task OmittedLogo_LeavesTheCurrentLogoUntouched()
    {
        await SeedAsync(profile => profile.SetLogo("https://cdn.example.test/logo.png",
            "tripmate/operator-logos/820/a", DateTimeOffset.UnixEpoch));

        var result = await Handler().Handle(Command() with { BusinessName = "Renamed" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Profile.LogoUrl.Should().Be("https://cdn.example.test/logo.png");
        db.OperatorProfiles.Single(profile => profile.UserId == OperatorId).LogoPublicId
            .Should().Be("tripmate/operator-logos/820/a");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(OperatorId, nameof(UserRole.Traveler))]
    [InlineData(OperatorId, nameof(UserRole.Administrator))]
    [InlineData(OperatorId, nameof(UserRole.ServiceProvider))]
    public async Task NonOperatorCallers_AreRefusedAndNothingChanges(long? userId, string? role)
    {
        await SeedAsync();
        currentUser.UserId = userId;
        currentUser.Role = role;

        var result = await Handler().Handle(Command() with { BusinessName = "Hacked" }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
        db.OperatorProfiles.Single(profile => profile.UserId == OperatorId).CompanyName.Should().Be("Sun Tours");
    }

    [Theory]
    [InlineData(OperatorApprovalStatus.PendingApproval, AccountStatus.PendingApproval)]
    [InlineData(OperatorApprovalStatus.Rejected, AccountStatus.Rejected)]
    [InlineData(OperatorApprovalStatus.Approved, AccountStatus.Locked)]
    [InlineData(OperatorApprovalStatus.Approved, AccountStatus.Inactive)]
    [InlineData(OperatorApprovalStatus.Approved, AccountStatus.PendingContractSignature)]
    public async Task OperatorsThatAreNotApprovedAndActive_AreRefusedAndNothingChanges(
        OperatorApprovalStatus approval, AccountStatus account)
    {
        await SeedAsync(approval: approval, account: account);

        var result = await Handler().Handle(Command() with { BusinessName = "Hacked" }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
        db.OperatorProfiles.Single(profile => profile.UserId == OperatorId).CompanyName.Should().Be("Sun Tours");
    }

    [Fact]
    public async Task UpdatingOneOperator_NeverChangesAnother()
    {
        await SeedAsync();
        db.Users.Add(new User
        {
            Id = 821,
            Email = "other@x.vn",
            FullName = "Other",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        });
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = 821,
            CompanyName = "Other Co",
            TaxCode = "0999999999",
            BusinessLicenseNo = "11-1/2026/TCDL-GPLHQT",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await db.SaveChangesAsync();

        var result = await Handler().Handle(Command() with { BusinessName = "Renamed" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.OperatorProfiles.Single(profile => profile.UserId == 821).CompanyName.Should().Be("Other Co");
    }

    private static UpdateOperatorProfileCommand Command() =>
        new("Sun Tours", null, null, null, null, null, null, null, false);

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
            ReviewedBy = 99,
            ReviewedAtUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        };
        configure?.Invoke(profile);
        db.Users.Add(user);
        db.OperatorProfiles.Add(profile);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}