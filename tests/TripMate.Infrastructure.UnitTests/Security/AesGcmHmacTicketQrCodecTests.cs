using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Security;

namespace TripMate.Infrastructure.UnitTests.Security;

public sealed class AesGcmHmacTicketQrCodecTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 10, 24, 8, 15, 30, TimeSpan.Zero);

    [Fact]
    public void Issue_ThenVerify_RoundTripsTheTicketAndTimestamp()
    {
        var codec = NewCodec();

        var payload = codec.Issue(77, IssuedAt);
        var verified = codec.TryVerify(payload.Content, out var verification);

        verified.Should().BeTrue();
        verification!.TicketId.Should().Be(77);
        verification.QrToken.Should().Be(payload.QrToken);
        verification.IssuedAtUtc.Should().Be(IssuedAt);
    }

    [Fact]
    public void Issue_ContentIsVersionTokenAndSignature()
    {
        var payload = NewCodec().Issue(77, IssuedAt);

        payload.Content.Should().Be($"{AesGcmHmacTicketQrCodec.ContentPrefix}.{payload.QrToken}.{payload.Signature}");
        payload.Content.Split('.').Should().HaveCount(3);
        payload.QrToken.Length.Should().BeLessThanOrEqualTo(TicketQrCode.QrTokenMaxLength);
        payload.Signature.Length.Should().BeLessThanOrEqualTo(TicketQrCode.SignatureMaxLength);
    }

    [Fact]
    public void ContentFor_TheStoredTokenAndSignature_RebuildsAVerifiableContent()
    {
        var codec = NewCodec();
        var payload = codec.Issue(77, IssuedAt);

        var rebuilt = codec.ContentFor(payload.QrToken, payload.Signature);

        rebuilt.Should().Be(payload.Content);
        codec.TryVerify(rebuilt, out _).Should().BeTrue();
    }

    [Fact]
    public void Issue_TwiceForTheSameTicket_ProducesDifferentTokens()
    {
        var codec = NewCodec();

        var first = codec.Issue(77, IssuedAt);
        var second = codec.Issue(77, IssuedAt);

        second.QrToken.Should().NotBe(first.QrToken);
        second.Signature.Should().NotBe(first.Signature);
    }

    [Fact]
    public void Issue_PlaintextHoldsOnlyVersionTicketNonceAndTimestamp()
    {
        var (encryptionKey, signingKey) = (NewKey(), NewKey());
        var payload = NewCodec(encryptionKey, signingKey).Issue(77, IssuedAt);

        var raw = Base64Url.DecodeFromChars(payload.QrToken);
        var nonce = raw[..12];
        var tag = raw[^16..];
        var ciphertext = raw[12..^16];
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(encryptionKey, 16))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext,
                Encoding.ASCII.GetBytes(AesGcmHmacTicketQrCodec.ContentPrefix));
        }

        using var document = JsonDocument.Parse(plaintext);
        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("v", "tid", "n", "iat");
        document.RootElement.GetProperty("tid").GetInt64().Should().Be(77);
        document.RootElement.GetProperty("iat").GetInt64().Should().Be(IssuedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void TryVerify_WithATamperedTokenOrSignature_Fails()
    {
        var codec = NewCodec();
        var payload = codec.Issue(77, IssuedAt);

        codec.TryVerify($"TM1.{Flip(payload.QrToken)}.{payload.Signature}", out _).Should().BeFalse();
        codec.TryVerify($"TM1.{payload.QrToken}.{Flip(payload.Signature)}", out _).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_WithAValidSignatureOverAForgedToken_Fails()
    {
        var signingKey = NewKey();
        var codec = NewCodec(NewKey(), signingKey);
        var forgedToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));
        var signature = Base64Url.EncodeToString(
            HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes($"TM1.{forgedToken}")));

        codec.TryVerify($"TM1.{forgedToken}.{signature}", out _).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_WithOtherKeys_Fails()
    {
        var payload = NewCodec().Issue(77, IssuedAt);

        NewCodec().TryVerify(payload.Content, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("TM1")]
    [InlineData("TM1..")]
    [InlineData("TM2.a.b")]
    [InlineData("TM1.!!!.???")]
    [InlineData("TM1.a.b.c")]
    public void TryVerify_WithMalformedContent_FailsWithoutThrowing(string content)
    {
        NewCodec().TryVerify(content, out var verification).Should().BeFalse();
        verification.Should().BeNull();
    }

    [Fact]
    public void TryVerify_WithReorderedParts_Fails()
    {
        var codec = NewCodec();
        var payload = codec.Issue(77, IssuedAt);

        codec.TryVerify($"TM1.{payload.Signature}.{payload.QrToken}", out _).Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithUnusableKeys_FailsClosedWithoutKeyMaterial()
    {
        var options = new TicketQrOptions { EncryptionKey = "not-base64", SigningKey = "secret-value" };

        var action = () => new AesGcmHmacTicketQrCodec(Options.Create(options));

        action.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("TicketQr").And.NotContain("secret-value");
    }

    internal static byte[] NewKey() => RandomNumberGenerator.GetBytes(TicketQrOptions.KeyLengthBytes);

    private static AesGcmHmacTicketQrCodec NewCodec() => NewCodec(NewKey(), NewKey());

    private static AesGcmHmacTicketQrCodec NewCodec(byte[] encryptionKey, byte[] signingKey) =>
        new(Options.Create(new TicketQrOptions
        {
            EncryptionKey = Convert.ToBase64String(encryptionKey),
            SigningKey = Convert.ToBase64String(signingKey),
        }));

    private static string Flip(string value)
    {
        var characters = value.ToCharArray();
        characters[characters.Length / 2] = characters[characters.Length / 2] == 'A' ? 'B' : 'A';
        return new string(characters);
    }
}