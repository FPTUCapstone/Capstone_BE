using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class TicketQrCodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issue_CreatesAnActiveCode()
    {
        var code = TicketQrCode.Issue(ticketId: 77, qrToken: "token", signature: "signature", Now);

        code.TicketId.Should().Be(77);
        code.QrToken.Should().Be("token");
        code.Signature.Should().Be("signature");
        code.Status.Should().Be(TicketQrCodeStatus.Active);
        code.IssuedAtUtc.Should().Be(Now);
        code.ExpiresAtUtc.Should().BeNull();
        code.ConsumedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Issue_WithValuesLongerThanTheColumns_Throws()
    {
        var tooLong = new string('a', TicketQrCode.QrTokenMaxLength + 1);

        var tokenAction = () => TicketQrCode.Issue(77, tooLong, "signature", Now);
        var signatureAction = () => TicketQrCode.Issue(77, "token",
            new string('a', TicketQrCode.SignatureMaxLength + 1), Now);

        tokenAction.Should().Throw<ArgumentOutOfRangeException>();
        signatureAction.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0L, "token", "signature")]
    [InlineData(77L, "", "signature")]
    [InlineData(77L, "token", " ")]
    public void Issue_WithMissingValues_Throws(long ticketId, string qrToken, string signature)
    {
        var action = () => TicketQrCode.Issue(ticketId, qrToken, signature, Now);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Revoke_AnActiveCode_MarksItRevoked()
    {
        var code = TicketQrCode.Issue(77, "token", "signature", Now);

        var revoked = code.Revoke();

        revoked.Should().BeTrue();
        code.Status.Should().Be(TicketQrCodeStatus.Revoked);
    }

    [Fact]
    public void Revoke_AnAlreadyRevokedCode_IsANoOp()
    {
        var code = TicketQrCode.Issue(77, "token", "signature", Now);
        code.Revoke();

        var revokedAgain = code.Revoke();

        revokedAgain.Should().BeFalse();
        code.Status.Should().Be(TicketQrCodeStatus.Revoked);
    }
}
