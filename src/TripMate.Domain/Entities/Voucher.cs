using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class Voucher : BaseEntity
{
    public const int CodeMaxLength = 30;
    public const int NameMaxLength = 120;
    public const decimal MaximumMoneyAmount = 9_999_999_999.99m;
    private readonly List<VoucherApplicableTour> _applicableTours = [];

    private Voucher() { }

    // Null is reserved by the existing schema for a future platform-issued voucher.
    // UC-38 always supplies an active Tour Operator owner.
    public long? OwnerOperatorUserId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
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
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public IReadOnlyCollection<VoucherApplicableTour> ApplicableTours => _applicableTours.AsReadOnly();

    public static Voucher Create(long ownerOperatorUserId, string code, VoucherDiscountType discountType,
        decimal discountValue, decimal? maxDiscountAmount, decimal minOrderAmount, int? usageLimit,
        int? usageLimitPerUser, DateTimeOffset validFromUtc, DateTimeOffset validToUtc,
        IReadOnlyCollection<Tour> applicableTours, DateTimeOffset createdAtUtc)
        => Create(ownerOperatorUserId, code, code, discountType, discountValue, maxDiscountAmount,
            minOrderAmount, usageLimit, usageLimitPerUser, validFromUtc, validToUtc, applicableTours, createdAtUtc);

    public static Voucher Create(long ownerOperatorUserId, string code, string name, VoucherDiscountType discountType,
        decimal discountValue, decimal? maxDiscountAmount, decimal minOrderAmount, int? usageLimit,
        int? usageLimitPerUser, DateTimeOffset validFromUtc, DateTimeOffset validToUtc,
        IReadOnlyCollection<Tour> applicableTours, DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerOperatorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(applicableTours);
        var normalizedCode = code.Trim().ToUpperInvariant();
        var normalizedName = name.Trim();
        if (normalizedCode.Length is < 3 or > CodeMaxLength || !normalizedCode.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-'))
            throw new ArgumentException("Voucher code format is invalid.", nameof(code));
        if (normalizedName.Length is < 1 or > NameMaxLength)
            throw new ArgumentException("Voucher name is invalid.", nameof(name));
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
            Name = normalizedName,
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
            UpdatedAtUtc = createdAtUtc,
        };
        foreach (var tour in applicableTours) voucher._applicableTours.Add(new VoucherApplicableTour(voucher, tour));
        return voucher;
    }

    public void Update(string name, VoucherDiscountType discountType, decimal discountValue,
        decimal? maxDiscountAmount, decimal minOrderAmount, int? usageLimit, int? usageLimitPerUser,
        DateTimeOffset validFromUtc, DateTimeOffset validToUtc, IReadOnlyCollection<Tour> applicableTours,
        DateTimeOffset updatedAtUtc)
    {
        if (discountType != DiscountType)
            throw new ArgumentException("Voucher discount type cannot change.", nameof(discountType));
        if (ValidFromUtc <= updatedAtUtc && validFromUtc != ValidFromUtc)
            throw new ArgumentException("An elapsed voucher start cannot change.", nameof(validFromUtc));

        var wasExpired = ValidToUtc <= updatedAtUtc || Status == VoucherStatus.Expired;
        var replacement = Create(OwnerOperatorUserId ?? throw new InvalidOperationException("Voucher owner is required."),
            Code, name, discountType, discountValue, maxDiscountAmount, minOrderAmount, usageLimit,
            usageLimitPerUser, validFromUtc, validToUtc, applicableTours, updatedAtUtc);
        if (usageLimit.HasValue && usageLimit.Value < UsedCount)
            throw new ArgumentOutOfRangeException(nameof(usageLimit));

        Name = replacement.Name;
        DiscountValue = replacement.DiscountValue;
        MaxDiscountAmount = replacement.MaxDiscountAmount;
        MinOrderAmount = replacement.MinOrderAmount;
        UsageLimit = replacement.UsageLimit;
        UsageLimitPerUser = replacement.UsageLimitPerUser;
        if (ValidFromUtc > updatedAtUtc)
            ValidFromUtc = replacement.ValidFromUtc;
        ValidToUtc = replacement.ValidToUtc;
        if (wasExpired)
            Status = VoucherStatus.Disabled;
        var selectedIds = applicableTours.Select(tour => tour.Id).ToHashSet();
        _applicableTours.RemoveAll(link => !selectedIds.Contains(link.TourId > 0 ? link.TourId : link.Tour.Id));
        var existingIds = _applicableTours.Select(link => link.TourId > 0 ? link.TourId : link.Tour.Id).ToHashSet();
        foreach (var tour in applicableTours.Where(tour => !existingIds.Contains(tour.Id)))
            _applicableTours.Add(new VoucherApplicableTour(this, tour));
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Deactivate(DateTimeOffset updatedAtUtc)
    {
        Status = VoucherStatus.Disabled;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Activate(DateTimeOffset updatedAtUtc)
    {
        if (ValidToUtc <= updatedAtUtc)
            throw new InvalidOperationException("Expired vouchers cannot be activated.");
        Status = VoucherStatus.Active;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Records one redemption against the global usage_limit (UC-27). Caller is
    /// responsible for the per-user limit, which this entity cannot see on its own.</summary>
    public void IncrementUsage()
    {
        if (UsageLimit.HasValue && UsedCount >= UsageLimit.Value)
            throw new InvalidOperationException("This voucher has reached its usage limit.");
        UsedCount++;
    }

    public void ReleaseUsage()
    {
        if (UsedCount <= 0)
            throw new InvalidOperationException("This voucher has no occupied usage to release.");
        UsedCount--;
    }

    private static bool IsSupportedMoneyAmount(decimal value) =>
        value >= 0m && value <= MaximumMoneyAmount && value == decimal.Round(value, 2);
}