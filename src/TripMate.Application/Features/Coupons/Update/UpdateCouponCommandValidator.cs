using FluentValidation;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.Update;

public sealed class UpdateCouponCommandValidator : AbstractValidator<UpdateCouponCommand>
{
    public UpdateCouponCommandValidator(IDateTimeProvider dateTimeProvider)
    {
        RuleFor(command => command.CouponId).GreaterThan(0);
        RuleFor(command => command.Name).NotEmpty().Must(name => name.Trim().Length <= Voucher.NameMaxLength);
        RuleFor(command => command.DiscountType).IsInEnum();
        RuleFor(command => command.DiscountValue).GreaterThan(0m).Must(IsSupportedMoneyAmount);
        RuleFor(command => command.MinOrderAmount).GreaterThanOrEqualTo(0m).Must(IsSupportedMoneyAmount);
        RuleFor(command => command.MaxDiscountAmount).Must(value => value is null || IsSupportedMoneyAmount(value.Value));
        RuleFor(command => command.ValidToUtc).GreaterThan(command => command.ValidFromUtc);
        RuleFor(command => command.ValidToUtc).GreaterThan(dateTimeProvider.UtcNow)
            .WithMessage("The coupon end time must be in the future.");
        RuleFor(command => command.UsageLimit).GreaterThan(0).When(command => command.UsageLimit.HasValue);
        RuleFor(command => command.UsageLimitPerUser).GreaterThan(0).When(command => command.UsageLimitPerUser.HasValue);
        RuleFor(command => command.ApplicableTourIds).NotNull().NotEmpty()
            .Must(ids => ids is not null && ids.Distinct().Count() == ids.Count);
        RuleForEach(command => command.ApplicableTourIds).GreaterThan(0);
        When(command => command.DiscountType == VoucherDiscountType.Percentage, () =>
        {
            RuleFor(command => command.DiscountValue).InclusiveBetween(1m, 100m);
            RuleFor(command => command.MaxDiscountAmount).NotNull().GreaterThan(0m);
        });
        When(command => command.DiscountType == VoucherDiscountType.Flat, () =>
        {
            RuleFor(command => command.MaxDiscountAmount).Null();
            RuleFor(command => command).Must(command => command.MinOrderAmount <= 0m ||
                    command.DiscountValue <= command.MinOrderAmount)
                .WithMessage("A flat discount cannot exceed the minimum order amount.");
        });
    }

    private static bool IsSupportedMoneyAmount(decimal value) =>
        value >= 0m && value <= Voucher.MaximumMoneyAmount && value == decimal.Round(value, 2);
}