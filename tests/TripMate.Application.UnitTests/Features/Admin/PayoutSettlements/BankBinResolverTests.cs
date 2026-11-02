using System.Text;

using FluentAssertions;

using TripMate.Application.Features.Admin.PayoutSettlements.Common;

namespace TripMate.Application.UnitTests.Features.Admin.PayoutSettlements;

public sealed class BankBinResolverTests
{
    private static readonly BankCatalogueEntry[] Catalogue =
    [
        new("970436", "Vietcombank", "Ngân hàng TMCP Ngoại Thương Việt Nam"),
        new("970415", "VietinBank", "Ngân hàng TMCP Công thương Việt Nam"),
        new("970418", "BIDV", "Ngân hàng TMCP Đầu tư và Phát triển Việt Nam"),
        new("970422", "MBBank", "Ngân hàng TMCP Quân đội"),
    ];

    [Fact]
    public void StoredBin_PresentInCatalogue_IsReturnedWithoutLookingAtTheName()
    {
        var result = BankBinResolver.Resolve("970415", "Some other name", Catalogue);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("970415");
    }

    [Theory]
    [InlineData("999999")]
    [InlineData("97043")]
    [InlineData("97043A")]
    public void StoredBin_NotInCatalogue_FailsAndDoesNotFallBackToTheName(string storedBin)
    {
        var result = BankBinResolver.Resolve(storedBin, "Vietcombank", Catalogue);

        AssertUnresolved(result);
    }

    [Theory]
    [InlineData("Vietcombank")]
    [InlineData("VietinBank")]
    [InlineData("BIDV")]
    [InlineData("Ngân hàng TMCP Ngoại Thương Việt Nam")]
    [InlineData("Ngân hàng TMCP Đầu tư và Phát triển Việt Nam")]
    public void BankName_ExactShortOrOfficialName_ResolvesItsBin(string bankName)
    {
        var result = BankBinResolver.Resolve(null, bankName, Catalogue);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(bankName switch
        {
            "Vietcombank" or "Ngân hàng TMCP Ngoại Thương Việt Nam" => "970436",
            "VietinBank" => "970415",
            _ => "970418",
        });
    }

    [Theory]
    [InlineData("VIETCOMBANK", "970436")]
    [InlineData("vietcombank", "970436")]
    [InlineData("bidv", "970418")]
    [InlineData("NGÂN HÀNG TMCP NGOẠI THƯƠNG VIỆT NAM", "970436")]
    [InlineData("ngân hàng tmcp đầu tư và phát triển việt nam", "970418")]
    public void BankName_DifferingOnlyInCase_Resolves(string bankName, string expectedBin)
    {
        BankBinResolver.Resolve(null, bankName, Catalogue).Value.Should().Be(expectedBin);
    }

    [Theory]
    [InlineData("  Vietcombank")]
    [InlineData("Vietcombank  ")]
    [InlineData("\tVietcombank\r\n")]
    public void BankName_WithSurroundingWhitespace_IsTrimmedAndResolves(string bankName)
    {
        BankBinResolver.Resolve(null, bankName, Catalogue).Value.Should().Be("970436");
    }

    [Fact]
    public void BankName_InDecomposedUnicodeForm_ResolvesToTheSameBankAsTheComposedForm()
    {
        var decomposed = "Ngân hàng TMCP Ngoại Thương Việt Nam".Normalize(NormalizationForm.FormD);
        decomposed.Should().NotBe("Ngân hàng TMCP Ngoại Thương Việt Nam", "the fixture must really be decomposed");

        var result = BankBinResolver.Resolve(null, decomposed, Catalogue);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("970436");
    }

    [Theory]
    [InlineData("Ngan hang TMCP Ngoai Thuong Viet Nam")]
    [InlineData("Ngân hàng TMCP Ngoại Thuong Việt Nam")]
    [InlineData("Ngân hàng  TMCP Ngoại Thương Việt Nam")]
    [InlineData("Ngân hàng TMCP Ngoại Thương Việt Nam.")]
    public void BankName_WithAccentsStrippedOrInnerWhitespaceOrPunctuationChanged_IsNotAMatch(string bankName)
    {
        AssertUnresolved(BankBinResolver.Resolve(null, bankName, Catalogue));
    }

    [Theory]
    [InlineData("Vietcom")]
    [InlineData("Vietcombank Chi nhanh Ha Noi")]
    [InlineData("Vietcombnak")]
    [InlineData("VCB")]
    [InlineData("Viet")]
    [InlineData("Ngân hàng TMCP")]
    [InlineData("Ngân hàng TMCP Quân")]
    public void BankName_PartialTypoOrAbbreviated_IsNotAMatch(string bankName)
    {
        AssertUnresolved(BankBinResolver.Resolve(null, bankName, Catalogue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOrMissingName_WithoutStoredBin_IsNotAMatch(string? bankName)
    {
        AssertUnresolved(BankBinResolver.Resolve(null, bankName, Catalogue));
        AssertUnresolved(BankBinResolver.Resolve("", bankName, Catalogue));
    }

    [Fact]
    public void NameMatchingTwoDifferentBins_IsAmbiguousAndFails()
    {
        BankCatalogueEntry[] ambiguous =
        [
            new("970436", "Alpha", "Ngân hàng Alpha"),
            new("970437", "ALPHA", "Ngân hàng Beta"),
        ];

        AssertUnresolved(BankBinResolver.Resolve(null, "alpha", ambiguous));
    }

    [Fact]
    public void NameMatchingBothShortAndOfficialNameOfTheSameBank_IsNotAmbiguous()
    {
        BankCatalogueEntry[] same = [new("970436", "Alpha", "alpha")];

        BankBinResolver.Resolve(null, "ALPHA", same).Value.Should().Be("970436");
    }

    [Fact]
    public void EmptyCatalogue_NeverResolves()
    {
        AssertUnresolved(BankBinResolver.Resolve(null, "Vietcombank", []));
        AssertUnresolved(BankBinResolver.Resolve("970436", null, []));
    }

    private static void AssertUnresolved(TripMate.Application.Common.Models.Result<string> result)
    {
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutSettlementErrorCodes.BankBinUnresolved);
        result.ErrorMessage.Should().Be(PayoutSettlementErrorCodes.BankBinUnresolvedMessage);
    }
}