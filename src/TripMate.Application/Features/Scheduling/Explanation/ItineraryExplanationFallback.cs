using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Explanation;

/// <summary>Provider-independent, deterministic copy for itinerary explanations.</summary>
public static class ItineraryExplanationFallback
{
    private const int MaximumLength = 500;

    public const string MandatoryVisit = "Điểm đến bắt buộc theo yêu cầu của bạn.";
    public const string OptionalVisit = "Điểm gợi ý phù hợp với sở thích và khung giờ của bạn.";
    public const string RestWithPoi = "Điểm nghỉ ngơi được đề xuất trên lộ trình.";
    public const string RestWithoutPoi = "Thời gian nghỉ ngơi tự do giữa các điểm đến.";

    private static readonly string[] MandatoryWithCategory =
    [
        "{poi} là một điểm dừng quan trọng trong hành trình, đồng thời mang đến trải nghiệm nổi bật về {category}.",
        "{poi} được ưu tiên giữ lại trong lịch trình và là cơ hội để bạn khám phá thêm về {category}.",
        "Hành trình của bạn có {poi} như một điểm dừng đáng chú ý, giúp chuyến đi có thêm trải nghiệm về {category}.",
        "{poi} là một phần đáng chú ý của chuyến đi, mang đến thêm trải nghiệm thuộc nhóm {category}."
    ];

    private static readonly string[] MandatoryGeneric =
    [
        "{poi} là một điểm dừng quan trọng trong hành trình và được dành riêng thời gian để bạn khám phá thoải mái hơn.",
        "{poi} là một trong những điểm chính của chuyến đi, vì vậy lịch trình ưu tiên giữ lại điểm dừng này.",
        "{poi} được giữ lại như một điểm dừng quan trọng để hành trình của bạn diễn ra trọn vẹn hơn."
    ];

    private static readonly string[] SuggestedWithCategory =
    [
        "{poi} là một điểm dừng đáng khám phá thuộc nhóm {category}, phù hợp để bạn trải nghiệm thêm trong chuyến đi này.",
        "{poi} mang đến thêm một lựa chọn thú vị về {category}, giúp hành trình trở nên phong phú hơn.",
        "Nếu bạn muốn khám phá thêm về {category}, {poi} là một điểm dừng phù hợp để kết hợp vào lịch trình.",
        "{poi} là một gợi ý thú vị trong nhóm {category}, giúp chuyến đi có thêm trải nghiệm mới mà vẫn thuận tiện.",
        "{poi} có thể là một điểm ghé thú vị nếu bạn muốn dành thêm thời gian khám phá {category}."
    ];

    private static readonly string[] NearbyGeneral =
    [
        "{poi} nằm thuận tiện trên lộ trình và là một lựa chọn thú vị để chuyến đi thêm phong phú.",
        "{poi} là một điểm ghé phù hợp để kết hợp vào hành trình mà vẫn giữ nhịp chuyến đi thoải mái.",
        "{poi} được gợi ý như một điểm dừng thuận tiện, giúp bạn có thêm trải nghiệm trong chuyến đi.",
        "{poi} là một lựa chọn đáng cân nhắc để hành trình thêm đa dạng mà vẫn thuận tiện."
    ];

    private static readonly string[] PreferenceMatch =
    [
        "{poi} là một gợi ý khá phù hợp với sở thích của bạn và có thể kết hợp tự nhiên vào hành trình hiện tại.",
        "{poi} có nét gần với điều bạn quan tâm, đồng thời vẫn thuận tiện cho lịch trình.",
        "Nếu bạn muốn dành thêm thời gian cho những trải nghiệm mình yêu thích, {poi} là một điểm dừng đáng cân nhắc.",
        "{poi} là một lựa chọn phù hợp với sở thích của bạn để bổ sung thêm trải nghiệm cho chuyến đi."
    ];

    private static readonly string[] Rest =
    [
        "Khoảng nghỉ này giúp hành trình trở nên thoải mái hơn, để bạn có thêm năng lượng trước khi tiếp tục khám phá.",
        "Một chút thời gian nghỉ giữa hành trình sẽ giúp chuyến đi nhẹ nhàng hơn trước khi bạn tiếp tục đến điểm tiếp theo.",
        "Khoảng nghỉ này tạo thêm không gian để bạn thư giãn và giữ nhịp chuyến đi thoải mái hơn."
    ];

    private static readonly string[] FreeTime =
    [
        "Khoảng thời gian này được giữ linh hoạt để bạn có thể nghỉ ngơi, khám phá tự do hoặc điều chỉnh kế hoạch theo cảm hứng.",
        "Đây là khoảng thời gian linh hoạt để bạn tự do khám phá, nghỉ ngơi hoặc dành thêm thời gian cho nơi mình yêu thích.",
        "Khoảng thời gian này giúp hành trình bớt gò bó, để bạn chủ động tận hưởng chuyến đi theo cách riêng."
    ];

    private static readonly string[] Generic =
    [
        "Điểm dừng này được sắp xếp để hành trình của bạn diễn ra thuận tiện, cân đối và thoải mái hơn.",
        "Điểm dừng này giúp lịch trình có thêm sự cân bằng và giữ cho hành trình diễn ra tự nhiên hơn.",
        "Điểm dừng này được kết hợp vào chuyến đi để hành trình thêm phong phú mà vẫn thuận tiện."
    ];

    public static string Resolve(ItineraryExplanationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Kind == ItineraryItemKind.Rest)
        {
            return item.PoiId.HasValue
                ? Select(Rest, item, "rest")
                : Select(FreeTime, item, "free-time");
        }

        if (item.Kind != ItineraryItemKind.Visit)
        {
            return Select(Generic, item, "generic");
        }

        string poi = Safe(item.PoiName, "điểm dừng này");
        string? category = SafeOptional(item.CategoryName);
        string family;
        string[] templates;

        if (item.IsMandatory && category is not null)
        {
            family = "mandatory-category";
            templates = MandatoryWithCategory;
        }
        else if (item.IsMandatory)
        {
            family = "mandatory";
            templates = MandatoryGeneric;
        }
        else if (HasProvenPreferenceMatch(item))
        {
            family = "preference";
            templates = PreferenceMatch;
        }
        else if (category is not null)
        {
            family = "suggested-category";
            templates = SuggestedWithCategory;
        }
        else if (IsNearbyReason(item.CspRecommendationReason))
        {
            family = "nearby";
            templates = NearbyGeneral;
        }
        else
        {
            family = "generic";
            templates = Generic;
        }

        string template = Select(templates, item, family);
        return Limit(template.Replace("{poi}", poi, StringComparison.Ordinal)
            .Replace("{category}", category ?? string.Empty, StringComparison.Ordinal));
    }

    public static string Resolve(ItineraryItemKind kind, bool isMandatory, long? poiId) =>
        kind switch
        {
            ItineraryItemKind.Visit when isMandatory => MandatoryVisit,
            ItineraryItemKind.Visit => OptionalVisit,
            ItineraryItemKind.Rest when poiId.HasValue => RestWithPoi,
            ItineraryItemKind.Rest => RestWithoutPoi,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string Select(string[] templates, ItineraryExplanationItem item, string family) =>
        templates[StableIndex(templates.Length, item, family)];

    private static int StableIndex(int count, ItineraryExplanationItem item, string family)
    {
        string key = string.Join("|",
            item.PoiId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            SafeOptional(item.PoiName) ?? string.Empty,
            SafeOptional(item.CategoryName) ?? string.Empty,
            item.IsMandatory ? "mandatory" : "suggested",
            item.Kind, family,
            string.Join(",", item.TagNames.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim()).Order(StringComparer.Ordinal)));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(digest);
        return (int)(value % (uint)count);
    }

    private static bool HasProvenPreferenceMatch(ItineraryExplanationItem item)
    {
        string reason = item.CspRecommendationReason ?? string.Empty;
        if (!reason.Contains("preference", StringComparison.OrdinalIgnoreCase)
            && !reason.Contains("sở thích", StringComparison.OrdinalIgnoreCase)
            && !reason.Contains("interest", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return item.TagNames.Any(tag => !string.IsNullOrWhiteSpace(tag));
    }

    private static bool IsNearbyReason(string? reason) =>
        reason?.Contains("near", StringComparison.OrdinalIgnoreCase) == true
        || reason?.Contains("gần", StringComparison.OrdinalIgnoreCase) == true
        || reason?.Contains("thuận tiện", StringComparison.OrdinalIgnoreCase) == true;

    private static string Safe(string? value, string fallback) => SafeOptional(value) ?? fallback;

    private static string? SafeOptional(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized)
            || string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase)
            ? null : normalized;
    }

    private static string Limit(string value)
    {
        string normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= MaximumLength)
        {
            return normalized;
        }

        return normalized[..MaximumLength].TrimEnd('.', ',', ';', ':', ' ') + ".";
    }
}
