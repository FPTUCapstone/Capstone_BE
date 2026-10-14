using FluentAssertions;

using TripMate.Application.Features.Admin.Payouts.GetDetail;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts.GetDetail;

public sealed class GetPayoutDetailQueryHandlerTests
{
    private static readonly DateTimeOffset RequestedAtUtc = new(2026, 10, 3, 8, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly FakeCurrentUserService _currentUserService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly GetPayoutDetailQueryHandler _handler;

    public GetPayoutDetailQueryHandlerTests()
    {
        _currentUserService.UserId = 1;
        _currentUserService.Role = "Staff";
        _dateTimeProvider.UtcNow = NowUtc;
        _handler = new GetPayoutDetailQueryHandler(_dbContext, _currentUserService, _dateTimeProvider);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotStaff_ReturnsForbiddenAndWritesNoAuditRow()
    {
        _currentUserService.Role = "Administrator";
        var payout = await SeedPayoutAsync();

        var result = await _handler.Handle(new GetPayoutDetailQuery(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenPayoutDoesNotExist_ReturnsNotFoundAndWritesNoAuditRow()
    {
        var result = await _handler.Handle(new GetPayoutDetailQuery(999), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotFound);
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenPayoutExists_ReturnsDetailAndWritesAuditRow()
    {
        var payout = await SeedPayoutAsync();

        var result = await _handler.Handle(new GetPayoutDetailQuery(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PayoutId.Should().Be(payout.Id.ToString());
        result.Value.Status.Should().Be(Payout.StatusPendingReview);
        result.Value.GrossRevenue.Should().Be(12_000_000m);
        result.Value.CommissionAmount.Should().Be(1_200_000m);
        result.Value.NetPayoutAmount.Should().Be(10_800_000m);
        result.Value.CommissionRate.Should().Be(0.1m);
        result.Value.ContributingBookings.Should().BeEmpty("no Booking entity exists yet; this is a known gap, not a bug");

        var auditRow = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        auditRow.ActorUserId.Should().Be(1);
        auditRow.AffectedEntityId.Should().Be(payout.Id);
        auditRow.CreatedAtUtc.Should().Be(NowUtc);
    }

    [Fact]
    public async Task Handle_WhenBeneficiarySnapshotIsNull_ReturnsNullMaskedAccountNumber()
    {
        var payout = await SeedPayoutAsync();

        var result = await _handler.Handle(new GetPayoutDetailQuery(payout.Id), CancellationToken.None);

        result.Value.Beneficiary.MaskedAccountNumber.Should().BeNull();
        result.Value.Beneficiary.BankName.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenBeneficiarySnapshotIsSet_MasksAllButLast4Digits()
    {
        var payout = await SeedPayoutAsync(bankName: "Vietcombank", accountHolder: "NGUYEN VAN A", last4: "1234");

        var result = await _handler.Handle(new GetPayoutDetailQuery(payout.Id), CancellationToken.None);

        result.Value.Beneficiary.BankName.Should().Be("Vietcombank");
        result.Value.Beneficiary.AccountHolder.Should().Be("NGUYEN VAN A");
        result.Value.Beneficiary.MaskedAccountNumber.Should().Be("XXXXXXXX1234");
    }

    [Fact]
    public async Task Handle_WhenGrossRevenueIsZero_ReturnsZeroCommissionRateInsteadOfDividingByZero()
    {
        var payout = await SeedPayoutAsync(grossRevenue: 0m, commissionAmount: 0m);

        var result = await _handler.Handle(new GetPayoutDetailQuery(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CommissionRate.Should().Be(0m);
    }

    private async Task<Payout> SeedPayoutAsync(
        decimal grossRevenue = 12_000_000m,
        decimal commissionAmount = 1_200_000m,
        string? bankName = null,
        string? accountHolder = null,
        string? last4 = null)
    {
        var user = new User
        {
            FullName = "Hoi An Tours",
            Email = "operator@example.com",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        _dbContext.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            User = user,
            CompanyName = "Hoi An Tours",
            TaxCode = "TAX-001",
            BusinessLicenseNo = "LIC-001",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var payout = Payout.CreatePendingReview(
            user.Id,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 30),
            grossRevenue,
            commissionAmount,
            RequestedAtUtc,
            beneficiaryBankName: bankName,
            beneficiaryAccountHolder: accountHolder,
            beneficiaryAccountLast4: last4);

        _dbContext.Payouts.Add(payout);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return payout;
    }
}