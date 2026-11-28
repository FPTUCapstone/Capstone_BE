using FluentAssertions;

using TripMate.Application.Features.Complaints.StaffReview;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;

namespace TripMate.Application.UnitTests.Features.Traveler.PostTripComplaints.Domain;

public sealed class StaffComplaintEvidenceValidationTests
{
    [Fact]
    public void EmptyOrForgedEvidence_IsRejected()
    {
        StaffComplaintEvidenceRules.Validate([]).IsFailure.Should().BeTrue();
        StaffComplaintEvidenceRules.Validate([new("bad.pdf", "application/pdf", [1, 2, 3])]).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void MoreThanThreeFiles_IsRejected()
    {
        var file = new ComplaintEvidenceUpload("file.pdf", "application/pdf", "%PDF-test"u8.ToArray());
        StaffComplaintEvidenceRules.Validate([file, file, file, file]).IsFailure.Should().BeTrue();
        StaffComplaintEvidenceRules.Validate([file]).IsSuccess.Should().BeTrue();
    }
}