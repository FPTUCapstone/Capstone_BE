using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Admin.PayoutSettlements.Processing;
using TripMate.Infrastructure.Services.Payment.PayoutDisbursement;

namespace TripMate.Infrastructure.UnitTests.Payment;

public sealed class PayoutDisbursementConfigurationTests
{
    [Fact]
    public void NoConfiguration_SelectsTheMockGatewayAndUsesTheSafeDefaults()
    {
        using var provider = Build();

        provider.GetRequiredService<IOptions<PayoutDisbursementOptions>>().Value.Provider.Should().Be("Mock");
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPayoutDisbursementGateway>()
            .Should().BeOfType<MockDisbursementGateway>();
        var worker = provider.GetRequiredService<IOptions<PayoutSettlementOptions>>().Value;
        worker.MaxSubmitAttempts.Should().Be(3);
        worker.PollDeadline.Should().Be(TimeSpan.FromHours(24));
        worker.SweepInterval.Should().BePositive();
        worker.EffectivePollBackoff.Should().BeInAscendingOrder().And.HaveCountGreaterThan(3);
    }

    [Fact]
    public void ExplicitMockProvider_IsAccepted()
    {
        using var provider = Build(("Payment:Payout:Provider", "mock"));

        provider.GetRequiredService<IOptions<PayoutDisbursementOptions>>().Value.Should().NotBeNull();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPayoutDisbursementGateway>()
            .Should().BeOfType<MockDisbursementGateway>();
    }

    [Fact]
    public void PayOsProvider_WithoutTheLiveTransfersSwitch_FailsValidation()
    {
        using var provider = Build(
            ("Payment:Payout:Provider", "PayOs"),
            ("Payment:Payout:PayOs:ClientId", "client"),
            ("Payment:Payout:PayOs:ApiKey", "key"),
            ("Payment:Payout:PayOs:ChecksumKey", "checksum"));

        var act = () => provider.GetRequiredService<IOptions<PayoutDisbursementOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*LiveTransfersEnabled*");
    }

    [Theory]
    [InlineData("ClientId")]
    [InlineData("ApiKey")]
    [InlineData("ChecksumKey")]
    public void PayOsProvider_MissingAnyPayoutCredential_FailsValidationWithoutNamingTheValue(string missing)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Payment:Payout:Provider"] = "PayOs",
            ["Payment:Payout:PayOs:LiveTransfersEnabled"] = "true",
            ["Payment:Payout:PayOs:ClientId"] = "client-secret-value",
            ["Payment:Payout:PayOs:ApiKey"] = "key-secret-value",
            ["Payment:Payout:PayOs:ChecksumKey"] = "checksum-secret-value",
        };
        settings.Remove($"Payment:Payout:PayOs:{missing}");
        using var provider = Build(settings.Select(pair => (pair.Key, pair.Value)).ToArray());

        var act = () => provider.GetRequiredService<IOptions<PayoutDisbursementOptions>>().Value;

        var exception = act.Should().Throw<OptionsValidationException>().Which;
        exception.Message.Should().Contain(missing);
        exception.Message.Should().NotContain("secret-value");
    }

    [Fact]
    public void CollectionCredentialsAlone_NeverEnablePayouts()
    {
        using var provider = Build(
            ("Payment:Provider", "PayOS"),
            ("Payment:PayOs:ClientId", "collection-client"),
            ("Payment:PayOs:ApiKey", "collection-key"),
            ("Payment:PayOs:ChecksumKey", "collection-checksum"),
            ("Payment:Payout:Provider", "PayOs"),
            ("Payment:Payout:PayOs:LiveTransfersEnabled", "true"));

        var act = () => provider.GetRequiredService<IOptions<PayoutDisbursementOptions>>().Value;

        act.Should().Throw<OptionsValidationException>("payout credentials never fall back to the collection channel");
    }

    [Fact]
    public void PayOsProvider_WithTheSwitchAndAllCredentials_IsAccepted()
    {
        using var provider = Build(
            ("Payment:Payout:Provider", "PayOs"),
            ("Payment:Payout:PayOs:LiveTransfersEnabled", "true"),
            ("Payment:Payout:PayOs:ClientId", "client"),
            ("Payment:Payout:PayOs:ApiKey", "key"),
            ("Payment:Payout:PayOs:ChecksumKey", "checksum"));

        var options = provider.GetRequiredService<IOptions<PayoutDisbursementOptions>>().Value;

        options.PayOs.BaseUrl.Should().Be("https://api-merchant.payos.vn");
        options.PayOs.LiveTransfersEnabled.Should().BeTrue();
    }

    [Fact]
    public void PayOsProvider_WithTheSwitchAndAllCredentials_SelectsThePayOsPayoutGateway()
    {
        using var provider = Build(
            ("Payment:Payout:Provider", "PayOs"),
            ("Payment:Payout:PayOs:LiveTransfersEnabled", "true"),
            ("Payment:Payout:PayOs:ClientId", "client"),
            ("Payment:Payout:PayOs:ApiKey", "key"),
            ("Payment:Payout:PayOs:ChecksumKey", "checksum"));

        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPayoutDisbursementGateway>()
            .Should().BeOfType<PayOsPayoutGateway>();
    }

    [Theory]
    [InlineData("VNPay")]
    [InlineData("")]
    [InlineData("Stripe")]
    public void UnknownProvider_FailsValidation(string providerName)
    {
        using var provider = Build(("Payment:Payout:Provider", providerName));

        var act = () => provider.GetRequiredService<IOptions<PayoutDisbursementOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*Provider*");
    }

    [Theory]
    [InlineData("PayoutSettlement:SweepInterval", "00:00:00")]
    [InlineData("PayoutSettlement:BatchSize", "0")]
    [InlineData("PayoutSettlement:ProcessingLeaseDuration", "00:00:00")]
    [InlineData("PayoutSettlement:MaxSubmitAttempts", "0")]
    [InlineData("PayoutSettlement:PollDeadline", "00:00:00")]
    public void WorkerOptions_MustBePositive(string key, string value)
    {
        using var provider = Build((key, value));

        var act = () => provider.GetRequiredService<IOptions<PayoutSettlementOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void WorkerBackoff_MustBePositiveAndNotDecreasing()
    {
        var decreasing = Build(
            ("PayoutSettlement:PollBackoff:0", "00:05:00"),
            ("PayoutSettlement:PollBackoff:1", "00:01:00"));
        var act = () => decreasing.GetRequiredService<IOptions<PayoutSettlementOptions>>().Value;
        act.Should().Throw<OptionsValidationException>();
        decreasing.Dispose();
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(item => item.Key, item => item.Value))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPayoutDisbursement(configuration);
        return services.BuildServiceProvider();
    }
}
