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

/// <summary>UC-34 logo behavior: spec §4.2 (removeLogo) and §6 (upload, replace, compensation).</summary>
public sealed class UpdateOperatorProfileLogoTests
{
    private const long OperatorId = 830;
    private const string OldLogoUrl = "https://cdn.example.test/tripmate/operator-logos/830/old.png";
    private const string OldPublicId = "tripmate/operator-logos/830/old";
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2];
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 5, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext db = TestDbContext.Create();
    private readonly FakeOperatorLogoStorage storage = new();
    private readonly FakeCurrentUserService currentUser = new()
    {
        UserId = OperatorId,
        Role = nameof(UserRole.TourOperator),
    };
    private readonly FakeDateTimeProvider clock = new() { UtcNow = Now };

    private UpdateOperatorProfileCommandHandler Handler() =>
        new(db, currentUser, clock, storage, NullLogger<UpdateOperatorProfileCommandHandler>.Instance);

    [Fact]
    public async Task NewLogo_WhenNoneExists_IsUploadedAndStored()
    {
        await SeedAsync();

        var result = await Handler().Handle(Command() with { LogoBytes = Png, LogoContentType = "image/png" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        storage.Uploads.Should().ContainSingle();
        storage.Uploads[0].PublicId.Should().Be($"tripmate/operator-logos/{OperatorId}/new1");
        storage.Uploads[0].ContentType.Should().Be("image/png");
        storage.Uploads[0].Bytes.Should().Equal(Png);
        storage.Deletes.Should().BeEmpty();
        var stored = Stored();
        stored.LogoPublicId.Should().Be($"tripmate/operator-logos/{OperatorId}/new1");
        stored.LogoUrl.Should().Be($"https://cdn.example.test/tripmate/operator-logos/{OperatorId}/new1.png");
        result.Value.Profile.LogoUrl.Should().Be(stored.LogoUrl);
    }

    [Fact]
    public async Task ReplacingTheLogo_StoresTheNewOneAndDeletesTheOldAssetAfterCommit()
    {
        await SeedAsync(withLogo: true);

        var result = await Handler().Handle(Command() with { LogoBytes = Png, LogoContentType = "image/png" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Stored().LogoPublicId.Should().Be($"tripmate/operator-logos/{OperatorId}/new1");
        storage.Deletes.Should().Equal(OldPublicId);
    }

    [Fact]
    public async Task UploadFailure_ReturnsUnavailable_PersistsNothingAndDeletesNothing()
    {
        await SeedAsync(withLogo: true);
        storage.FailUpload = true;

        var result = await Handler().Handle(
            Command() with { BusinessName = "Renamed", LogoBytes = Png, LogoContentType = "image/png" },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OperatorProfileErrorCodes.Unavailable);
        storage.Deletes.Should().BeEmpty();
        var stored = Stored();
        stored.CompanyName.Should().Be("Sun Tours", "text fields are not saved when the logo upload fails");
        stored.LogoPublicId.Should().Be(OldPublicId);
    }

    [Fact]
    public async Task DatabaseFailureAfterUpload_DeletesTheNewAssetAndKeepsTheOldLogo()
    {
        await SeedAsync(withLogo: true);
        db.SaveChangesFailureFactory = () => new InvalidOperationException("db down");

        var act = () => Handler().Handle(Command() with { LogoBytes = Png, LogoContentType = "image/png" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        storage.Deletes.Should().Equal($"tripmate/operator-logos/{OperatorId}/new1");
        db.SaveChangesFailureFactory = null;
        db.ChangeTracker.Clear();
        Stored().LogoPublicId.Should().Be(OldPublicId);
    }

    [Fact]
    public async Task RemoveLogo_WithAnExistingLogo_ClearsItAndDeletesTheAsset()
    {
        await SeedAsync(withLogo: true);

        var result = await Handler().Handle(Command() with { RemoveLogo = true }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Stored().LogoUrl.Should().BeNull();
        Stored().LogoPublicId.Should().BeNull();
        result.Value.Profile.LogoUrl.Should().BeNull();
        storage.Deletes.Should().Equal(OldPublicId);
        storage.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveLogo_WithoutALogo_IsAnIdempotentNoOp_NoStorageCalls()
    {
        await SeedAsync();

        var first = await Handler().Handle(Command() with { RemoveLogo = true }, CancellationToken.None);
        var second = await Handler().Handle(Command() with { RemoveLogo = true }, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        first.Value.Profile.LogoUrl.Should().BeNull();
        storage.CallCount.Should().Be(0);
        Stored().LogoUrl.Should().BeNull();
    }

    [Fact]
    public async Task RemoveLogo_WithALegacyUrlButNoPublicId_ClearsBothColumnsWithoutDeleting()
    {
        await SeedAsync();
        // A pre-UC-34 style row: the URL column is set but the public id is not. The setter is private.
        var tracked = db.OperatorProfiles.Single(profile => profile.UserId == OperatorId);
        typeof(OperatorProfile).GetProperty(nameof(OperatorProfile.LogoUrl))!.SetValue(tracked, OldLogoUrl);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Stored().LogoPublicId.Should().BeNull();
        Stored().LogoUrl.Should().Be(OldLogoUrl);

        var result = await Handler().Handle(Command() with { RemoveLogo = true }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Stored().LogoUrl.Should().BeNull();
        Stored().LogoPublicId.Should().BeNull();
        storage.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RemoveLogoTogetherWithANewLogo_TheNewLogoWins()
    {
        await SeedAsync(withLogo: true);

        var result = await Handler().Handle(
            Command() with { RemoveLogo = true, LogoBytes = Png, LogoContentType = "image/png" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Stored().LogoPublicId.Should().Be($"tripmate/operator-logos/{OperatorId}/new1");
        storage.Uploads.Should().ContainSingle();
        storage.Deletes.Should().Equal(OldPublicId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FailingToDeleteTheOldAsset_NeverFailsTheRequest(bool deleteReturnsFalse, bool deleteThrows)
    {
        await SeedAsync(withLogo: true);
        storage.DeleteResult = !deleteReturnsFalse;
        if (deleteThrows) storage.DeleteThrows = new HttpRequestException("provider down");

        var result = await Handler().Handle(Command() with { LogoBytes = Png, LogoContentType = "image/png" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Stored().LogoPublicId.Should().Be($"tripmate/operator-logos/{OperatorId}/new1");
        storage.Deletes.Should().Equal(OldPublicId);
    }

    [Fact]
    public async Task TextOnlyUpdate_NeverTouchesStorageAndKeepsTheLogo()
    {
        await SeedAsync(withLogo: true);

        var result = await Handler().Handle(Command() with { BusinessName = "Renamed" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        storage.CallCount.Should().Be(0);
        Stored().LogoPublicId.Should().Be(OldPublicId);
        Stored().CompanyName.Should().Be("Renamed");
    }

    [Fact]
    public async Task RefusedCaller_NeverUploads()
    {
        await SeedAsync(approval: OperatorApprovalStatus.PendingApproval, account: AccountStatus.PendingApproval);

        var result = await Handler().Handle(Command() with { LogoBytes = Png, LogoContentType = "image/png" },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
        storage.CallCount.Should().Be(0);
    }

    private OperatorProfile Stored() =>
        db.OperatorProfiles.AsNoTracking().Single(profile => profile.UserId == OperatorId);

    private static UpdateOperatorProfileCommand Command() =>
        new("Sun Tours", null, null, null, null, null, null, null, false);

    private async Task SeedAsync(
        bool withLogo = false,
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
            ApprovalStatus = approval,
        };
        if (withLogo) profile.SetLogo(OldLogoUrl, OldPublicId, DateTimeOffset.UnixEpoch);
        db.Users.Add(user);
        db.OperatorProfiles.Add(profile);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}