using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.Create;

public sealed record CreateCouponCommand(string Code, VoucherDiscountType DiscountType, decimal DiscountValue,
    decimal? MaxDiscountAmount, decimal MinOrderAmount, int? UsageLimit, int? UsageLimitPerUser,
    DateTimeOffset ValidFromUtc, DateTimeOffset ValidToUtc, IReadOnlyCollection<long>? ApplicableTourIds)
    : IRequest<Result<CreateCouponResponse>>;

public sealed record CreateCouponResponse(long CouponId, string Code);