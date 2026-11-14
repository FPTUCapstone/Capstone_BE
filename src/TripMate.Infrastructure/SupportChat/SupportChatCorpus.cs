using TripMate.Application.Features.SupportChat;

namespace TripMate.Infrastructure.SupportChat;

internal static class SupportChatCorpus
{
    // Versioned static knowledge. Update passage IDs when their content changes.
    // Routes verified against Capstone_FE/src/lib/routes.ts and matching app/**/page.tsx files.
    internal static IReadOnlyList<KnowledgePassage> Passages { get; } = Array.AsReadOnly<KnowledgePassage>(
    [
        // FE features/scheduling/components/CreateSchedulingRequestPage.tsx and its form.
        new(new("planning-v1", "Plan a trip", "/plan"),
            "Travelers can sign in and open the trip planner at /plan. Enter the starting point, " +
            "available time and preferences in the planning form to request an itinerary. " +
            "Lập lịch trình: đăng nhập bằng tài khoản khách du lịch, mở trang lập kế hoạch, " +
            "nhập điểm xuất phát, thời gian dự kiến và sở thích."),
        // Application Features/Itineraries/List/ListMyItinerariesQueryHandler.cs.
        new(new("itineraries-v1", "My itineraries", "/itineraries"),
            "My itineraries lists the current itineraries you own and those shared through an active " +
            "travel-group membership. Open an itinerary to view its details. " +
            "Lịch trình của tôi hiển thị lịch trình cá nhân và lịch trình được chia sẻ qua nhóm đang tham gia."),
        // FE app/tours/page.tsx and app/tours/[id]/page.tsx.
        new(new("tours-v1", "Explore tours", "/tours"),
            "Open Explore tours to browse tours and open a tour's detail page. Read the displayed " +
            "tour information before choosing a booking. " +
            "Khám phá tour: xem danh sách tour và mở chi tiết tour để đọc thông tin trước khi đặt."),
        // FE features/public/pois/components/ExplorePoisPage.tsx.
        new(new("pois-v1", "Explore places", "/pois"),
            "Explore places offers point-of-interest search, filters, list and map views. " +
            "Open a place to view its details. " +
            "Khám phá địa điểm: tìm kiếm và lọc địa điểm tham quan, xem dạng danh sách hoặc bản đồ, mở trang chi tiết."),
        // Application Features/TripHistory/List/ListTravelerTripsQueryHandler.cs and FE app/account/trips/page.tsx.
        new(new("trip-history-v1", "My trips", "/account/trips"),
            "Travelers can open My trips to view their own bookings and self-planned itineraries. " +
            "Use the trip list to open the relevant trip's details. " +
            "Chuyến đi của tôi hiển thị các đặt chỗ và lịch trình tự lập của bạn để xem lại thông tin chuyến đi."),
        // Application Features/Authentication/PasswordReset and FE app/forgot-password/page.tsx.
        new(new("password-recovery-v1", "Password recovery", "/forgot-password"),
            "Open Password recovery and enter your email to request password-reset instructions. " +
            "The request response is generic and does not confirm whether an account exists. " +
            "Quên mật khẩu: mở trang khôi phục mật khẩu và nhập email để yêu cầu hướng dẫn đặt lại mật khẩu."),
        // Domain/Enums/RefundStatus.cs, Domain/Entities/Refund.cs, Application Features/Bookings/Refunds.
        new(new("refund-progress-v1", "Refund progress", "/account/trips"),
            "Refund progress is available to the booking's traveler and the tour's operator after access checks. " +
            "Pending means awaiting processing. Processing means a refund attempt is in progress. " +
            "RetryPending means another attempt is scheduled. ManualReview means manual payment-provider " +
            "processing is required. Processed records gateway acceptance or an administrator-verified manual " +
            "settlement. Bank credit timing requires separate bank or payment-provider confirmation. " +
            "Failed means processing failed. Rejected means refund validation was rejected. Use the authorized " +
            "booking context for actual refund amount, status and recorded timestamps. My trips lists traveler " +
            "booking records. Hoàn tiền: giải thích trạng thái xử lý từ thông tin đặt chỗ được phép truy cập."),
        // Application Features/Bookings/Preview/GetBookingCancellationPreviewQueryHandler.cs.
        new(new("cancellation-preview-v1", "Cancellation preview", "/account/trips"),
            "Cancellation preview is a read-only check for a booking's traveler or its tour operator. " +
            "It returns CanCancel, a reason code and message, the calculated refund amount and refund status. " +
            "The check uses booking state, departure, applicable cancellation rules and verified payment. " +
            "Review the current preview before explicitly confirming a cancellation in the booking flow. " +
            "A successful preview alone leaves the booking unchanged. My trips lists traveler booking records. " +
            "Xem trước hủy đặt chỗ: kiểm tra điều kiện và số tiền hoàn được tính trước khi xác nhận hủy."),
        // Application Features/TravelGroups/CreateTravelGroup, JoinTravelGroup and ManageInvitation.
        new(new("travel-groups-v1", "Travel groups", "/itineraries"),
            "A travel group is linked to an itinerary. The itinerary owner can create a group and becomes " +
            "its Host. Travelers join an active group using a usable invitation code. Expired invitations " +
            "and invitations whose maximum uses have been reached are unavailable. My itineraries includes " +
            "itineraries shared through active group membership. " +
            "Nhóm du lịch: chủ lịch trình tạo nhóm, thành viên tham gia bằng mã mời còn hiệu lực và xem lịch trình được chia sẻ."),
        // Application Features/Coupons/Create, GetEligibleTours, Lifecycle and FE app/partner/coupons/page.tsx.
        new(new("operator-coupons-v1", "Operator coupons", "/partner/coupons"),
            "An active approved Tour Operator can manage their own coupons on the operator coupons page. " +
            "Creating a coupon requires at least one applicable published tour owned by that operator. " +
            "Coupon codes must be unique. Operators can activate or deactivate their coupons. An expired " +
            "coupon requires an updated end date before activation. Eligibility and discount amounts come " +
            "from the coupon's actual configuration. " +
            "Mã giảm giá: nhà điều hành được duyệt quản lý mã của mình, chọn tour đã xuất bản thuộc sở hữu và kiểm tra hiệu lực mã.")
    ]).Concat(SupportChatBusinessRules.Passages).ToArray();
}