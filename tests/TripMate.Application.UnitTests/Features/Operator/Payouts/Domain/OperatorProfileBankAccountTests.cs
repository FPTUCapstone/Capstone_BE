using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts.Domain;

public sealed class OperatorProfileBankAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HasBeneficiaryBankAccount_WhenNoBankSet_IsFalse()
    {
        var profile = new OperatorProfile();

        profile.HasBeneficiaryBankAccount.Should().BeFalse();
    }

    [Fact]
    public void UpdateBeneficiaryBankAccount_SetsMaskedAndProtectedValuesAndTimestamp()
    {
        var profile = new OperatorProfile();

        profile.UpdateBeneficiaryBankAccount(
            bankName: "Vietcombank",
            accountHolderName: "Nguyen Van A",
            accountNumberMasked: "****1234",
            accountNumberProtected: "protected:1029384756:xxxx",
            nowUtc: Now);

        profile.HasBeneficiaryBankAccount.Should().BeTrue();
        profile.BankName.Should().Be("Vietcombank");
        profile.AccountHolderName.Should().Be("Nguyen Van A");
        profile.AccountNumberMasked.Should().Be("****1234");
        profile.AccountNumberProtected.Should().Be("protected:1029384756:xxxx");
        profile.BankAccountUpdatedAtUtc.Should().Be(Now);
    }

    [Theory]
    [InlineData("", "Holder", "****1234")]
    [InlineData("Bank", "", "****1234")]
    [InlineData("Bank", "Holder", "")]
    public void UpdateBeneficiaryBankAccount_WithMissingRequiredField_Throws(
        string bankName, string accountHolderName, string accountNumberMasked)
    {
        var profile = new OperatorProfile();

        var act = () => profile.UpdateBeneficiaryBankAccount(
            bankName, accountHolderName, accountNumberMasked, "protected:value", Now);

        act.Should().Throw<ArgumentException>();
    }
}