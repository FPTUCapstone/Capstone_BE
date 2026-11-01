using FluentAssertions;

using TripMate.Application.Features.Admin.PayoutSettlements.Processing;

namespace TripMate.Application.UnitTests.Features.Admin.PayoutSettlements;

public sealed class PayoutDescriptionTests
{
    [Fact]
    public void Build_UsesThePayoutCodeAndFitsTheTransferContentLimit()
    {
        var description = PayoutDescription.Build("PO-000123");

        description.Should().Be("TripMate Payout PO-000123");
        description.Length.Should().BeLessThanOrEqualTo(PayoutDescription.MaxLength);
    }

    [Fact]
    public void Build_TruncatesALongCodeToTheLimit()
    {
        var description = PayoutDescription.Build("PO-202610-000001-EXTRA-LONG");

        description.Should().HaveLength(PayoutDescription.MaxLength);
        description.Should().StartWith("TripMate Payout PO-2026");
    }

    [Theory]
    [InlineData("Thanh toán Đợt 1", "Thanh toan Dot 1")]
    [InlineData("Nguyễn Văn A", "Nguyen Van A")]
    [InlineData("ABC \u0001 DEF", "ABC  DEF")]
    [InlineData("  padded  ", "padded")]
    public void Sanitize_ReducesToPlainAsciiWithoutDiacriticsOrControlCharacters(string input, string expected)
    {
        PayoutDescription.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_NeverExceedsTheLimit_AndDropsNonLatinCharacters()
    {
        var result = PayoutDescription.Sanitize(new string('a', 100) + "漢字");

        result.Should().HaveLength(PayoutDescription.MaxLength);
        result.Should().MatchRegex("^[ -~]+$");
    }

    [Fact]
    public void Sanitize_OfNothingUsableFallsBackToTheBrandName()
    {
        PayoutDescription.Sanitize("漢字").Should().Be("TripMate");
        PayoutDescription.Sanitize("   ").Should().Be("TripMate");
    }
}
