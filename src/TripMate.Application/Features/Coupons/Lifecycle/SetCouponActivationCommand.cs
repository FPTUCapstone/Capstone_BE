using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;

namespace TripMate.Application.Features.Coupons.Lifecycle;

public sealed record SetCouponActivationCommand(long CouponId, bool Activate)
    : IRequest<Result<OperatorCouponResponse>>;