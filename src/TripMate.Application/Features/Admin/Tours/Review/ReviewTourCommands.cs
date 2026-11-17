using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.Tours.Review;

public sealed record TourReviewChecklistDto(
    bool ContentCompleteness,
    bool ImageAppropriateness,
    bool PricePlausibility,
    bool PolicyCompliance,
    bool ItineraryFeasibility);

public sealed record ApproveTourCommand(
    long TourId,
    TourReviewChecklistDto? Checklist = null,
    string? Note = null) : IRequest<Result<TourReviewResponse>>;

public sealed record RejectTourCommand(long TourId, string Reason) : IRequest<Result<TourReviewResponse>>;

public sealed record PublishTourCommand(long TourId) : IRequest<Result<TourReviewResponse>>;

public sealed record UnpublishTourCommand(long TourId) : IRequest<Result<TourReviewResponse>>;

public sealed record TourReviewResponse(long TourId, string Status, DateTimeOffset ChangedAtUtc);