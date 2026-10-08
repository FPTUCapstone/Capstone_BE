using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class Voucher : BaseEntity
{
    public const int CodeMaxLength = 30;
    public const decimal MaximumMoneyAmount = 9_999_999_999.99m;
    private readonly List<VoucherApplicableTour> _applicableTours = [];

    private Voucher() { }

    // Null is reserved by the existing schema for a future platform-issued voucher.
    // UC-38 always supplies an active Tour Operator owner.
    public long? OwnerOperatorUserId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public VoucherDiscountType DiscountType { get; private set; }
    public decimal DiscountValue { get; private set; }
    public decimal? MaxDiscountAmount { get; private set; }
    public decimal MinOrderAmount { get; private set; }
    public int? UsageLimit { get; private set; }
    public int? UsageLimitPerUser { get; private set; }
    public int UsedCount { get; private set; }
    public DateTimeOffset ValidFromUtc { get; private set; }
    public DateTimeOffset ValidToUtc { get; private set; }
    public VoucherStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public IReadOnlyCollection<VoucherApplicableTour> ApplicableTours => _applicableTours.AsReadOnly();

    public static Voucher Create(long ownerOperatorUserId, string code, VoucherDiscountType discountType,
        decimal discountValue, decimal? maxDiscountAmount, decimal minOrderAmount, int? usageLimit,
        int? usageLimitPerUser, DateTimeOffset validFromUtc, DateTimeOffset validToUtc,
        IReadOnlyCollection<Tour> applicableTours, DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerOperatorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(applicableTours);
        var normalizedCode = code.Trim().ToUpperInvariant();
        if (normalizedCode.Length is < 3 or > CodeMaxLength || !normalizedCode.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-'))
            throw new ArgumentException("Voucher code format is invalid.", nameof(code));
        if (!Enum.IsDefined(discountType))
            throw new ArgumentOutOfRangeException(nameof(discountType));
        if (applicableTours.Select(tour => tour.Id).Distinct().Count() != applicableTours.Count)
            throw new ArgumentException("Applicable tours must be distinct.", nameof(applicableTours));
        if (validToUtc <= validFromUtc || validToUtc <= createdAtUtc || !IsSupportedMoneyAmount(discountValue) || minOrderAmount < 0
            || !IsSupportedMoneyAmount(minOrderAmount) || (maxDiscountAmount.HasValue && !IsSupportedMoneyAmount(maxDiscountAmount.Value))
            || usageLimit is <= 0 || usageLimitPerUser is <= 0 || applicableTours.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(validToUtc));
        if (discountType == VoucherDiscountType.Percentage && (discountValue > 100 || maxDiscountAmount is null or <= 0))
            throw new ArgumentOutOfRangeException(nameof(maxDiscountAmount));
        if (discountType == VoucherDiscountType.Flat && maxDiscountAmount is not null)
            throw new ArgumentException("Flat vouchers cannot have a discount cap.", nameof(maxDiscountAmount));
        if (discountType == VoucherDiscountType.Flat && minOrderAmount > 0m && discountValue > minOrderAmount)
            throw new ArgumentOutOfRangeException(nameof(discountValue));

        var voucher = new Voucher
        {
            OwnerOperatorUserId = ownerOperatorUserId,
            Code = normalizedCode,
            DiscountType = discountType,
            DiscountValue = discountValue,
            MaxDiscountAmount = maxDiscountAmount,
            MinOrderAmount = minOrderAmount,
            UsageLimit = usageLimit,
            UsageLimitPerUser = usageLimitPerUser,
            ValidFromUtc = validFromUtc,
            ValidToUtc = validToUtc,
            Status = VoucherStatus.Active,
            CreatedAtUtc = createdAtUtc,
        };
        foreach (var tour in applicableTours) voucher._applicableTours.Add(new VoucherApplicableTour(voucher, tour));
        return voucher;
    }

    private static bool IsSupportedMoneyAmount(decimal value) =>
        value >= 0m && value <= MaximumMoneyAmount && value == decimal.Round(value, 2);
}