using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Complaints;

namespace TripMate.Infrastructure.UnitTests.Complaints;

public sealed class PendingPartialRefundProposalPortsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Store_BeforeTm222_FindsNothingAndClosesNothing()
    {
        var store = new PendingPartialRefundProposalStore();

        (await store.FindAsync(1, CancellationToken.None)).Should().BeNull();
        (await store.TryCloseAsync(1, PostTripComplaintStatus.Declined, Now, CancellationToken.None)).Should().BeFalse();
        (await store.ListOverdueAsync(Now, 100, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task RefundRequestCreator_BeforeTm88_NeverFallsBackToDirectRefund()
    {
        var creator = new PendingComplaintRefundRequestCreator();

        var act = () => creator.CreateForAcceptedComplaintAsync(1, 2, 500_000m, Now, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*TM-88*");
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(60, 0)]
    public void OptionsValidator_RejectsNonPositiveValues(int sweepMinutes, int batchSize)
    {
        var result = new PartialRefundProposalDeadlineOptionsValidator().Validate(null,
            new PartialRefundProposalDeadlineOptions
            {
                SweepInterval = TimeSpan.FromMinutes(sweepMinutes),
                BatchSize = batchSize,
            });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void OptionsValidator_DefaultsAreHourlyAndValid()
    {
        var options = new PartialRefundProposalDeadlineOptions();

        options.SweepInterval.Should().Be(TimeSpan.FromHours(1));
        new PartialRefundProposalDeadlineOptionsValidator().Validate(null, options)
            .Should().Be(ValidateOptionsResult.Success);
    }
}