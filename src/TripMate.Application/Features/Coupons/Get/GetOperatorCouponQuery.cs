using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;

namespace TripMate.Application.Features.Coupons.Get;

public sealed record GetOperatorCouponQuery(long CouponId) : IRequest<Result<OperatorCouponResponse>>;
