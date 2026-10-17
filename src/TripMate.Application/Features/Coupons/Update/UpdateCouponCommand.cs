using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.Update;

public sealed record UpdateCouponCommand(long CouponId, string Name, VoucherDiscountType DiscountType,
    decimal DiscountValue, decimal? MaxDiscountAmount, decimal MinOrderAmount, int? UsageLimit,
    int? UsageLimitPerUser, DateTimeOffset ValidFromUtc, DateTimeOffset ValidToUtc,
    IReadOnlyCollection<long>? ApplicableTourIds) : IRequest<Result<OperatorCouponResponse>>;
