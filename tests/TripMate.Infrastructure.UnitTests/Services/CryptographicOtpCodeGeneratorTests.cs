using System.Globalization;
using System.Text.RegularExpressions;

using FluentAssertions;

using TripMate.Infrastructure.Services;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Services;

public class CryptographicOtpCodeGeneratorTests
{
    private static readonly Regex SixDigits = new("^[0-9]{6}$", RegexOptions.Compiled);

    private readonly CryptographicOtpCodeGenerator _generator = new();

    [Fact(DisplayName = "PLAN-OTP-01: generated code has length 6")]
    public void GeneratedCode_HasLength6()
    {
        for (var i = 0; i < 1000; i++)
        {
            _generator.Generate().Length.Should().Be(6);
        }
    }

    [Fact(DisplayName = "PLAN-OTP-02: generated code is digits only")]
    public void GeneratedCode_IsDigitsOnly()
    {
        for (var i = 0; i < 1000; i++)
        {
            SixDigits.IsMatch(_generator.Generate()).Should().BeTrue();
        }
    }

    [Fact(DisplayName = "PLAN-OTP-03: leading-zero representation is valid")]
    public void GeneratedCode_PreservesLeadingZeros()
    {
        var codes = Enumerable.Range(0, 5000).Select(_ => _generator.Generate()).ToList();

        codes.Should().Contain(code => code.StartsWith('0'));
        codes.Should().OnlyContain(code => code.Length == 6);
    }

    [Fact(DisplayName = "PLAN-OTP-04: repeated generation outputs always remain in approved format")]
    public void RepeatedGeneration_StaysInApprovedFormat()
    {
        var codes = Enumerable.Range(0, 5000).Select(_ => _generator.Generate()).ToList();

        codes.Should().OnlyContain(code => SixDigits.IsMatch(code));
    }

    [Fact(DisplayName = "PLAN-OTP-05: generated codes parse as integers within the approved 0..999999 range")]
    public void GeneratedCodes_ParseWithinApprovedRange()
    {
        foreach (var code in Enumerable.Range(0, 5000).Select(_ => _generator.Generate()))
        {
            int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                .Should().BeTrue();
            value.Should().BeInRange(0, 999_999);
        }
    }
}