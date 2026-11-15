using TripMate.Application.Features.ServiceProviderRequests;

namespace TripMate.Application.UnitTests.Features.ServiceProviderRequests;

public sealed class ProviderRequestValidationTests
{
    [Fact]
    public void Decision_requires_rejection_reason_and_valid_version_and_key()
    {
        var validator = new DecideServiceProviderRequestValidator();
        Assert.False(validator.Validate(new DecideServiceProviderRequest(1, "Rejected", " ",
            "AAAAAAAAAAA=", Guid.NewGuid())).IsValid);
        Assert.True(validator.Validate(new DecideServiceProviderRequest(1, "Rejected", "Unavailable",
            "AAAAAAAAAAA=", Guid.NewGuid())).IsValid);
        Assert.False(validator.Validate(new DecideServiceProviderRequest(1, "Confirmed", null,
            "broken", Guid.NewGuid())).IsValid);
    }

    [Fact]
    public void List_rejects_reversed_dates_and_unbounded_pages()
    {
        var validator = new ListServiceProviderRequestsValidator();
        Assert.False(validator.Validate(new ListServiceProviderRequests(PageSize: 101)).IsValid);
        Assert.False(validator.Validate(new ListServiceProviderRequests(
            RequestedDateFrom: new DateOnly(2026, 11, 15),
            RequestedDateTo: new DateOnly(2026, 11, 14))).IsValid);
    }

    [Fact]
    public void Completed_requires_positive_amount_and_no_show_rejects_charge()
    {
        var validator = new RecordServiceProviderOutcomeValidator();
        Assert.False(validator.Validate(new RecordServiceProviderOutcome(1, "Completed", 0,
            "AAAAAAAAAAA=", Guid.NewGuid())).IsValid);
        Assert.False(validator.Validate(new RecordServiceProviderOutcome(1, "NoShow", 12,
            "AAAAAAAAAAA=", Guid.NewGuid())).IsValid);
        Assert.True(validator.Validate(new RecordServiceProviderOutcome(1, "Completed", 123456.78m,
            "AAAAAAAAAAA=", Guid.NewGuid())).IsValid);
    }
}