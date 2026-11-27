using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Profile;

public sealed class OperatorProfileBusinessProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UpdateBusinessProfile_TrimsAndStoresEditableFields()
    {
        var profile = Approved();

        profile.UpdateBusinessProfile(
            "  New Name  ", "  About us  ", "  1 Beach Rd  ", " 0901234567 ",
            " ops@example.com ", " https://example.com ", Now);

        profile.CompanyName.Should().Be("New Name");
        profile.Description.Should().Be("About us");
        profile.ContactAddress.Should().Be("1 Beach Rd");
        profile.ContactPhone.Should().Be("0901234567");
        profile.ContactEmail.Should().Be("ops@example.com");
        profile.WebsiteUrl.Should().Be("https://example.com");
        profile.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void UpdateBusinessProfile_StoresBlankOptionalFieldsAsNull()
    {
        var profile = Approved();
        profile.UpdateBusinessProfile("Name", "x", "x", "0901234567", "a@b.co", "https://x.io", Now);

        profile.UpdateBusinessProfile("Name", "  ", null, "", " ", null, Now);

        profile.Description.Should().BeNull();
        profile.ContactAddress.Should().BeNull();
        profile.ContactPhone.Should().BeNull();
        profile.ContactEmail.Should().BeNull();
        profile.WebsiteUrl.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateBusinessProfile_RequiresCompanyName(string? name)
    {
        var profile = Approved();

        var act = () => profile.UpdateBusinessProfile(name!, null, null, null, null, null, Now);

        act.Should().Throw<ArgumentException>();
        profile.CompanyName.Should().Be("Original Co");
    }

    [Fact]
    public void UpdateBusinessProfile_RejectsValuesLongerThanTheColumns()
    {
        var profile = Approved();

        var cases = new (string Field, Action Act)[]
        {
            ("name", () => profile.UpdateBusinessProfile(new string('a', OperatorProfile.CompanyNameMaxLength + 1), null, null, null, null, null, Now)),
            ("description", () => profile.UpdateBusinessProfile("Name", new string('a', OperatorProfile.DescriptionMaxLength + 1), null, null, null, null, Now)),
            ("address", () => profile.UpdateBusinessProfile("Name", null, new string('a', OperatorProfile.ContactAddressMaxLength + 1), null, null, null, Now)),
            ("phone", () => profile.UpdateBusinessProfile("Name", null, null, new string('0', OperatorProfile.ContactPhoneMaxLength + 1), null, null, Now)),
            ("email", () => profile.UpdateBusinessProfile("Name", null, null, null, new string('a', OperatorProfile.ContactEmailMaxLength + 1), null, Now)),
            ("website", () => profile.UpdateBusinessProfile("Name", null, null, null, null, new string('a', OperatorProfile.WebsiteUrlMaxLength + 1), Now)),
        };

        foreach (var (field, act) in cases)
        {
            act.Should().Throw<ArgumentException>(field);
        }

        profile.CompanyName.Should().Be("Original Co");
    }

    [Fact]
    public void UpdateBusinessProfile_NeverTouchesLegalApprovalCommissionOrBankFields()
    {
        var profile = Approved();
        profile.UpdateBeneficiaryBankAccount("Bank", "Holder", "****1234", "protected", Now.AddDays(-1));
        var reviewedAt = profile.ReviewedAtUtc;

        profile.UpdateBusinessProfile("Other", "d", "a", "0900000000", "e@x.io", "https://x.io", Now);

        profile.TaxCode.Should().Be("0312345678");
        profile.BusinessLicenseNo.Should().Be("79-1234/2026/TCDL-GPLHQT");
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Approved);
        profile.CommissionRate.Should().Be(12.5m);
        profile.ReviewedBy.Should().Be(99);
        profile.ReviewedAtUtc.Should().Be(reviewedAt);
        profile.BankName.Should().Be("Bank");
        profile.AccountNumberProtected.Should().Be("protected");
    }

    [Fact]
    public void SetLogo_StoresUrlAndPublicId()
    {
        var profile = Approved();

        profile.SetLogo("https://cdn.example.test/logo.png", "tripmate/operator-logos/7/abc", Now);

        profile.LogoUrl.Should().Be("https://cdn.example.test/logo.png");
        profile.LogoPublicId.Should().Be("tripmate/operator-logos/7/abc");
        profile.UpdatedAtUtc.Should().Be(Now);
    }

    [Theory]
    [InlineData("", "id")]
    [InlineData("https://cdn.example.test/logo.png", " ")]
    public void SetLogo_RequiresBothValues(string url, string publicId)
    {
        var profile = Approved();

        var act = () => profile.SetLogo(url, publicId, Now);

        act.Should().Throw<ArgumentException>();
        profile.LogoUrl.Should().BeNull();
    }

    [Fact]
    public void SetLogo_RejectsValuesLongerThanTheColumns()
    {
        var profile = Approved();

        ((Action)(() => profile.SetLogo(new string('a', OperatorProfile.LogoUrlMaxLength + 1), "id", Now)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => profile.SetLogo("https://x.io/l.png", new string('a', OperatorProfile.LogoPublicIdMaxLength + 1), Now)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ClearLogo_ClearsBothColumns()
    {
        var profile = Approved();
        profile.SetLogo("https://cdn.example.test/logo.png", "tripmate/operator-logos/7/abc", Now.AddHours(-1));

        profile.ClearLogo(Now);

        profile.LogoUrl.Should().BeNull();
        profile.LogoPublicId.Should().BeNull();
        profile.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void ClearLogo_IsANoOpWhenThereIsNoLogo()
    {
        var profile = Approved();
        var before = profile.UpdatedAtUtc;

        profile.ClearLogo(Now);
        profile.ClearLogo(Now.AddMinutes(1));

        profile.LogoUrl.Should().BeNull();
        profile.LogoPublicId.Should().BeNull();
        profile.UpdatedAtUtc.Should().Be(before);
    }

    private static OperatorProfile Approved() => new()
    {
        UserId = 7,
        CompanyName = "Original Co",
        TaxCode = "0312345678",
        BusinessLicenseNo = "79-1234/2026/TCDL-GPLHQT",
        CommissionRate = 12.5m,
        ApprovalStatus = OperatorApprovalStatus.Approved,
        ReviewedBy = 99,
        ReviewedAtUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        UpdatedAtUtc = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero),
    };
}