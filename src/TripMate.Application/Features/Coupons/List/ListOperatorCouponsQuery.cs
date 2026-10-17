using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;

namespace TripMate.Application.Features.Coupons.List;

public sealed record ListOperatorCouponsQuery : IRequest<Result<IReadOnlyList<OperatorCouponResponse>>>;