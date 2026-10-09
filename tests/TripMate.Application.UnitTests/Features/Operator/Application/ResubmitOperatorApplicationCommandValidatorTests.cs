using FluentAssertions;

using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.RegisterOperator;
using TripMate.Application.Features.Operator.Application.Resubmit;

namespace TripMate.Application.UnitTests.Features.Operator.Application;

public sealed class ResubmitOperatorApplicationCommandValidatorTests
{
    private readonly ResubmitOperatorApplicationCommandValidator validator = new();

    [Fact]
    public void ExistingLicense_AllowsOmittingReplacement()
    {
        validator.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RequiredLengths_AreCheckedAfterTrim()
    {
        validator.Validate(Valid() with
        {
            CompanyName = "  " + new string('C', 200) + "  ",
            ContactPerson = "  " + new string('N', 150) + "  ",
        }).IsValid.Should().BeTrue();

        validator.Validate(Valid() with { ContactPerson = new string('N', 151) })
            .Errors.Should().Contain(error =>
                error.PropertyName == nameof(ResubmitOperatorApplicationCommand.ContactPerson));
    }

    [Theory]
    [InlineData("0101234567", "79-0123/2026/TCDL-GPLHQT")]
    [InlineData("0315678901-001", "01-0456/2025/SDL-GPLHND")]
    public void BusinessIdentifiers_ReuseUc02Formats(string taxCode, string licence)
    {
        validator.Validate(Valid() with
        {
            TaxCode = taxCode,
            BusinessLicenseNo = licence,
        }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void InvalidFileSignature_UsesMsg158()
    {
        var result = validator.Validate(Valid() with
        {
            BusinessLicenseDocument = new OperatorRegistrationDocument(
                "licence.pdf", "application/pdf", [(byte)'M', (byte)'Z']),
        });

        result.Errors.Should().ContainSingle(error =>
            error.PropertyName == nameof(ResubmitOperatorApplicationCommand.BusinessLicenseDocument) &&
            error.ErrorCode == AuthErrorCodes.Msg158);
    }

    [Fact]
    public void MoreThanFiveSupportingDocuments_IsRejected()
    {
        var result = validator.Validate(Valid() with
        {
            SupportingDocuments = Enumerable.Range(0, 6)
                .Select(_ => new OperatorRegistrationDocument(
                    "support.pdf", "application/pdf", "%PDF-"u8.ToArray()))
                .ToArray(),
        });

        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(ResubmitOperatorApplicationCommand.SupportingDocuments));
    }

    private static ResubmitOperatorApplicationCommand Valid() => new(
        "TripMate Tours",
        "79-0123/2026/TCDL-GPLHQT",
        "0101234567",
        "Nguyễn An",
        null,
        null,
        null,
        []);
}
