using FluentAssertions;

using Microsoft.Extensions.Configuration;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.OperatorPaymentChannels.Approve;
using TripMate.Application.Features.Admin.OperatorPaymentChannels.Disable;
using TripMate.Application.Features.Operator.PaymentChannels.Common;
using TripMate.Application.Features.Operator.PaymentChannels.GetChannel;
using TripMate.Application.Features.Operator.PaymentChannels.Submit;
using TripMate.Application.Features.Operator.PaymentChannels.Verify;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.PaymentChannels;

public sealed class OperatorPaymentChannelHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static GetOperatorPaymentChannelQueryHandler GetHandler(
        TestDbContext db, long? userId = 7, string? role = nameof(UserRole.TourOperator)) =>
        new(db, new FakeCurrentUserService { UserId = userId, Role = role });

    private static SubmitPaymentChannelCredentialsCommandHandler SubmitHandler(
        TestDbContext db, long? userId = 7, string? role = nameof(UserRole.TourOperator)) =>
        new(db, new FakeCurrentUserService { UserId = userId, Role = role },
            new FakeDateTimeProvider { UtcNow = Now },
            new FakePaymentCredentialProtector());

    private static VerifyPaymentChannelCommandHandler VerifyHandler(
        TestDbContext db, FakePaymentChannelVerifier verifier,
        long? userId = 7, string? role = nameof(UserRole.TourOperator))
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new("PayOs:WebhookBaseUrl", "https://webhook.example.com")])
            .Build();
        return new(db, new FakeCurrentUserService { UserId = userId, Role = role },
            new FakeDateTimeProvider { UtcNow = Now },
            new FakePaymentCredentialProtector(),
            verifier, config);
    }

    private static ApproveOperatorPaymentChannelCommandHandler ApproveHandler(
        TestDbContext db, string? role = nameof(UserRole.Administrator)) =>
        new(db, new FakeCurrentUserService { UserId = 99, Role = role },
            new FakeDateTimeProvider { UtcNow = Now });

    private static DisableOperatorPaymentChannelCommandHandler DisableHandler(
        TestDbContext db, string? role = nameof(UserRole.Administrator)) =>
        new(db, new FakeCurrentUserService { UserId = 99, Role = role },
            new FakeDateTimeProvider { UtcNow = Now });

    private static OperatorPaymentChannel SeedChannel(
        TestDbContext db, long operatorId = 7,
        string status = OperatorPaymentChannel.StatusPendingVerification,
        int version = 2,
        string? webhookUrl = null)
    {
        var channel = OperatorPaymentChannel.Create(operatorId, Now.AddDays(-1));
        channel.SetCredentialRefs(
            "ref:client-id", "ref:api-key", "ref:checksum",
            "1234567890", "Nguyen Van A", "VCB",
            Now.AddDays(-1));
        Set(channel, nameof(OperatorPaymentChannel.Status), status);
        Set(channel, nameof(OperatorPaymentChannel.ChannelVersion), version);
        if (webhookUrl is not null)
        {
            Set(channel, nameof(OperatorPaymentChannel.WebhookUrl), webhookUrl);
            Set(channel, nameof(OperatorPaymentChannel.WebhookRegisteredAtUtc), (DateTimeOffset?)Now);
            Set(channel, nameof(OperatorPaymentChannel.LastVerifiedAtUtc), (DateTimeOffset?)Now);
        }
        db.OperatorPaymentChannels.Add(channel);
        db.SaveChanges();
        return channel;
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType()
            .GetProperty(property,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)!
            .SetValue(instance, value);

    // ---- U1: GetChannel — non-TO role → Forbidden ----

    [Fact]
    public async Task GetChannel_NonTourOperatorRole_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var result = await GetHandler(db, userId: 50, role: nameof(UserRole.Traveler))
            .Handle(new GetOperatorPaymentChannelQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.Forbidden);
    }

    // ---- U2: GetChannel — no channel exists → NotConnected 200 ----

    [Fact]
    public async Task GetChannel_NoChannelExists_ReturnsNotConnectedDto()
    {
        await using var db = TestDbContext.Create();
        var result = await GetHandler(db)
            .Handle(new GetOperatorPaymentChannelQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OperatorPaymentChannel.StatusNotConnected);
        result.Value.ChannelId.Should().BeNull();
    }

    // ---- U3: GetChannel — channel exists → DTO with correct status ----

    [Fact]
    public async Task GetChannel_ChannelExists_ReturnsDtoWithStatus()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        SeedChannel(db, operatorId: 7, status: OperatorPaymentChannel.StatusPendingVerification);

        var result = await GetHandler(db)
            .Handle(new GetOperatorPaymentChannelQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OperatorPaymentChannel.StatusPendingVerification);
        result.Value.ChannelId.Should().NotBeNull();
    }

    // ---- U4: Submit — non-TO → Forbidden ----

    [Fact]
    public async Task Submit_NonTourOperatorRole_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var result = await SubmitHandler(db, userId: 50, role: nameof(UserRole.Traveler))
            .Handle(ValidSubmitCommand(), CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.Forbidden);
    }

    // ---- U5: Submit — no approved profile → Forbidden ----

    [Fact]
    public async Task Submit_NoApprovedProfile_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        // No profile seeded for operatorId 7

        var result = await SubmitHandler(db)
            .Handle(ValidSubmitCommand(), CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.Forbidden);
    }

    // ---- U6: Submit — blank credentials → ValidationFailed ----

    [Fact]
    public async Task Submit_BlankCredentials_ReturnsValidationFailed()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);

        var result = await SubmitHandler(db)
            .Handle(new SubmitPaymentChannelCredentialsCommand(
                ClientId: "", ApiKey: "k", ChecksumKey: "c",
                BeneficiaryAccountNumber: "1234", BeneficiaryAccountName: "Name", BeneficiaryBankCode: "VCB"),
            CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.ValidationFailed);
    }

    // ---- U7: Submit — blank beneficiary → ValidationFailed ----

    [Fact]
    public async Task Submit_BlankBeneficiary_ReturnsValidationFailed()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);

        var result = await SubmitHandler(db)
            .Handle(new SubmitPaymentChannelCredentialsCommand(
                ClientId: "client", ApiKey: "key", ChecksumKey: "checksum",
                BeneficiaryAccountNumber: "", BeneficiaryAccountName: "Name", BeneficiaryBankCode: "VCB"),
            CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.ValidationFailed);
    }

    // ---- U8: Submit — new channel → created with PendingVerification ----

    [Fact]
    public async Task Submit_NewChannel_CreatesChannelInPendingVerification()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);

        var result = await SubmitHandler(db)
            .Handle(ValidSubmitCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OperatorPaymentChannel.StatusPendingVerification);
        db.OperatorPaymentChannels.Should().HaveCount(1);
        var channel = db.OperatorPaymentChannels.Single();
        channel.ClientIdRef.Should().NotBeNull();
        channel.BeneficiaryAccountNumber.Should().Be("1234567890");
    }

    // ---- U9: Submit — existing channel → updated, still PendingVerification ----

    [Fact]
    public async Task Submit_ExistingChannel_UpdatesCredentialsInPendingVerification()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        SeedChannel(db, 7, OperatorPaymentChannel.StatusReady, version: 3);

        var result = await SubmitHandler(db)
            .Handle(ValidSubmitCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OperatorPaymentChannel.StatusPendingVerification);
        db.OperatorPaymentChannels.Should().HaveCount(1);
    }

    // ---- U10: Verify — no channel → NotFound ----

    [Fact]
    public async Task Verify_NoChannel_ReturnsNotFound()
    {
        await using var db = TestDbContext.Create();
        var result = await VerifyHandler(db, new FakePaymentChannelVerifier())
            .Handle(new VerifyPaymentChannelCommand(), CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.NotFound);
    }

    // ---- U11: Verify — credentials missing → CredentialsMissing ----

    [Fact]
    public async Task Verify_CredentialsMissing_ReturnsCredentialsMissing()
    {
        await using var db = TestDbContext.Create();
        var bare = OperatorPaymentChannel.Create(7, Now);
        db.OperatorPaymentChannels.Add(bare);
        await db.SaveChangesAsync();

        var result = await VerifyHandler(db, new FakePaymentChannelVerifier())
            .Handle(new VerifyPaymentChannelCommand(), CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.CredentialsMissing);
    }

    // ---- U12: Verify — ConnectionError → MarkConnectionError + VerificationFailed ----

    [Fact]
    public async Task Verify_ConnectionError_MarksChannelConnectionErrorAndReturnsFailure()
    {
        await using var db = TestDbContext.Create();
        SeedChannel(db, 7);

        var verifier = new FakePaymentChannelVerifier
        {
            OutcomeToReturn = ChannelVerificationOutcome.ConnectionError,
            ErrorCodeToReturn = "PAYOS_TIMEOUT"
        };

        var result = await VerifyHandler(db, verifier)
            .Handle(new VerifyPaymentChannelCommand(), CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.VerificationFailed);
        db.OperatorPaymentChannels.Single().Status.Should().Be(OperatorPaymentChannel.StatusConnectionError);
    }

    // ---- U13: Verify — PendingAdminApproval → no mutation, still PendingVerification ----

    [Fact]
    public async Task Verify_PendingAdminApproval_WaitsForOwnershipApproval()
    {
        await using var db = TestDbContext.Create();
        SeedChannel(db, 7, OperatorPaymentChannel.StatusPendingVerification);

        var verifier = new FakePaymentChannelVerifier
        {
            OutcomeToReturn = ChannelVerificationOutcome.PendingAdminApproval
        };

        var result = await VerifyHandler(db, verifier)
            .Handle(new VerifyPaymentChannelCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OperatorPaymentChannel.StatusPendingVerification);
        db.OperatorPaymentChannels.Single().Status.Should().Be(OperatorPaymentChannel.StatusPendingVerification);
    }

    // ---- U14: Verify — AutoApproved → MarkReady ----

    [Fact]
    public async Task Verify_AutoApproved_StillRequiresAdminReview()
    {
        await using var db = TestDbContext.Create();
        SeedChannel(db, 7);

        var verifier = new FakePaymentChannelVerifier
        {
            OutcomeToReturn = ChannelVerificationOutcome.AutoApproved,
            WebhookUrlToReturn = null
        };

        var result = await VerifyHandler(db, verifier)
            .Handle(new VerifyPaymentChannelCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OperatorPaymentChannel.StatusPendingVerification);
        db.OperatorPaymentChannels.Single().Status.Should().Be(OperatorPaymentChannel.StatusPendingVerification);
    }

    // ---- U15: Approve — non-Admin → Forbidden ----

    [Fact]
    public async Task Approve_NonAdminRole_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var channel = SeedChannel(db, 7);

        var result = await ApproveHandler(db, role: nameof(UserRole.TourOperator))
            .Handle(new ApproveOperatorPaymentChannelCommand(
                channel.Id, Guid.NewGuid(), 2, "reason"), CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.Forbidden);
    }

    // ---- U16: Approve — version conflict → VersionConflict ----

    [Fact]
    public async Task Approve_VersionConflict_ReturnsVersionConflict()
    {
        await using var db = TestDbContext.Create();
        var channel = SeedChannel(db, 7, version: 2);

        var result = await ApproveHandler(db)
            .Handle(new ApproveOperatorPaymentChannelCommand(
                channel.Id, Guid.NewGuid(), ExpectedVersion: 99, "reason"), CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.VersionConflict);
    }

    // ---- U17: Approve — wrong status → InvalidState ----

    [Fact]
    public async Task Approve_ChannelNotPendingVerification_ReturnsInvalidState()
    {
        await using var db = TestDbContext.Create();
        var channel = SeedChannel(db, 7, OperatorPaymentChannel.StatusReady, version: 3);

        var result = await ApproveHandler(db)
            .Handle(new ApproveOperatorPaymentChannelCommand(
                channel.Id, Guid.NewGuid(), ExpectedVersion: 3, "reason"), CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.InvalidState);
    }

    // ---- U18: Approve — happy path → Ready ----

    [Fact]
    public async Task Approve_ValidRequest_MarksChannelReady()
    {
        await using var db = TestDbContext.Create();
        var channel = SeedChannel(db, 7, OperatorPaymentChannel.StatusPendingVerification, version: 2,
            webhookUrl: "https://hook.example.test/channel/1/webhook");

        var result = await ApproveHandler(db)
            .Handle(new ApproveOperatorPaymentChannelCommand(
                channel.Id, Guid.NewGuid(), ExpectedVersion: 2, "Verified by admin"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OperatorPaymentChannel.StatusReady);
        db.OperatorPaymentChannels.Single().Status.Should().Be(OperatorPaymentChannel.StatusReady);
    }

    // ---- U19: Disable — non-Admin → Forbidden ----

    [Fact]
    public async Task Disable_NonAdminRole_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var channel = SeedChannel(db, 7);

        var result = await DisableHandler(db, role: nameof(UserRole.TourOperator))
            .Handle(new DisableOperatorPaymentChannelCommand(
                channel.Id, Guid.NewGuid(), 2, "reason"), CancellationToken.None);

        result.ErrorCode.Should().Be(PaymentChannelErrorCodes.Forbidden);
    }

    // ---- U20: Disable — happy path → Disabled ----

    [Fact]
    public async Task Disable_ValidRequest_MarksChannelDisabled()
    {
        await using var db = TestDbContext.Create();
        var channel = SeedChannel(db, 7, OperatorPaymentChannel.StatusReady, version: 3);

        var result = await DisableHandler(db)
            .Handle(new DisableOperatorPaymentChannelCommand(
                channel.Id, Guid.NewGuid(), ExpectedVersion: 3, "Security audit"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OperatorPaymentChannel.StatusDisabled);
        db.OperatorPaymentChannels.Single().Status.Should().Be(OperatorPaymentChannel.StatusDisabled);
    }

    [Fact]
    public async Task AnotherOperatorCannotClaimCurrentOrRetiredMerchant()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        await SeedApprovedOperatorAsync(db, 8);
        (await SubmitHandler(db).Handle(ValidSubmitCommand(), default)).IsSuccess.Should().BeTrue();
        var second = await SubmitHandler(db, userId: 8).Handle(ValidSubmitCommand(), default);
        second.ErrorCode.Should().Be("payment_channel.merchant_in_use");
        (await SubmitHandler(db).Handle(ValidSubmitCommand() with { ClientId = "new-merchant" }, default)).IsSuccess.Should().BeTrue();
        var retired = await SubmitHandler(db, userId: 8).Handle(ValidSubmitCommand(), default);
        retired.ErrorCode.Should().Be("payment_channel.merchant_in_use");
    }

    private static SubmitPaymentChannelCredentialsCommand ValidSubmitCommand() =>
        new("client-id", "api-key", "checksum-key",
            "1234567890", "Nguyen Van A", "VCB");
}