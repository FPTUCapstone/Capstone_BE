using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.TourOperatorApplications.Approve;
using TripMate.Application.Features.Operator.Contracts;
using TripMate.Application.Features.Operator.Contracts.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Contracts;

public sealed class OperatorContractApprovalTests
{
    [Fact]
    public async Task ApprovalWithPublishedTemplateCreatesOneContractAndWaitsForSignature()
    {
        await using var fixture = await Fixture.Create(true);
        var result = await fixture.Handler.Handle(new(42), default);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(AccountStatus.PendingContractSignature, result.Value.AccountStatus);
        Assert.Equal(OperatorApprovalStatus.Approved, fixture.Profile.ApprovalStatus);
        var contract = await fixture.Db.OperatorContracts.SingleAsync();
        Assert.Equal(OperatorContractStatus.PendingSignature, contract.Status);
        Assert.Equal(OperatorContractDocumentState.NeedsSignerDetails, contract.DocumentState);
        Assert.Equal(42, contract.OperatorUserId);
        Assert.DoesNotContain("Account activated", result.Value.Message);
        Assert.True((await fixture.Handler.Handle(new(42), default)).IsFailure);
        Assert.Equal(1, await fixture.Db.OperatorContracts.CountAsync());
    }

    [Fact]
    public async Task MissingPublishedTemplateDoesNotApproveOrActivate()
    {
        await using var fixture = await Fixture.Create(false);
        var result = await fixture.Handler.Handle(new(42), default);
        Assert.Equal("contract.template_unavailable", result.ErrorCode);
        Assert.Equal(AccountStatus.PendingApproval, fixture.Owner.Status);
        Assert.Equal(OperatorApprovalStatus.PendingApproval, fixture.Profile.ApprovalStatus);
        Assert.Empty(fixture.Db.OperatorContracts);
        Assert.Empty(fixture.Db.Notifications);
    }

    [Fact]
    public async Task ApprovalHoldsSerializablePublicationReadUntilAccountAndContractAreSaved()
    {
        await using var fixture = await Fixture.Create(true);
        fixture.Db.RequireSerializablePublicationRead = true;

        var result = await fixture.Handler.Handle(new(42), default);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(fixture.Db.PublicationReadWasSerializable);
        Assert.True(fixture.Db.ApprovalSaveWasSerializable);
        Assert.False(fixture.Db.IsExecutingSerializableTransaction);
        Assert.Equal(1, (await fixture.Db.OperatorContracts.SingleAsync()).TemplateId);
        Assert.Equal(AccountStatus.PendingContractSignature, fixture.Owner.Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ContractDb Db { get; } = new();
        public User Owner { get; private set; } = null!;
        public OperatorProfile Profile { get; private set; } = null!;
        public ApproveOperatorApplicationCommandHandler Handler { get; private set; } = null!;
        public static async Task<Fixture> Create(bool published)
        {
            var fixture = new Fixture();
            var clock = new FakeDateTimeProvider { UtcNow = DateTimeOffset.Parse("2026-11-24T00:00:00Z") };
            var actor = new FakeCurrentUserService { UserId = 77, Role = "Staff" };
            fixture.Owner = new User
            {
                Id = 42,
                Email = "operator@example.invalid",
                FullName = "Contact person",
                Role = UserRole.TourOperator,
                Status = AccountStatus.PendingApproval,
                CreatedAtUtc = clock.UtcNow.AddDays(-2),
                EmailVerifiedAtUtc = clock.UtcNow.AddDays(-1)
            };
            fixture.Profile = new OperatorProfile
            {
                UserId = 42,
                User = fixture.Owner,
                CompanyName = "Đối tác",
                TaxCode = "0123456789",
                BusinessLicenseNo = "TEST-001",
                ApprovalStatus = OperatorApprovalStatus.PendingApproval
            };
            fixture.Profile.Documents.Add(new OperatorDocument { OperatorUserId = 42, DocumentType = OperatorDocumentType.BusinessLicense, Status = DocumentStatus.Submitted });
            fixture.Db.Users.Add(fixture.Owner); fixture.Db.OperatorProfiles.Add(fixture.Profile);
            if (published)
            {
                var sourceHash = new string('a', 64);
                var template = OperatorContractTemplate.CreateDraft("Hợp đồng", "source", sourceHash, "[]", true, 77, clock.UtcNow);
                template.Id = 1;
                Assert.True(template.RequestPreview(1));
                Assert.True(template.TryCompletePreview(1, sourceHash, "preview", new string('b', 64), clock.UtcNow));
                Assert.True(template.TryPublish(1, 1, 77, clock.UtcNow));
                var head = ContractTemplatePublicationHead.Create();
                Assert.True(head.TryAdvance(0, null, 1));
                fixture.Db.OperatorContractTemplates.Add(template); fixture.Db.ContractTemplatePublicationHeads.Add(head);
            }
            await fixture.Db.SaveChangesAsync();
            fixture.Handler = new(fixture.Db, actor, clock, new OperatorContractIssuer(fixture.Db, actor), Options.Create(new OperatorContractOptions { Enabled = true }));
            return fixture;
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class ContractDb() : TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options), IApplicationDbContext
    {
        public bool RequireSerializablePublicationRead { get; set; }
        public bool PublicationReadWasSerializable { get; private set; }
        public bool ApprovalSaveWasSerializable { get; private set; }
        public DbSet<OperatorContract> OperatorContracts => Set<OperatorContract>();
        public DbSet<OperatorContractTemplate> OperatorContractTemplates => Set<OperatorContractTemplate>();
        public DbSet<ContractTemplatePublicationHead> ContractTemplatePublicationHeads
        {
            get
            {
                if (RequireSerializablePublicationRead)
                {
                    if (!IsExecutingSerializableTransaction)
                        throw new InvalidOperationException("Publication can change before approval commits without a serializable transaction.");
                    PublicationReadWasSerializable = true;
                }
                return Set<ContractTemplatePublicationHead>();
            }
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (RequireSerializablePublicationRead && ChangeTracker.Entries<OperatorContract>().Any(entry => entry.State == EntityState.Added))
                ApprovalSaveWasSerializable = IsExecutingSerializableTransaction;
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}