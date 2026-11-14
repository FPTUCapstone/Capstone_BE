using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.Itineraries.GetDetail;
using TripMate.Application.Features.Itineraries.List;
using TripMate.Application.Features.Tours.GetDetail;
using TripMate.Application.Features.Tours.Search;
using TripMate.Application.Features.Tours.Shared;
using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.SupportChat;

public sealed class SupportChatLiveDataReader(ISender sender) : ISupportChatLiveDataReader
{
    private const int RowLimit = 5;
    private const int ItineraryItemLimit = 50;
    private const int TextLimit = 2000;
    private const string TourContext = "tour";
    private const string RefundContext = "refund";
    private const string Navigate = "navigate";
    private const string SelectContext = "selectContext";
    private const string VietnamTimeZoneId = "Asia/Ho_Chi_Minh";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public async Task<Result<ChatLiveData>> ReadAsync(ChatQueryPlan plan, long userId, string role,
        ChatResourceContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (userId <= 0)
            return Result.Failure<ChatLiveData>(SupportChatErrors.Unauthorized, "An authenticated account is required.");
        if (role is not (nameof(UserRole.Traveler) or nameof(UserRole.TourOperator)))
            return Forbidden();
        if (plan is null || !new ChatQueryPlanValidator().Validate(plan).IsValid)
            return InvalidRequest();
        if (context is not null &&
            (context.Kind is not (TourContext or RefundContext or TripRecordKinds.Itinerary) || !IsPositiveId(context.Id)))
            return InvalidRequest();

        DateOnly? departureDate = plan.DepartureDate is null ? null :
            DateOnly.ParseExact(plan.DepartureDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var search = new SearchToursQuery(Destination: plan.Destination, DepartureDate: departureDate,
            MaxPrice: plan.MaxPrice, PageSize: RowLimit, Keyword: plan.Search);
        // The shared planner bounds and the existing query validator both apply, including Unicode normalization.
        if (!new SearchToursQueryValidator().Validate(search).IsValid)
            return InvalidRequest();

        switch (plan.Intent)
        {
            case ChatIntents.General:
                return Result.Success(new ChatLiveData(string.Empty, []));
            case ChatIntents.OpenPlanner:
                return role == nameof(UserRole.Traveler)
                    ? FeaturePage("Lập lịch trình", "/plan") : Forbidden();
            case ChatIntents.BrowsePois:
                return FeaturePage("Khám phá địa điểm", "/pois");
            case ChatIntents.AccountProfile:
                return role == nameof(UserRole.Traveler)
                    ? FeaturePage("Hồ sơ cá nhân", "/account/profile") : Forbidden();
            case ChatIntents.TravelPreferences:
                return role == nameof(UserRole.Traveler)
                    ? FeaturePage("Sở thích du lịch", "/account/preferences") : Forbidden();
            case ChatIntents.AccountSecurity:
                return role == nameof(UserRole.Traveler)
                    ? FeaturePage("Bảo mật tài khoản", "/account/security") : Forbidden();
            case ChatIntents.SearchTours:
                return await SearchAsync(plan, search, role, cancellationToken);
            case ChatIntents.PrepareBooking:
            case ChatIntents.TourDetail:
                if (plan.Intent == ChatIntents.PrepareBooking && role != nameof(UserRole.Traveler))
                    return Forbidden();
                var tourId = ResourceId(plan, context, TourContext);
                return tourId is null
                    ? await SearchAsync(plan, search, role, cancellationToken)
                    : await TourAsync(plan, tourId.Value, role, departureDate, cancellationToken);
            case ChatIntents.MyBookings:
                return role == nameof(UserRole.TourOperator)
                    ? OperatorSelection()
                    : await BookingsAsync(userId, false, cancellationToken);
            case ChatIntents.Refund:
                var bookingId = ResourceId(plan, context, RefundContext);
                if (bookingId.HasValue)
                    return await RefundAsync(bookingId.Value, role, cancellationToken);
                return role == nameof(UserRole.TourOperator)
                    ? OperatorSelection()
                    : await BookingsAsync(userId, true, cancellationToken);
            case ChatIntents.MyItineraries:
            case ChatIntents.Itinerary:
                if (role != nameof(UserRole.Traveler))
                    return Forbidden();
                var itineraryId = ResourceId(plan, context, TripRecordKinds.Itinerary);
                return plan.Intent == ChatIntents.Itinerary && itineraryId.HasValue
                    ? await ItineraryAsync(itineraryId.Value, userId, cancellationToken)
                    : await ItinerariesAsync(userId, cancellationToken);
            default:
                return InvalidRequest();
        }
    }

    private static Result<ChatLiveData> FeaturePage(string label, string url) =>
        Result.Success(new ChatLiveData(
            JsonSerializer.Serialize(new
            {
                kind = "featureNavigation",
                title = label,
                url
            }, JsonOptions),
            [new ChatAction(Navigate, label, url)]));

    private async Task<Result<ChatLiveData>> SearchAsync(ChatQueryPlan plan, SearchToursQuery query,
        string role, CancellationToken token)
    {
        var result = await sender.Send(query, token);
        if (result.IsFailure)
            return Failure(result);
        var page = result.Value;
        var items = page.Items.Where(item => IsPositiveId(item.TourId)).Take(RowLimit).ToArray();
        if (page.TotalCount == 1 && items.Length == 1 &&
            (plan.Intent is ChatIntents.PrepareBooking or ChatIntents.TourDetail))
        {
            var tourId = long.Parse(items[0].TourId, NumberStyles.None, CultureInfo.InvariantCulture);
            return await TourAsync(plan, tourId, role, query.DepartureDate, token);
        }
        var prompt = plan.Intent == ChatIntents.PrepareBooking
            ? BookingPrompt(plan)
            : "Xem chi tiết tour này";
        var actions = new List<ChatAction>();
        foreach (var item in items)
        {
            var availability = item.AvailabilityStatus switch
            {
                "noUpcomingSchedule" => "Chưa có lịch khởi hành sắp tới",
                TourAvailability.Available => "Có thông tin chỗ trống; kiểm tra lại khi chọn lịch",
                TourAvailability.SoldOut => "Lịch đang hiển thị đã hết chỗ",
                _ => "Chưa xác định được chỗ trống",
            };
            var price = item.BasePrice.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
            var fitsParty = !plan.RequestedParticipants.HasValue ||
                item.RemainingSlots >= plan.RequestedParticipants.Value;
            if (fitsParty)
                actions.Add(new(SelectContext, item.Title, Context: new(TourContext, item.TourId), Prompt: prompt,
                    Description: $"Giá cơ bản {price} {item.Currency}. {availability}."));
            actions.Add(new(Navigate, $"Xem {item.Title}", $"/tours/{item.TourId}"));
        }
        actions.Add(new(Navigate, "Xem tất cả tour", "/tours"));
        return Live(new
        {
            plan.Intent,
            page.AsOfUtc,
            page.TotalCount,
            page = page.Page,
            limit = RowLimit,
            hasMore = page.TotalCount > items.Length,
            requestedParticipants = plan.RequestedParticipants,
            capacityGuidance = "Chỉ đề xuất lịch có đủ chỗ cho tổng số người lớn, trẻ em và em bé. Lịch thiếu hoặc chưa rõ chỗ trống không phải lựa chọn phù hợp cho đoàn.",
            selectionRequired = true,
            items = items.Select(item => new
            {
                item.TourId,
                item.Title,
                destinations = item.Destinations.Take(RowLimit),
                item.DurationDays,
                item.BasePrice,
                item.Currency,
                item.AvailabilityStatus,
                item.RemainingSlots,
                item.DepartureAtUtc,
                eligibleForRequestedParty = !plan.RequestedParticipants.HasValue ||
                    item.RemainingSlots >= plan.RequestedParticipants.Value,
            }),
        }, actions);
    }

    private async Task<Result<ChatLiveData>> TourAsync(ChatQueryPlan plan, long tourId, string role,
        DateOnly? departureDate, CancellationToken token)
    {
        var result = await sender.Send(new GetTourDetailQuery(tourId), token);
        if (result.IsFailure)
            return Failure(result);
        var tour = result.Value;
        var schedules = tour.Schedules.Where(schedule => IsPositiveId(schedule.ScheduleId));
        if (departureDate.HasValue)
        {
            TourSearchCriteria.TryGetDepartureBoundsUtc(departureDate.Value, out var from, out var to);
            schedules = schedules.Where(schedule => schedule.StartAtUtc >= from.UtcDateTime &&
                schedule.StartAtUtc < to.UtcDateTime);
        }
        if (plan.MaxPrice.HasValue)
            schedules = schedules.Where(schedule => schedule.AdultPrice <= plan.MaxPrice.Value);
        // Keep the displayed evidence and the offered booking departures together within the same limit.
        var matchingSchedules = schedules.OrderByDescending(schedule =>
            plan.RequestedParticipants.HasValue && schedule.Bookable && schedule.RemainingSlots >= plan.RequestedParticipants.Value).ToArray();
        var hasEligibleDeparture = matchingSchedules.Any(schedule => schedule.Bookable &&
            (!plan.RequestedParticipants.HasValue || schedule.RemainingSlots >= plan.RequestedParticipants.Value));
        var displayedSchedules = matchingSchedules.Take(RowLimit).ToArray();
        var itineraryItems = tour.Itinerary.SelectMany(day => day.Items.Select(item => new
        {
            day.DayNo,
            item.Poi.Name,
            item.PlannedTime,
            item.StayDurationMinutes,
        })).Take(RowLimit + 1).ToArray();
        var actions = new List<ChatAction> { new(Navigate, "Xem tour và lịch khởi hành", $"/tours/{tourId}") };
        if (role == nameof(UserRole.Traveler) && plan.PartySize.HasValue && tour.HasBookableSchedule)
        {
            foreach (var schedule in displayedSchedules.Where(schedule => schedule.Bookable &&
                         schedule.RemainingSlots >= plan.RequestedParticipants!.Value))
            {
                var vietnamDeparture = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(schedule.StartAtUtc, DateTimeKind.Utc),
                    TimeZoneInfo.FindSystemTimeZoneById(VietnamTimeZoneId));
                var departure = vietnamDeparture.ToString("dd/MM/yyyy HH:mm 'GMT+7'", CultureInfo.InvariantCulture);
                var adultPrice = schedule.AdultPrice.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
                var composition = FormattableString.Invariant($"{plan.PartySize.Value} người lớn, {plan.ChildCount ?? 0} trẻ em, {plan.InfantCount ?? 0} em bé");
                var extraCounts = plan.ChildCount.HasValue || plan.InfantCount.HasValue
                    ? FormattableString.Invariant($"&children={plan.ChildCount ?? 0}&infants={plan.InfantCount ?? 0}") : "";
                actions.Add(new(Navigate, $"Chọn khởi hành {departure}",
                    FormattableString.Invariant($"/tours/{tourId}/book?scheduleId={schedule.ScheduleId}&adults={plan.PartySize.Value}") + extraCounts,
                    Description: $"{adultPrice} {tour.Currency}/người lớn. Gợi ý {composition}. Giá và chỗ trống sẽ được kiểm tra lại tại biểu mẫu đặt tour."));
            }
        }
        return Live(new
        {
            plan.Intent,
            tourId = tourId.ToString(CultureInfo.InvariantCulture),
            tour.Title,
            tour.Destination,
            tour.DurationDays,
            tour.BasePrice,
            tour.Currency,
            tour.AsOfUtc,
            textLimit = TextLimit,
            description = LimitText(tour.Description),
            descriptionTruncated = tour.Description?.Length > TextLimit,
            inclusions = LimitText(tour.Inclusions),
            inclusionsTruncated = tour.Inclusions?.Length > TextLimit,
            exclusions = LimitText(tour.Exclusions),
            exclusionsTruncated = tour.Exclusions?.Length > TextLimit,
            contentGuidance = "Các phần được đánh dấu rút gọn còn nội dung trên trang chi tiết tour.",
            cancellationRuleLimit = RowLimit,
            cancellationRules = tour.CancellationPolicy.Rules.Take(RowLimit),
            hasMoreCancellationRules = tour.CancellationPolicy.Rules.Count > RowLimit,
            itineraryItemLimit = RowLimit,
            itinerary = itineraryItems.Take(RowLimit),
            hasMoreItineraryItems = itineraryItems.Length > RowLimit,
            partySize = plan.PartySize,
            childCount = plan.ChildCount,
            infantCount = plan.InfantCount,
            requestedParticipants = plan.RequestedParticipants,
            hasEligibleDeparture,
            selectionRequired = true,
            guidance = plan.RequestedParticipants.HasValue && !hasEligibleDeparture
                ? "Các lịch phù hợp ngày/giá đã yêu cầu hiện không đủ chỗ hoặc chưa mở đặt cho tổng số người trong đoàn. Không đề xuất đặt lịch thiếu chỗ. Hãy chọn ngày hoặc tour khác."
                : plan.PartySize.HasValue
                ? "Chọn lịch khởi hành. Giá và chỗ trống sẽ được kiểm tra lại tại biểu mẫu đặt tour."
                : "Vui lòng nhập số người lớn và chọn lịch khởi hành.",
            scheduleLimit = RowLimit,
            totalMatchingSchedules = matchingSchedules.Length,
            hasMoreSchedules = matchingSchedules.Length > RowLimit,
            schedules = displayedSchedules.Select(schedule => new
            {
                schedule.ScheduleId,
                schedule.StartAtUtc,
                schedule.EndAtUtc,
                schedule.AdultPrice,
                schedule.ChildPrice,
                schedule.InfantPrice,
                schedule.RemainingSlots,
                schedule.AvailabilityStatus,
                schedule.Bookable,
                eligibleForRequestedParty = schedule.Bookable && (!plan.RequestedParticipants.HasValue ||
                    schedule.RemainingSlots >= plan.RequestedParticipants.Value),
                startAtLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(schedule.StartAtUtc, DateTimeKind.Utc),
                    TimeZoneInfo.FindSystemTimeZoneById(VietnamTimeZoneId)).ToString("dd/MM/yyyy HH:mm 'GMT+7'", CultureInfo.InvariantCulture),
            }),
        }, actions);
    }

    private async Task<Result<ChatLiveData>> BookingsAsync(long userId, bool refundsOnly,
        CancellationToken token)
    {
        var items = new List<TripHistoryItemResponse>();
        var pages = new List<object>();
        long totalCandidates = 0;
        var tabs = refundsOnly
            ? new[] { ListTravelerTripsQuery.TabCancelled, ListTravelerTripsQuery.TabUpcoming, ListTravelerTripsQuery.TabCompleted }
            : ListTravelerTripsQuery.AllowedTabs;
        foreach (var tab in tabs)
        {
            var result = await sender.Send(new ListTravelerTripsQuery(userId, tab,
                ListTravelerTripsQuery.TripTypeTourBooking,
                null, null, 1, RowLimit), token);
            if (result.IsFailure)
                return Failure(result);
            totalCandidates += result.Value.TotalCount;
            pages.Add(new { tab, page = 1, limit = RowLimit, result.Value.TotalCount });
            items.AddRange(result.Value.Items.Where(item => item.Id > 0 && item.Kind == TripRecordKinds.CommerceBooking).Take(RowLimit));
        }
        var matching = items.Where(item => !refundsOnly || item.Actions.CanViewRefund)
            .DistinctBy(item => item.Id).ToArray();
        var choices = matching.Take(RowLimit).ToArray();
        var actions = new List<ChatAction>();
        foreach (var item in choices)
        {
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            if (item.Actions.CanViewRefund)
            {
                actions.Add(new(SelectContext, $"Hoàn tiền {item.Title}", Context: new(RefundContext, id),
                    Prompt: "Kiểm tra tiến độ hoàn tiền của booking này"));
                actions.Add(new(Navigate, $"Xem hoàn tiền {item.Title}", $"/bookings/{id}/refund"));
            }
        }
        actions.Add(new(Navigate, "Xem các chuyến đi của tôi", "/account/trips"));
        return Live(new
        {
            selectionRequired = true,
            limit = RowLimit,
            hasMore = totalCandidates > items.Count || matching.Length > RowLimit,
            scope = "Trang đầu tiên của từng trạng thái chuyến đi. Danh sách lựa chọn tối đa 5 mục.",
            pages,
            items = choices.Select(item => new
            {
                item.Kind,
                id = item.Id.ToString(CultureInfo.InvariantCulture),
                item.Title,
                item.BookingCode,
                item.Status,
                item.DepartureAtUtc,
                item.ParticipantCount,
                item.TotalAmount,
                currency = PaymentTransaction.DefaultCurrency,
                hasRefund = item.Actions.CanViewRefund,
            }),
        }, actions);
    }

    private async Task<Result<ChatLiveData>> ItinerariesAsync(long userId, CancellationToken token)
    {
        var result = await sender.Send(new ListMyItinerariesQuery(userId, 1, RowLimit), token);
        if (result.IsFailure)
            return Failure(result);
        var page = result.Value;
        var items = page.Items.Where(item => item.ItineraryId > 0).Take(RowLimit).ToArray();
        var actions = new List<ChatAction>();
        foreach (var item in items)
        {
            var id = item.ItineraryId.ToString(CultureInfo.InvariantCulture);
            actions.Add(new(SelectContext, item.Title ?? "Xem lịch trình", Context: new(TripRecordKinds.Itinerary, id),
                Prompt: "Xem lịch trình này"));
            actions.Add(new(Navigate, item.Title ?? "Mở lịch trình", $"/itinerary/{id}"));
        }
        actions.Add(new(Navigate, "Xem tất cả lịch trình", "/itineraries"));
        return Live(new
        {
            selectionRequired = true,
            page = page.Page,
            limit = RowLimit,
            page.TotalCount,
            hasMore = page.TotalCount > items.Length,
            items = items.Select(item => new
            {
                itineraryId = item.ItineraryId.ToString(CultureInfo.InvariantCulture),
                item.Title,
                item.Status,
                item.ValidFrom,
                item.ValidTo,
                item.StopCount,
                item.CanManage,
            }),
        }, actions);
    }

    private async Task<Result<ChatLiveData>> RefundAsync(long bookingId, string role, CancellationToken token)
    {
        var result = await sender.Send(new GetBookingRefundProgressQuery(bookingId), token);
        if (result.IsFailure)
            return Failure(result);
        var refund = result.Value;
        return Live(new
        {
            bookingId = bookingId.ToString(CultureInfo.InvariantCulture),
            refund.BookingCode,
            refund.RefundAmount,
            currency = PaymentTransaction.DefaultCurrency,
            refund.Status,
            refund.RequestedAtUtc,
            refund.ProcessedAtUtc,
        }, [new(Navigate, "Xem tiến độ hoàn tiền", $"/bookings/{bookingId}/refund"),
            new(Navigate, "Xem các booking", role == nameof(UserRole.TourOperator) ? "/partner/bookings" : "/account/trips")]);
    }

    private async Task<Result<ChatLiveData>> ItineraryAsync(long itineraryId, long userId, CancellationToken token)
    {
        var result = await sender.Send(new GetItineraryDetailQuery(itineraryId, userId), token);
        if (result.IsFailure)
            return Failure(result);
        var itinerary = result.Value;
        return Live(new
        {
            itineraryId = itineraryId.ToString(CultureInfo.InvariantCulture),
            itinerary.Title,
            itinerary.Status,
            itinerary.ValidFrom,
            itinerary.ValidTo,
            itinerary.TotalEstimatedCost,
            currency = PaymentTransaction.DefaultCurrency,
            itinerary.TotalDurationMinutes,
            itemLimit = ItineraryItemLimit,
            hasMoreItems = itinerary.Items.Count > ItineraryItemLimit,
            items = itinerary.Items.Take(ItineraryItemLimit).Select(item => new
            {
                item.SequenceNo,
                item.PoiName,
                item.PlannedArrival,
                item.PlannedDeparture,
                item.StayDurationMinutes,
                item.EstimatedCost,
                item.IsUnavailable,
            }),
        }, [new(Navigate, "Mở lịch trình", $"/itinerary/{itineraryId}")]);
    }

    private static string BookingPrompt(ChatQueryPlan plan)
    {
        var prompt = plan.PartySize.HasValue
            ? FormattableString.Invariant($"Tôi muốn đặt tour này cho {plan.PartySize.Value} người lớn")
            : "Tôi muốn đặt tour này, hãy hỏi số người lớn và cho tôi chọn ngày khởi hành";
        if (plan.DepartureDate is not null)
            prompt += $" khởi hành ngày {plan.DepartureDate}";
        if (plan.ChildCount.HasValue || plan.InfantCount.HasValue)
            prompt += FormattableString.Invariant($", {plan.ChildCount ?? 0} trẻ em, {plan.InfantCount ?? 0} em bé");
        if (plan.MaxPrice.HasValue)
            prompt += FormattableString.Invariant($" với giá tối đa {plan.MaxPrice.Value} {PaymentTransaction.DefaultCurrency}");
        return prompt;
    }

    private static string? LimitText(string? value)
    {
        if (value is null || value.Length <= TextLimit)
            return value;
        var length = char.IsHighSurrogate(value[TextLimit - 1]) ? TextLimit - 1 : TextLimit;
        return value[..length];
    }

    private static long? ResourceId(ChatQueryPlan plan, ChatResourceContext? context, string kind)
    {
        var id = plan.ResourceId ?? (context?.Kind == kind ? context.Id : null);
        return id is null ? null : long.Parse(id, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static bool IsPositiveId(string? id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;

    private static Result<ChatLiveData> OperatorSelection() => Live(new
    {
        selectionRequired = true,
        guidance = "Chọn booking trong trang quản lý booking của bạn để xem chi tiết hoặc tiến độ hoàn tiền.",
    }, [new(Navigate, "Chọn booking của nhà điều hành", "/partner/bookings")]);

    private static Result<ChatLiveData> Live(object facts, IReadOnlyList<ChatAction> actions) =>
        Result.Success(new ChatLiveData(JsonSerializer.Serialize(facts, JsonOptions), actions));

    private static Result<ChatLiveData> Failure(Result result) =>
        Result.Failure<ChatLiveData>(result.ErrorCode!, result.ErrorMessage!, result.ErrorMetadata);

    private static Result<ChatLiveData> InvalidRequest() =>
        Result.Failure<ChatLiveData>(SupportChatErrors.InvalidRequest, "The support chat query is invalid.");

    private static Result<ChatLiveData> Forbidden() =>
        Result.Failure<ChatLiveData>(SupportChatErrors.Forbidden, "This account cannot use the requested capability.");
}