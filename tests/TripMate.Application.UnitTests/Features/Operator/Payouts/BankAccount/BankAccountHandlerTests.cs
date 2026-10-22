using FluentAssertions;

using TripMate.Application.Features.Operator.Payouts.BankAccount;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Application.UnitTests.Features.Operator.Payouts.RequestPayout;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts.BankAccount;

public sealed class BankAccountHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_WithNoBankAccountSet_ReturnsNullFields()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db, withBankAccount: false);

        var handler = new GetBeneficiaryBankAccountQueryHandler(db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id));
        var result = await handler.Handle(new GetBeneficiaryBankAccountQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BankName.Should().BeNull();
        result.Value.MaskedAccountNumber.Should().BeNull();
    }

    [Fact]
    public async Task Get_AfterUpsert_ReturnsMaskedAccountNumberOnly()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db, withBankAccount: false);

        var upsertHandler = new UpsertBeneficiaryBankAccountCommandHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id),
            new RequestPayoutCommandHandlerTests.FixedClock(Now), new FakePiiProtector());
        var upserted = await upsertHandler.Handle(
            new UpsertBeneficiaryBankAccountCommand("Vietcombank", "Nguyen Van A", "1029384756"), CancellationToken.None);

        upserted.IsSuccess.Should().BeTrue();
        upserted.Value.MaskedAccountNumber.Should().Be("******4756");
        upserted.Value.MaskedAccountNumber.Should().NotContain("1029384756");

        var getHandler = new GetBeneficiaryBankAccountQueryHandler(db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id));
        var result = await getHandler.Handle(new GetBeneficiaryBankAccountQuery(), CancellationToken.None);

        result.Value.BankName.Should().Be("Vietcombank");
        result.Value.MaskedAccountNumber.Should().Be("******4756");
    }

    [Theory]
    [InlineData("", "Holder", "123")]
    [InlineData("Bank", "", "123")]
    [InlineData("Bank", "Holder", "")]
    public async Task Upsert_WithMissingRequiredField_ReturnsValidationRequiredField(
        string bankName, string accountHolderName, string accountNumber)
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db, withBankAccount: false);

        var handler = new UpsertBeneficiaryBankAccountCommandHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id),
            new RequestPayoutCommandHandlerTests.FixedClock(Now), new FakePiiProtector());

        var result = await handler.Handle(
            new UpsertBeneficiaryBankAccountCommand(bankName, accountHolderName, accountNumber), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorPayoutErrorCodes.ValidationRequiredField);
    }
}