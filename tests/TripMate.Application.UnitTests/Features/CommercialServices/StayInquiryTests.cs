using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.CommercialServices;

public sealed class StayInquiryTests
{
    [Fact]
    public void Reply_changes_contact_status_only_and_cannot_be_overwritten()
    {
        var inquiry = new StayInquiry(12, 34, null, new DateOnly(2026, 11, 28), new DateOnly(2026, 11, 30),
            2, "Mai", "mai@example.com", null, Guid.NewGuid(), new string('A', 64), DateTimeOffset.UtcNow);

        Assert.Equal(StayInquiry.Sent, inquiry.Status);
        Assert.True(inquiry.TryReply(false, "Vui lòng gọi cho cơ sở."));
        Assert.Equal(StayInquiry.Replied, inquiry.Status);
        Assert.False(inquiry.TryReply(true, "Khác"));
        Assert.Equal("Vui lòng gọi cho cơ sở.", inquiry.Reply);
    }
}
