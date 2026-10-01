using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Explanation;

public static class ItineraryExplanationFallback
{
    public const string MandatoryVisit = "Điểm đến bắt buộc theo yêu cầu của bạn.";

    public const string OptionalVisit = "Điểm gợi ý phù hợp với sở thích và khung giờ của bạn.";

    public const string RestWithPoi = "Điểm nghỉ ngơi được đề xuất trên lộ trình.";

    public const string RestWithoutPoi = "Thời gian nghỉ ngơi tự do giữa các điểm đến.";

    public static string Resolve(ItineraryItemKind kind, bool isMandatory, long? poiId) =>
        kind switch
        {
            ItineraryItemKind.Visit when isMandatory => MandatoryVisit,
            ItineraryItemKind.Visit => OptionalVisit,
            ItineraryItemKind.Rest when poiId.HasValue => RestWithPoi,
            ItineraryItemKind.Rest => RestWithoutPoi,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}