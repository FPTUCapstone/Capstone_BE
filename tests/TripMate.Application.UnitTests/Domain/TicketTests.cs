using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public class TicketTests
{
    [Theory]
    [InlineData(456L, "TK-0000000456")]
    [InlineData(1L, "TK-0000000001")]
    [InlineData(12345678901L, "TK-12345678901")]
    public void CodeFor_PadsTheBookingIdToTenDigits(long bookingId, string expected)
    {
        Ticket.CodeFor(bookingId).Should().Be(expected);
    }

    [Fact]
    public void CodeFor_LargestBookingId_FitsTheColumn()
    {
        Ticket.CodeFor(long.MaxValue).Length.Should().BeLessThanOrEqualTo(Ticket.TicketCodeMaxLength);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void CodeFor_NonPositiveBookingId_Throws(long bookingId)
    {
        var action = () => Ticket.CodeFor(bookingId);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}
