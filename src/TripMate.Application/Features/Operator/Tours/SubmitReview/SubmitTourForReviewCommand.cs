using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.SubmitReview;

public sealed record SubmitTourForReviewCommand(long TourId, string? SubmissionNote = null)
    : IRequest<Result<SubmitTourForReviewResponse>>;

public sealed record SubmitTourForReviewResponse(long TourId, string Status, DateTimeOffset SubmittedAtUtc);