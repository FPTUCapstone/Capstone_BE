using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Coupons.GetEligibleTours;

public sealed record GetEligibleCouponToursQuery
    : IRequest<Result<IReadOnlyList<EligibleCouponTourResponse>>>;

public sealed record EligibleCouponTourResponse(
    long TourId,
    string Title,
    string? Destination,
    decimal BasePrice);