namespace TripMate.Application.Features.Traveler.Payments.Common;

public sealed class PaymentOptions
{
    public const string SectionName = "Payment";

    public string Provider { get; set; } = "Mock";

    public PayOsOptions PayOs { get; set; } = new();

    public MockPaymentOptions Mock { get; set; } = new();
}

public sealed class PayOsOptions
{
    public string ClientId { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ChecksumKey { get; set; } = string.Empty;

    public string ReturnUrl { get; set; } = "http://localhost:3001/checkout/result";

    public string CancelUrl { get; set; } = "http://localhost:3001/checkout/result";
}

public sealed class MockPaymentOptions
{
    public string CheckoutBaseUrl { get; set; } = "http://localhost:3001/checkout/mock";

    public int ExpiryMinutes { get; set; } = 15;
}