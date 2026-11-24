using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Application.Features.Operator.Contracts;
using TripMate.Application.Features.Operator.Contracts.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Contracts;

public sealed class OperatorContractWorkflowTests
{
    [Fact]
    public async Task OtherRolesAndOtherOwnersCannotReadOrSignContracts()
    {
        await using var fixture = await Fixture.Create();
        fixture.Actor.Role = "Traveler";
        Assert.Equal("contract.forbidden", (await fixture.Workflow.GetCurrentAsync(default)).ErrorCode);
        fixture.Actor.Role = "TourOperator"; fixture.Actor.UserId = 43;
        fixture.Db.Users.Add(new User
        {
            Id = 43,
            Email = "other@example.invalid",
            FullName = "Other operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = fixture.Clock.UtcNow.AddDays(-3),
            EmailVerifiedAtUtc = fixture.Clock.UtcNow.AddDays(-2)
        });
        fixture.Db.OperatorProfiles.Add(new OperatorProfile { UserId = 43, ApprovalStatus = OperatorApprovalStatus.Approved, CompanyName = "Other", TaxCode = "0123456788", BusinessLicenseNo = "TEST-002" });
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("contract.not_found", (await fixture.Workflow.DownloadAsync(7, "unsigned", default)).ErrorCode);
        Assert.Equal(0, fixture.Email.Count);
    }

    [Fact]
    public async Task EmailChallengeUsesOwnerAndCannotBeResentBeforeCooldown()
    {
        await using var fixture = await Fixture.Create();
        var current = (await fixture.Workflow.GetCurrentAsync(default)).Value;
        var first = await fixture.Workflow.CreateChallengeAsync(7, new(current.AcceptedContentSha256!, current.RowVersion), default);
        Assert.True(first.IsSuccess, first.ErrorMessage);
        Assert.Equal("operator@example.invalid", fixture.Email.Destination);
        Assert.Equal(180, (first.Value.ExpiresAtUtc - fixture.Clock.UtcNow).TotalSeconds);
        var again = await fixture.Workflow.CreateChallengeAsync(7, new(current.AcceptedContentSha256!, first.Value.RowVersion), default);
        Assert.Equal("contract.otp_cooldown", again.ErrorCode);
        Assert.Equal(1, fixture.Email.Count);
        Assert.Equal(AccountStatus.PendingContractSignature, fixture.Owner.Status);
    }

    [Fact]
    public async Task SignedArtifactAndValidOtpActivateExactlyOnceAndReplaySameOperation()
    {
        await using var fixture = await Fixture.Create();
        var input = await fixture.SignInput();
        var signed = await fixture.Workflow.SignAsync(7, input, default);
        Assert.True(signed.IsSuccess, signed.ErrorMessage);
        Assert.Equal("Signed", signed.Value.Status);
        Assert.Equal(AccountStatus.Active, fixture.Owner.Status);
        Assert.Equal(1, fixture.Owner.SessionVersion);
        Assert.Equal(OperatorContractChallengeStatus.Used, (await fixture.Db.OperatorContractSigningChallenges.SingleAsync()).Status);
        var writes = fixture.Storage.Writes;
        Assert.True((await fixture.Workflow.SignAsync(7, input, default)).IsSuccess);
        Assert.Equal(writes, fixture.Storage.Writes);
        Assert.Equal(1, fixture.Owner.SessionVersion);
        Assert.Equal("contract.conflict", (await fixture.Workflow.SignAsync(7, input with { Signature = [9, 9] }, default)).ErrorCode);
    }

    [Fact]
    public async Task WrongOtpHasBoundedAttemptsAndNeverActivatesAccount()
    {
        await using var fixture = await Fixture.Create();
        var input = await fixture.SignInput();
        for (var index = 0; index < 5; index++)
            Assert.Equal("contract.otp_invalid", (await fixture.Workflow.SignAsync(7, input with { Otp = "000000" }, default)).ErrorCode);
        Assert.Equal(OperatorContractChallengeStatus.Failed, (await fixture.Db.OperatorContractSigningChallenges.SingleAsync()).Status);
        Assert.Equal(AccountStatus.PendingContractSignature, fixture.Owner.Status);
        Assert.Equal(0, fixture.Storage.Writes);
    }

    [Fact]
    public async Task StorageFailureDoesNotConsumeOtpOrActivateAccount()
    {
        await using var fixture = await Fixture.Create();
        var input = await fixture.SignInput();
        fixture.Storage.Fail = true;
        Assert.Equal(ContractTemplateDocumentErrors.StorageUnavailable, (await fixture.Workflow.SignAsync(7, input, default)).ErrorCode);
        Assert.Equal(AccountStatus.PendingContractSignature, fixture.Owner.Status);
        Assert.Equal(OperatorContractChallengeStatus.Sent, (await fixture.Db.OperatorContractSigningChallenges.SingleAsync()).Status);
    }

    [Fact]
    public async Task ExpiredOtpWrongHashAndMissingConsentNeverActivate()
    {
        await using var fixture = await Fixture.Create();
        var input = await fixture.SignInput();
        Assert.Equal("contract.validation", (await fixture.Workflow.SignAsync(7, input with { AcceptedTerms = false }, default)).ErrorCode);
        Assert.Equal("contract.conflict", (await fixture.Workflow.SignAsync(7, input with { ExpectedContentSha256 = new string('f', 64) }, default)).ErrorCode);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(3);
        Assert.Equal("contract.otp_expired", (await fixture.Workflow.SignAsync(7, input, default)).ErrorCode);
        Assert.Equal(AccountStatus.PendingContractSignature, fixture.Owner.Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ContractDb Db { get; } = new();
        public FakeCurrentUserService Actor { get; } = new() { UserId = 42, Role = "TourOperator" };
        public FakeDateTimeProvider Clock { get; } = new() { UtcNow = DateTimeOffset.Parse("2026-11-24T00:00:00Z") };
        public MemoryStorage Storage { get; } = new();
        public Email Email { get; } = new();
        public User Owner { get; private set; } = null!;
        public OperatorContractWorkflow Workflow { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            f.Owner = new User
            {
                Id = 42,
                Email = "operator@example.invalid",
                FullName = "Nguyễn Văn A",
                Role = UserRole.TourOperator,
                Status = AccountStatus.PendingContractSignature,
                CreatedAtUtc = f.Clock.UtcNow.AddDays(-3),
                EmailVerifiedAtUtc = f.Clock.UtcNow.AddDays(-2)
            };
            f.Db.Users.Add(f.Owner);
            f.Db.OperatorProfiles.Add(new OperatorProfile { UserId = 42, ApprovalStatus = OperatorApprovalStatus.Approved, CompanyName = "Đối tác", TaxCode = "0123456789", BusinessLicenseNo = "TEST-001" });
            var c = OperatorContract.Create(42, 1, 1, "source", Hash([1]), "Hợp đồng", "Đối tác", "0123456789", "TEST-001", "Đà Nẵng", f.Owner.Email, 77, f.Clock.UtcNow);
            c.Id = 7;
            c.TryStartPreparation(1, "Nguyễn Văn A", "Giám đốc", "Đại diện theo pháp luật", f.Clock.UtcNow);
            var claim = Guid.NewGuid(); c.TryClaimPreparation(claim, f.Clock.UtcNow, TimeSpan.FromMinutes(5));
            c.TryCompletePreparation(claim, c.Revision, Hash([1]), "unsigned", Hash([2]), "[]", f.Clock.UtcNow);
            f.Db.OperatorContracts.Add(c);
            await f.Db.SaveChangesAsync();
            f.Storage.Files["unsigned"] = [2];
            f.Workflow = new(f.Db, f.Actor, f.Clock, Options.Create(new OperatorContractOptions { Enabled = true }), f.Storage,
                new Composer(), new Renderer(), new Inspector(), new Stamper(), new Protector(), f.Email, new Codes(), new SecurityClock(f.Clock));
            return f;
        }
        public async Task<SignOperatorContractInput> SignInput()
        {
            var current = (await Workflow.GetCurrentAsync(default)).Value;
            var challenge = (await Workflow.CreateChallengeAsync(7, new(current.AcceptedContentSha256!, current.RowVersion), default)).Value;
            return new(current.AcceptedContentSha256!, challenge.RowVersion, "Nguyễn Văn A", true, OperatorContractSigningPolicy.ConsentVersion,
                challenge.ChallengeId, "123456", Guid.NewGuid(), [1, 2, 3]);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class ContractDb() : TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options), IApplicationDbContext
    {
        public DbSet<OperatorContract> OperatorContracts => Set<OperatorContract>();
        public DbSet<OperatorContractSigningChallenge> OperatorContractSigningChallenges => Set<OperatorContractSigningChallenge>();
    }
    private sealed class MemoryStorage : IContractTemplateArtifactStorage
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public int Writes { get; private set; }
        public bool Fail { get; set; }
        public Task<Result<StoredContractTemplateArtifact>> StoreAsync(byte[] bytes, CancellationToken ct)
        {
            if (Fail) return Task.FromResult(Result.Failure<StoredContractTemplateArtifact>(ContractTemplateDocumentErrors.StorageUnavailable, "Unavailable"));
            var key = (++Writes).ToString(); Files[key] = bytes;
            return Task.FromResult(Result.Success(new StoredContractTemplateArtifact(key, Hash(bytes), bytes.Length)));
        }
        public Task<Result<byte[]>> ReadAsync(string key, CancellationToken ct) => Task.FromResult(Result.Success(Files[key]));
        public Task<Result> DeleteAsync(string key, CancellationToken ct) { Files.Remove(key); return Task.FromResult(Result.Success()); }
    }
    private sealed class Email : IOperatorContractOtpEmailSender
    {
        public int Count; public string? Destination;
        public Task<EmailDeliveryResult> SendAsync(string destinationEmail, string otp, long contractId, CancellationToken ct)
        { Count++; Destination = destinationEmail; return Task.FromResult(EmailDeliveryResult.Delivered); }
    }
    private sealed class Codes : IOtpCodeGenerator { public string Generate() => "123456"; }
    private sealed class SecurityClock(FakeDateTimeProvider clock) : TimeProvider { public override DateTimeOffset GetUtcNow() => clock.UtcNow; }
    private sealed class Protector : IOperatorContractOtpProtector
    {
        public string Protect(string otp, long userId, long contractId, string hash, Guid id, DateTimeOffset time) => $"{otp}:{userId}:{contractId}:{hash}:{id}:{time:O}";
        public bool Verify(string otp, long userId, long contractId, string hash, Guid id, DateTimeOffset time, string value) => Protect(otp, userId, contractId, hash, id, time) == value;
    }
    private sealed class Inspector : IContractSignatureImageInspector
    {
        public Result<InspectedContractSignature> Inspect(byte[] bytes) => Result.Success(new InspectedContractSignature(bytes, Hash(bytes), Hash(bytes), 600, 200));
    }
    private sealed class Stamper : IContractPdfSignatureStamper
    {
        public Result<SignedContractPdf> Stamp(byte[] pdf, InspectedContractSignature signature, IReadOnlyList<ContractSignaturePlacement> placements) => Result.Success(new SignedContractPdf([3], Hash([3])));
    }
    private sealed class Composer : IOperatorContractDocumentComposer
    {
        public Task<Result<byte[]>> PrepareAsync(byte[] bytes, string name, IReadOnlyDictionary<string, string> values, CancellationToken ct) => Task.FromResult(Result.Success(bytes));
    }
    private sealed class Renderer : IContractTemplatePdfRenderer
    {
        public Task<Result<RenderedContractTemplate>> RenderPreviewAsync(byte[] bytes, CancellationToken ct) => Task.FromResult(Result.Failure<RenderedContractTemplate>("contract.unavailable", "Unavailable"));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}