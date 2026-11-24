using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Operator.Application.GetCurrent;
using TripMate.Application.Features.Operator.Contracts.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Authentication;

public sealed class OperatorContractAccountEligibilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-24T00:00:00Z");

    [Fact]
    public async Task Disabled_gate_preserves_legacy_auth_without_reading_new_tables()
    {
        await using var db = TestDbContext.Create();
        var user = new User { Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user); db.OperatorProfiles.Add(new OperatorProfile { User = user, ApprovalStatus = OperatorApprovalStatus.Approved });
        await db.SaveChangesAsync();
        var result = await AccountEligibilityResolver.ResolveAsync(db, user, Now, default);
        Assert.True(result.IsSuccess);
        Assert.Equal("Active", result.Value.OnboardingState);
    }

    [Fact]
    public async Task Pending_signature_remains_auth_eligible_but_is_not_workspace_active()
    {
        await using var db = new ContractDb();
        var user = new User
        {
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingContractSignature,
            CreatedAtUtc = Now.AddDays(-2),
            EmailVerifiedAtUtc = Now.AddDays(-1)
        };
        db.Users.Add(user); db.OperatorProfiles.Add(new OperatorProfile { User = user, ApprovalStatus = OperatorApprovalStatus.Approved });
        await db.SaveChangesAsync();
        var contract = OperatorContract.Create(user.Id, 1, 1, "source", new string('a', 64), "Contract", "Company", "000", "LIC", "", "operator@example.invalid", 9, Now);
        db.OperatorContracts.Add(contract); await db.SaveChangesAsync();
        var result = await AccountEligibilityResolver.ResolveAsync(db, user, Now, default, true);
        Assert.True(result.IsSuccess);
        Assert.Equal(AccountStatus.Active, result.Value.Status);
        Assert.Equal("AwaitingSignature", result.Value.OnboardingState);
        Assert.Equal("PendingSignature", result.Value.ContractStatus);
        Assert.Equal(contract.Id.ToString(), result.Value.CurrentContractId);
        Assert.Equal(AccountStatus.PendingContractSignature, user.Status);
    }

    [Fact]
    public async Task Active_approved_account_without_signed_contract_or_cutover_exemption_fails_closed()
    {
        await using var db = new ContractDb();
        var user = new User { Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user); db.OperatorProfiles.Add(new OperatorProfile { User = user, ApprovalStatus = OperatorApprovalStatus.Approved });
        await db.SaveChangesAsync();
        var result = await AccountEligibilityResolver.ResolveAsync(db, user, Now, default, true);
        Assert.True(result.IsFailure);
        db.OperatorContractLegacyExemptions.Add(OperatorContractLegacyExemption.Create(user.Id, Now, "cutover"));
        await db.SaveChangesAsync();
        Assert.True((await AccountEligibilityResolver.ResolveAsync(db, user, Now, default, true)).IsSuccess);
    }

    [Fact]
    public async Task Application_status_identifies_newly_approved_pending_signature_without_treating_it_as_active()
    {
        await using var db = new ContractDb();
        var user = new User
        {
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingContractSignature,
            CreatedAtUtc = Now.AddDays(-2),
            EmailVerifiedAtUtc = Now.AddDays(-1)
        };
        db.Users.Add(user); db.OperatorProfiles.Add(new OperatorProfile { User = user, CompanyName = "Approved company", ApprovalStatus = OperatorApprovalStatus.Approved });
        await db.SaveChangesAsync();
        var contract = OperatorContract.Create(user.Id, 1, 1, "source", new string('a', 64), "Contract", "Approved company", "000", "LIC", "", "operator@example.invalid", 9, Now);
        db.OperatorContracts.Add(contract); await db.SaveChangesAsync();
        var handler = new GetOperatorApplicationQueryHandler(db,
            new FakeCurrentUserService { UserId = user.Id, Role = nameof(UserRole.TourOperator) }, new UnusedStorage(),
            new FakeDateTimeProvider { UtcNow = Now }, Options.Create(new OperatorContractOptions { Enabled = true }));
        var result = await handler.Handle(new GetOperatorApplicationQuery(), default);
        Assert.True(result.IsSuccess);
        Assert.Equal("PendingContractSignature", result.Value.UserStatus);
        Assert.Equal("Approved", result.Value.ApprovalStatus);
        Assert.Equal("AwaitingSignature", result.Value.OnboardingState);
        Assert.Equal(contract.Id.ToString(), result.Value.CurrentContractId);
    }

    private sealed class UnusedStorage : IOperatorDocumentStorage
    {
        public string AllocatePublicId() => throw new NotSupportedException();
        public Task<OperatorDocumentStorageUploadResult> UploadAsync(OperatorDocumentStorageUpload request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OperatorDocumentStorageDeleteResult> DeleteAsync(string publicId, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Uri? CreateTemporaryDownloadUrl(string storedReference, DateTimeOffset expiresAtUtc) => throw new NotSupportedException();
    }

    private sealed class ContractDb() : TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options), IApplicationDbContext
    {
        public DbSet<OperatorContract> OperatorContracts => Set<OperatorContract>();
        public DbSet<OperatorContractLegacyExemption> OperatorContractLegacyExemptions => Set<OperatorContractLegacyExemption>();
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<OperatorContractLegacyExemption>().HasKey(x => x.OperatorUserId);
        }
    }
}