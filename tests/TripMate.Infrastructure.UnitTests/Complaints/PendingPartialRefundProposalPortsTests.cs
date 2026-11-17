using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Complaints;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Complaints;

public sealed class PostTripComplaintProposalStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EmptyStore_FindsNothingAndClosesNothing()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostTripComplaintProposalStore(dbContext);

        (await store.FindAsync(1, CancellationToken.None)).Should().BeNull();
        (await store.TryCloseAsync(1, PostTripComplaintStatus.Declined, Now, CancellationToken.None)).Should().BeFalse();
        (await store.ListOverdueAsync(Now, 100, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task RefundRequestCreator_WithoutVerifiedPayment_RefusesToCreateRefund()
    {
        await using var dbContext = CreateDbContext();
        var creator = new PostTripComplaintRefundRequestCreator(dbContext);

        var act = () => creator.CreateForAcceptedComplaintAsync(1, 2, 500_000m, Now, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No verified payment transaction*");
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

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"post-trip-complaint-proposal-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }
}