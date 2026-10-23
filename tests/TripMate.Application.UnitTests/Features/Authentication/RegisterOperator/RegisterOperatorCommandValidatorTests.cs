using FluentAssertions;

using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.RegisterOperator;

namespace TripMate.Application.UnitTests.Features.Authentication.RegisterOperator;

public sealed class RegisterOperatorCommandValidatorTests
{
    private readonly RegisterOperatorCommandValidator validator = new();

    [Fact]
    public void ValidRequiredFields_AllowOmittedOptionalFields()
    {
        var result = validator.Validate(ValidCommand());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void LengthBoundaries_AreCheckedAfterTrimmingLikePersistedValues()
    {
        var command = ValidCommand() with
        {
            CompanyName = "  " + new string('C', 200) + "  ",
            ContactPerson = "  " + new string('N', 150) + "  ",
            BusinessAddress = "  " + new string('A', 300) + "  ",
        };

        validator.Validate(command).IsValid.Should().BeTrue();
        validator.Validate(command with { CompanyName = " " + new string('C', 201) + " " })
            .Errors.Should().Contain(error => error.PropertyName == nameof(command.CompanyName));
        validator.Validate(command with { BusinessAddress = " " + new string('A', 301) + " " })
            .Errors.Should().Contain(error => error.PropertyName == nameof(command.BusinessAddress));
    }

    [Theory]
    [InlineData("0101234567")]
    [InlineData("0315678901-001")]
    public void TaxCode_AcceptsHeadOfficeAndBranchFormats(string taxCode)
    {
        validator.Validate(ValidCommand() with { TaxCode = taxCode }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("010123456a")]
    [InlineData("0315678901001")]
    [InlineData("0315678901-01")]
    [InlineData("0315678901 -001")]
    public void TaxCode_RejectsInvalidFormat(string taxCode)
    {
        validator.Validate(ValidCommand() with { TaxCode = taxCode }).Errors.Should().Contain(error =>
            error.PropertyName == nameof(RegisterOperatorCommand.TaxCode) &&
            error.ErrorCode == "OPERATOR_TAX_CODE_INVALID");
    }

    [Theory]
    [InlineData("79-0123/2026/TCDL-GPLHQT")]
    [InlineData("01-0456/2025/SDL-GPLHND")]
    [InlineData("01-7/2025/SDL-GPLHND")]
    public void BusinessLicenseNo_AcceptsBothTravelLicenseTypes(string licence)
    {
        validator.Validate(ValidCommand() with { BusinessLicenseNo = licence }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("LIC-1")]
    [InlineData("79-0123/2026/TCDL-GPLHND")]
    [InlineData("79-ABCD/2026/TCDL-GPLHQT")]
    [InlineData("79-0123/26/TCDL-GPLHQT")]
    public void BusinessLicenseNo_RejectsInvalidFormat(string licence)
    {
        validator.Validate(ValidCommand() with { BusinessLicenseNo = licence }).Errors.Should().Contain(error =>
            error.PropertyName == nameof(RegisterOperatorCommand.BusinessLicenseNo) &&
            error.ErrorCode == "OPERATOR_TRAVEL_LICENSE_INVALID");
    }

    [Theory]
    [InlineData(nameof(RegisterOperatorCommand.FirebaseIdToken), AuthErrorCodes.AuthTokenMissing)]
    [InlineData(nameof(RegisterOperatorCommand.Email), AuthErrorCodes.Msg01)]
    [InlineData(nameof(RegisterOperatorCommand.Password), AuthErrorCodes.Msg01)]
    [InlineData(nameof(RegisterOperatorCommand.ConfirmPassword), AuthErrorCodes.Msg01)]
    [InlineData(nameof(RegisterOperatorCommand.CompanyName), AuthErrorCodes.Msg01)]
    [InlineData(nameof(RegisterOperatorCommand.BusinessLicenseNo), AuthErrorCodes.Msg01)]
    [InlineData(nameof(RegisterOperatorCommand.TaxCode), AuthErrorCodes.Msg01)]
    [InlineData(nameof(RegisterOperatorCommand.ContactPerson), AuthErrorCodes.Msg01)]
    public void MissingTextField_ProducesFieldError(string field, string code)
    {
        var command = ValidCommand();
        command = field switch
        {
            nameof(RegisterOperatorCommand.FirebaseIdToken) => command with { FirebaseIdToken = " " },
            nameof(RegisterOperatorCommand.Email) => command with { Email = " " },
            nameof(RegisterOperatorCommand.Password) => command with { Password = " " },
            nameof(RegisterOperatorCommand.ConfirmPassword) => command with { ConfirmPassword = " " },
            nameof(RegisterOperatorCommand.CompanyName) => command with { CompanyName = " " },
            nameof(RegisterOperatorCommand.BusinessLicenseNo) => command with { BusinessLicenseNo = " " },
            nameof(RegisterOperatorCommand.TaxCode) => command with { TaxCode = " " },
            _ => command with { ContactPerson = " " },
        };

        validator.Validate(command).Errors.Should().Contain(error =>
            error.PropertyName == field && error.ErrorCode == code);
    }

    [Fact]
    public void InvalidEmailAndPassword_UseSharedAuthCodes()
    {
        var command = ValidCommand() with
        {
            Email = "invalid",
            Password = "weak",
            ConfirmPassword = "weak",
        };

        var errors = validator.Validate(command).Errors;

        errors.Should().Contain(error =>
            error.PropertyName == nameof(command.Email) && error.ErrorCode == AuthErrorCodes.Msg02);
        errors.Should().Contain(error =>
            error.PropertyName == nameof(command.Password) && error.ErrorCode == AuthErrorCodes.Msg05);
    }

    [Fact]
    public void PasswordMismatch_UsesMsg06OnConfirmPassword()
    {
        var command = ValidCommand() with { ConfirmPassword = "OtherPassword123!" };

        validator.Validate(command).Errors.Should().ContainSingle(error =>
            error.PropertyName == nameof(command.ConfirmPassword) &&
            error.ErrorCode == AuthErrorCodes.Msg06);
    }

    [Fact]
    public void MissingBusinessLicense_UsesMsg157()
    {
        var command = ValidCommand() with { BusinessLicenseDocument = null };

        validator.Validate(command).Errors.Should().ContainSingle(error =>
            error.PropertyName == nameof(command.BusinessLicenseDocument) &&
            error.ErrorCode == AuthErrorCodes.Msg157);
    }

    [Theory]
    [InlineData("application/pdf", "license.png")]
    [InlineData("image/gif", "license.gif")]
    [InlineData("image/jpeg", "license.pdf")]
    public void UnsupportedDocumentType_UsesMsg158(string contentType, string fileName)
    {
        var command = ValidCommand() with
        {
            BusinessLicenseDocument = new(fileName, contentType, PdfBytes()),
        };

        validator.Validate(command).Errors.Should().Contain(error =>
            error.PropertyName == nameof(command.BusinessLicenseDocument) &&
            error.ErrorCode == AuthErrorCodes.Msg158);
    }

    [Fact]
    public void RenamedExecutableWithPdfNameAndMime_IsRejected()
    {
        var command = ValidCommand() with
        {
            BusinessLicenseDocument = new("license.pdf", "application/pdf", [(byte)'M', (byte)'Z', 0, 0, 0]),
        };

        validator.Validate(command).Errors.Should().Contain(error =>
            error.PropertyName == nameof(command.BusinessLicenseDocument) &&
            error.ErrorCode == AuthErrorCodes.Msg158);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RegisterOperatorCommandValidator.MaxFileSizeBytes + 1)]
    public void EmptyOrOversizedDocument_UsesMsg158(int size)
    {
        var command = ValidCommand() with
        {
            BusinessLicenseDocument = PdfDocument(new byte[size]),
        };

        validator.Validate(command).Errors.Should().Contain(error =>
            error.PropertyName == nameof(command.BusinessLicenseDocument) &&
            error.ErrorCode == AuthErrorCodes.Msg158);
    }

    [Theory]
    [InlineData("application/pdf", "license.pdf")]
    [InlineData("image/jpeg", "license.jpg")]
    [InlineData("image/jpeg", "license.jpeg")]
    [InlineData("image/png", "license.png")]
    public void SupportedDocumentTypes_AreAllowed(string contentType, string fileName)
    {
        var command = ValidCommand() with
        {
            BusinessLicenseDocument = new(fileName, contentType, contentType switch
            {
                "application/pdf" => PdfBytes(),
                "image/jpeg" => [0xFF, 0xD8, 0xFF, 0xD9],
                _ => [137, 80, 78, 71, 13, 10, 26, 10],
            }),
        };

        validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void FileAtFiveMegabytes_IsAllowed()
    {
        var command = ValidCommand() with
        {
            BusinessLicenseDocument = PdfDocument(
                new byte[RegisterOperatorCommandValidator.MaxFileSizeBytes]),
        };

        validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void FiveValidSupportingDocuments_AreAllowed()
    {
        var command = ValidCommand() with
        {
            SupportingDocuments = Enumerable.Range(0, 5)
                .Select(_ => PdfDocument(PdfBytes()))
                .ToArray(),
        };

        validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void SixSupportingDocuments_AreRejected()
    {
        var command = ValidCommand() with
        {
            SupportingDocuments = Enumerable.Range(0, 6)
                .Select(_ => PdfDocument(PdfBytes()))
                .ToArray(),
        };

        validator.Validate(command).Errors.Should().Contain(error =>
            error.PropertyName == nameof(command.SupportingDocuments) &&
            error.ErrorCode == AuthErrorCodes.RequestInvalid &&
            error.ErrorMessage.Contains("5", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidSupportingFile_IdentifiesItsIndex()
    {
        var command = ValidCommand() with
        {
            SupportingDocuments = [PdfDocument(PdfBytes()), new("bad.exe", "application/octet-stream", [2])],
        };

        validator.Validate(command).Errors.Should().Contain(error =>
            error.PropertyName == "SupportingDocuments[1]" &&
            error.ErrorCode == AuthErrorCodes.Msg158);
    }

    [Fact]
    public void OptionalContactFields_RespectSchemaAndPhoneRule()
    {
        var command = ValidCommand() with
        {
            BusinessAddress = new string('a', 301),
            ContactPhone = "12345",
        };

        var errors = validator.Validate(command).Errors;

        errors.Should().Contain(error =>
            error.PropertyName == nameof(command.BusinessAddress) &&
            error.ErrorCode == AuthErrorCodes.RequestInvalid);
        errors.Should().Contain(error =>
            error.PropertyName == nameof(command.ContactPhone) &&
            error.ErrorCode == AuthErrorCodes.Msg04);
    }

    [Fact]
    public void OverlongRequiredBusinessFields_AreRejectedBeforePersistence()
    {
        var command = ValidCommand() with
        {
            CompanyName = new string('a', 201),
            BusinessLicenseNo = new string('b', 101),
            TaxCode = new string('c', 51),
            ContactPerson = new string('d', 151),
        };

        var errors = validator.Validate(command).Errors;

        errors.Should().Contain(error =>
            error.PropertyName == nameof(command.CompanyName) &&
            error.ErrorCode == AuthErrorCodes.RequestInvalid);
        errors.Should().Contain(error =>
            error.PropertyName == nameof(command.BusinessLicenseNo) &&
            error.ErrorCode == AuthErrorCodes.RequestInvalid);
        errors.Should().Contain(error =>
            error.PropertyName == nameof(command.TaxCode) &&
            error.ErrorCode == AuthErrorCodes.RequestInvalid);
        errors.Should().Contain(error =>
            error.PropertyName == nameof(command.ContactPerson) &&
            error.ErrorCode == AuthErrorCodes.RequestInvalid);
    }

    [Fact]
    public void UnacceptedTerms_AreRejected()
    {
        var command = ValidCommand() with { AcceptTerms = false };

        validator.Validate(command).Errors.Should().Contain(error =>
            error.PropertyName == nameof(command.AcceptTerms) &&
            error.ErrorCode == AuthErrorCodes.MsgTos);
    }

    private static RegisterOperatorCommand ValidCommand() => new(
        FirebaseIdToken: "firebase-token",
        Email: "operator@example.com",
        Password: "Password123!",
        ConfirmPassword: "Password123!",
        CompanyName: "TripMate Tours",
        BusinessLicenseNo: "79-0123/2026/TCDL-GPLHQT",
        TaxCode: "0101234567",
        ContactPerson: "Nguyễn An",
        BusinessAddress: null,
        ContactPhone: null,
        BusinessLicenseDocument: PdfDocument(PdfBytes()),
        SupportingDocuments: null,
        AcceptTerms: true);

    private static OperatorRegistrationDocument PdfDocument(byte[] bytes)
    {
        if (bytes.Length >= 5)
        {
            "%PDF-"u8.CopyTo(bytes);
        }

        return new("license.pdf", "application/pdf", bytes);
    }

    private static byte[] PdfBytes() => [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'];
}