using FluentAssertions;

using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Edit;
using TripMate.Application.Features.TripReviews.Submit;

namespace TripMate.Application.UnitTests.Features.TripReviews;

public sealed class TripReviewModerationPolicyTests
{
    [Fact]
    public void Contract_ExposesTheSingleActivePolicyVersion()
    {
        var moderator = new RecordingModerator();

        ReviewContentPolicy.ActiveVersion.Should().Be("tm79-review-text-v1");
        moderator.ActivePolicyVersion.Should().Be("test-only-v1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contract_ScreensExactlyNormalizedCreateAndEditText(bool edit)
    {
        ReviewText text;
        if (edit)
        {
            var command = new EditTripReviewCommand(1, 1, "  Không tốt  ", "  Dịch vụ cần cải thiện.\n ", false, "AQIDBAUGBwg=");
            new EditTripReviewCommandValidator().Validate(command).IsValid.Should().BeTrue();
            text = command.Text;
        }
        else
        {
            var command = new SubmitTripReviewCommand(1, 1, "  Không tốt  ", "  Dịch vụ cần cải thiện.\n ", [], []);
            new SubmitTripReviewCommandValidator().Validate(command).IsValid.Should().BeTrue();
            text = command.Text;
        }
        var moderator = new RecordingModerator();
        var result = await moderator.ScreenAsync(text, CancellationToken.None);
        moderator.Seen.Should().BeSameAs(text);
        text.Title.Should().Be("Không tốt");
        text.Content.Should().Be("Dịch vụ cần cải thiện.");
        result.Decision.Should().Be(ReviewModerationDecision.Accepted);
        result.PolicyVersion.Should().Be("test-only-v1");
    }

    [Fact]
    public void Results_KeepRejectedAndUnavailableDistinctWithoutPolicyApproval()
    {
        var rejected = ReviewContentModerationResult.Rejected(
            ReviewPolicyCategory.ThreatOrCallForPhysicalHarm);
        var unavailable = ReviewContentModerationResult.Unavailable();
        rejected.Decision.Should().Be(ReviewModerationDecision.Rejected);
        unavailable.Decision.Should().Be(ReviewModerationDecision.Unavailable);
        rejected.PolicyVersion.Should().BeNull();
        unavailable.PolicyVersion.Should().BeNull();
        rejected.Categories.Should().Equal(
            ReviewPolicyCategory.ThreatOrCallForPhysicalHarm);
        unavailable.Categories.Should().BeEmpty();
    }

    [Fact]
    public void Rejection_RequiresStableCategories_AndDoesNotExposeMatchedText()
    {
        var rejected = ReviewContentModerationResult.Rejected(
            ReviewPolicyCategory.UnrelatedAdvertisingSpamPhishingScamOrFraudulentSolicitation,
            ReviewPolicyCategory.TargetedDegradingHarassment,
            ReviewPolicyCategory.TargetedDegradingHarassment);

        rejected.Categories.Should().Equal(
            ReviewPolicyCategory.TargetedDegradingHarassment,
            ReviewPolicyCategory.UnrelatedAdvertisingSpamPhishingScamOrFraudulentSolicitation);
        rejected.GetType().GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name =>
                name.Contains("match", StringComparison.OrdinalIgnoreCase)
                || name.Contains("text", StringComparison.OrdinalIgnoreCase)
                || name.Contains("phrase", StringComparison.OrdinalIgnoreCase)
                || name.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejection_RequiresAtLeastOneCategory()
    {
        var act = () => ReviewContentModerationResult.Rejected();

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Acceptance_RequiresAnIdentifiedPolicyVersion(string? version)
    {
        var act = () => ReviewContentModerationResult.Accepted(version!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Contract_PropagatesCancellation_NotUnavailableOrAccepted()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var moderator = new RecordingModerator();
        var act = () => moderator.ScreenAsync(ReviewText.Normalize("Title", "Content"), cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        moderator.Seen.Should().BeNull();
    }

    // Contract double only; not production moderation or evidence that negative reviews
    // pass an approved corpus. Auth/duplicate/write ordering is tested in Tasks 9/10.
    private sealed class RecordingModerator : IReviewContentModerator
    {
        public string ActivePolicyVersion => "test-only-v1";

        public ReviewText? Seen { get; private set; }
        public Task<ReviewContentModerationResult> ScreenAsync(ReviewText text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Seen = text;
            return Task.FromResult(ReviewContentModerationResult.Accepted("test-only-v1"));
        }
    }
}