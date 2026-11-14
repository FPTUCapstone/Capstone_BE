using System.Text.Json;

using MediatR;

using Moq;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Itineraries.GetDetail;
using TripMate.Application.Features.Itineraries.List;
using TripMate.Application.Features.SupportChat;
using TripMate.Application.Features.Tours.GetDetail;
using TripMate.Application.Features.Tours.Search;
using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.List;

namespace TripMate.Application.UnitTests.Features.SupportChat;

public sealed class SupportChatLiveDataReaderTests
{
    private readonly Mock<ISender> sender = new(MockBehavior.Strict);

    private Task<Result<ChatLiveData>> Read(ChatQueryPlan plan, ChatResourceContext? context = null,
        string role = "Traveler", long userId = 42, CancellationToken token = default) =>
        new SupportChatLiveDataReader(sender.Object).ReadAsync(plan, userId, role, context, token);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("https://evil.example")]
    [InlineData("+12")]
    [InlineData("")]
    public async Task InvalidResourceIdsAreRejectedBeforeAnyQuery(string id)
    {
        Assert.Equal(SupportChatErrors.InvalidRequest,
            (await Read(new(ChatIntents.TourDetail, ResourceId: id))).ErrorCode);
        sender.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task InvalidPartySizesAreRejected(int count)
    {
        Assert.Equal(SupportChatErrors.InvalidRequest,
            (await Read(new(ChatIntents.PrepareBooking, PartySize: count))).ErrorCode);
    }

    [Fact]
    public async Task InvalidFiltersAndContextAreRejected()
    {
        ChatQueryPlan[] plans = [new("invented"), new(ChatIntents.SearchTours, MaxPrice: -1),
            new(ChatIntents.SearchTours, DepartureDate: "2026-02-30"),
            new(ChatIntents.SearchTours, Search: new string('x', 301)),
            new(ChatIntents.SearchTours, Search: "\ud800")];
        foreach (var plan in plans)
            Assert.Equal(SupportChatErrors.InvalidRequest, (await Read(plan)).ErrorCode);
        Assert.Equal(SupportChatErrors.InvalidRequest,
            (await Read(new(ChatIntents.Refund), new("refund", "0"))).ErrorCode);
    }

    [Theory]
    [InlineData("Administrator", 42, SupportChatErrors.Forbidden)]
    [InlineData("Traveler", 0, SupportChatErrors.Unauthorized)]
    public async Task CallerMustBeAuthenticatedAndInSupportedRole(string role, long id, string error)
    {
        Assert.Equal(error, (await Read(new(ChatIntents.MyBookings), role: role, userId: id)).ErrorCode);
    }

    [Fact]
    public async Task GeneralDoesNotDispatchAQuery()
    {
        var result = await Read(new(ChatIntents.General));
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Facts);
        Assert.Empty(result.Value.Actions);
    }

    [Fact]
    public async Task SearchUsesLiveQueryFiltersAndBoundsProjection()
    {
        SetupSearch(7);
        var result = await Read(new(ChatIntents.SearchTours, Search: "biển", Destination: "Đà Nẵng",
            DepartureDate: "2027-01-02", MaxPrice: 2000000));
        Assert.True(result.IsSuccess);
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.Equal(5, facts.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(facts.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.DoesNotContain("private-thumbnail", result.Value.Facts);
        sender.Verify(x => x.Send(It.Is<SearchToursQuery>(q => q.PageSize == 5 && q.Page == 1 &&
            q.Keyword == "biển" && q.Destination == "Đà Nẵng" && q.MaxPrice == 2000000 &&
            q.DepartureDate == new DateOnly(2027, 1, 2)), It.IsAny<CancellationToken>()), Times.Once);
        Assert.All(result.Value.Actions.Where(a => a.Type == "navigate"),
            a => Assert.True(a.Url == "/tours" || a.Url?.StartsWith("/tours/", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task BookingSearchOffersRealTourContextAndPreservesPartyIntent()
    {
        SetupSearch(2);
        var result = await Read(new(ChatIntents.PrepareBooking, Search: "biển", PartySize: 2));
        var choices = result.Value.Actions.Where(a => a.Type == "selectContext").ToArray();
        Assert.Equal(2, choices.Length);
        Assert.All(choices, action =>
        {
            Assert.Equal("tour", action.Context!.Kind);
            Assert.Contains(action.Context.Id, new[] { "1", "2" });
            Assert.Contains("đặt tour này cho 2 người lớn", action.Prompt);
        });
        Assert.DoesNotContain(result.Value.Actions, a => a.Url?.Contains("/book?") == true);
        sender.Verify(x => x.Send(It.IsAny<GetTourDetailQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CatalogTourWithoutFutureDepartureHasExplicitAvailabilityInActions()
    {
        sender.Setup(x => x.Send(It.IsAny<SearchToursQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PagedToursResponseDto(1, 5, 1, 1, DateTime.UtcNow,
                [new("7", "Tour Huế", ["Huế"], "Operator", 1, 100000, "VND", null, null,
                    "noUpcomingSchedule", null, null, null)])));
        var result = await Read(new(ChatIntents.SearchTours));
        var action = Assert.Single(result.Value.Actions, action => action.Type == "selectContext");
        Assert.Contains("Chưa có lịch khởi hành", action.Description);
        Assert.Contains("100.000 VND", action.Description);
    }

    [Theory]
    [InlineData(ChatIntents.PrepareBooking)]
    [InlineData(ChatIntents.TourDetail)]
    public async Task UniquePublicTourMatchReadsDetailAndOffersDeparturesInSameResponse(string intent)
    {
        SetupSearch(1);
        sender.Setup(x => x.Send(new GetTourDetailQuery(1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new TourDetailDto("1", "Huế", "Description", "Huế", ["Huế"], 2,
                100000, "VND", new("Operator", null), [], null, null, new([]), [], [Schedule("10", 5)],
                true, new(null, 0), DateTime.UtcNow)));
        var result = await Read(new(intent, Search: "Huế", PartySize: 2));
        Assert.Contains(result.Value.Actions, a => a.Url == "/tours/1/book?scheduleId=10&adults=2");
        Assert.DoesNotContain(result.Value.Actions, a => a.Type == "selectContext");
        sender.Verify(x => x.Send(new GetTourDetailQuery(1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BookingUsesOnlyExplicitlySelectedTourAndOffersEveryEligibleDeparture()
    {
        SetupDetail([Schedule("10", 2), Schedule("11", 3), Schedule("12", 1), Schedule("13", 5, false)]);
        var result = await Read(new(ChatIntents.PrepareBooking, PartySize: 2), new("tour", "7"));
        var urls = result.Value.Actions.Select(a => a.Url).ToArray();
        Assert.Contains("/tours/7/book?scheduleId=10&adults=2", urls);
        Assert.Contains("/tours/7/book?scheduleId=11&adults=2", urls);
        Assert.DoesNotContain(urls, u => u?.Contains("scheduleId=12") == true || u?.Contains("scheduleId=13") == true);
    }

    [Fact]
    public async Task BookingChoiceDisplaysVietnamTimeAndActualAdultPrice()
    {
        SetupDetail([Schedule("10", 5)]);
        var result = await Read(new(ChatIntents.PrepareBooking, ResourceId: "7", PartySize: 2));
        var choice = Assert.Single(result.Value.Actions, a => a.Url?.Contains("/book?") == true);
        Assert.Contains("02/01/2027 09:00", choice.Label);
        Assert.Contains("GMT+7", choice.Label);
        Assert.Contains("100.000 VND/người lớn", choice.Description);
        Assert.Contains("2 người lớn", choice.Description);
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.Equal("2027-01-02T02:00:00Z", facts.RootElement.GetProperty("schedules")[0].GetProperty("startAtUtc").GetString());
    }

    [Fact]
    public async Task MissingPartySizeRequestsSelectionWithoutDefaultCount()
    {
        SetupDetail([Schedule("10", 20)]);
        var result = await Read(new(ChatIntents.PrepareBooking, ResourceId: "7"));
        Assert.DoesNotContain(result.Value.Actions, a => a.Url?.Contains("/book?") == true);
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.Contains("số người lớn", facts.RootElement.GetProperty("guidance").GetString());
    }

    [Fact]
    public async Task MixedPartyPreservesAllCountsAndRequiresCapacityForEveryParticipant()
    {
        SetupDetail([Schedule("10", 2), Schedule("11", 3)]);
        var result = await Read(new(ChatIntents.PrepareBooking, ResourceId: "7", PartySize: 2,
            ChildCount: 1, InfantCount: 0));
        var action = Assert.Single(result.Value.Actions, a => a.Url?.Contains("/book?") == true);
        Assert.Equal("/tours/7/book?scheduleId=11&adults=2&children=1&infants=0", action.Url);
        Assert.Contains("1 trẻ em", action.Description);
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.Equal(3, facts.RootElement.GetProperty("requestedParticipants").GetInt32());
    }

    [Fact]
    public async Task TwentyFiveParticipantsCannotBeOfferedTwentySlotsAsEligible()
    {
        SetupDetail([Schedule("10", 20)]);
        var result = await Read(new(ChatIntents.PrepareBooking, ResourceId: "7", PartySize: 25));
        Assert.DoesNotContain(result.Value.Actions, a => a.Url?.Contains("/book?") == true);
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.False(facts.RootElement.GetProperty("hasEligibleDeparture").GetBoolean());
        Assert.False(facts.RootElement.GetProperty("schedules")[0].GetProperty("eligibleForRequestedParty").GetBoolean());
        Assert.Contains("không đủ", facts.RootElement.GetProperty("guidance").GetString());
    }

    [Theory]
    [InlineData(2, -1, 0)]
    [InlineData(2, 0, -1)]
    [InlineData(50, 1, 0)]
    [InlineData(2, int.MaxValue, int.MaxValue)]
    public async Task InvalidCompositionIsRejectedBeforeReading(int adults, int children, int infants)
    {
        var result = await Read(new(ChatIntents.PrepareBooking, ResourceId: "7", PartySize: adults,
            ChildCount: children, InfantCount: infants));
        Assert.Equal(SupportChatErrors.InvalidRequest, result.ErrorCode);
        sender.Verify(x => x.Send(It.IsAny<GetTourDetailQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManySchedulesAreBoundedAndLargeValidPartyCountIsSafe()
    {
        SetupDetail(Enumerable.Range(10, 8).Select(i => Schedule(i.ToString(), 50)).ToArray());
        var result = await Read(new(ChatIntents.PrepareBooking, ResourceId: "7", PartySize: 50));
        Assert.Equal(5, result.Value.Actions.Count(a => a.Url?.Contains("/book?") == true));
        Assert.All(result.Value.Actions.Where(a => a.Url?.Contains("/book?") == true),
            a => Assert.EndsWith("&adults=50", a.Url));
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.True(facts.RootElement.GetProperty("hasMoreSchedules").GetBoolean());
    }

    [Fact]
    public async Task BookingDepartureDateFiltersChoicesUsingVietnamDay()
    {
        SetupDetail([Schedule("10", 5) with { StartAtUtc = new DateTime(2027, 1, 1, 18, 0, 0, DateTimeKind.Utc) },
            Schedule("11", 5) with { StartAtUtc = new DateTime(2027, 1, 2, 18, 0, 0, DateTimeKind.Utc) }]);
        var result = await Read(new(ChatIntents.PrepareBooking, ResourceId: "7", PartySize: 2, DepartureDate: "2027-01-02"));
        Assert.Contains(result.Value.Actions, a => a.Url == "/tours/7/book?scheduleId=10&adults=2");
        Assert.DoesNotContain(result.Value.Actions, a => a.Url?.Contains("scheduleId=11") == true);
    }

    [Fact]
    public async Task TourOperatorCannotPrepareTravelerBooking()
    {
        Assert.Equal(SupportChatErrors.Forbidden,
            (await Read(new(ChatIntents.PrepareBooking, ResourceId: "7", PartySize: 2), role: "TourOperator")).ErrorCode);
    }

    [Fact]
    public async Task TourDetailForOperatorExposesNoBookingLinkOrPrivateFields()
    {
        SetupDetail([Schedule("10", 5)]);
        var result = await Read(new(ChatIntents.TourDetail, ResourceId: "7", PartySize: 2), role: "TourOperator");
        Assert.DoesNotContain(result.Value.Actions, a => a.Url?.Contains("/book?") == true);
        Assert.DoesNotContain("private-phone", result.Value.Facts);
    }

    [Fact]
    public async Task TravelerBookingListOffersRefundOnlyForExistingRefund()
    {
        SetupTrips([Trip(1, true), Trip(2, false)]);
        var result = await Read(new(ChatIntents.MyBookings));
        var refund = Assert.Single(result.Value.Actions, a => a.Type == "selectContext");
        Assert.Equal(new ChatResourceContext("refund", "1"), refund.Context);
        Assert.Contains(result.Value.Actions, a => a.Type == "navigate" && a.Url == "/account/trips");
        sender.Verify(x => x.Send(It.Is<ListTravelerTripsQuery>(q => q.TravelerUserId == 42 && q.PageSize == 5 &&
            q.TripType == ListTravelerTripsQuery.TripTypeTourBooking), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task RefundWithoutSameKindContextListsChoicesWithoutReadingOtherResource()
    {
        SetupTrips([Trip(1, true), Trip(2, false)]);
        var result = await Read(new(ChatIntents.Refund), new("itinerary", "999"));
        Assert.Single(result.Value.Actions, a => a.Type == "selectContext");
        sender.Verify(x => x.Send(It.IsAny<GetBookingRefundProgressQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefundDetailUsesExplicitIdAndOmitsGatewayData()
    {
        sender.Setup(x => x.Send(new GetBookingRefundProgressQuery(123), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new BookingRefundProgressResponse(123, "BK-123", 100000, "Processed",
                "private-gateway", 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "private-error")));
        var result = await Read(new(ChatIntents.Refund, ResourceId: "123"), new("refund", "456"));
        Assert.Contains("Processed", result.Value.Facts);
        Assert.Contains("VND", result.Value.Facts);
        Assert.DoesNotContain("private-", result.Value.Facts);
        Assert.Contains(result.Value.Actions, a => a.Type == "navigate" && a.Url == "/bookings/123/refund");
    }

    [Fact]
    public async Task AuthorizedQueryFailurePropagatesWithoutFallback()
    {
        sender.Setup(x => x.Send(new GetBookingRefundProgressQuery(123), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<BookingRefundProgressResponse>("refund.forbidden", "Forbidden"));
        Assert.Equal("refund.forbidden", (await Read(new(ChatIntents.Refund), new("refund", "123"))).ErrorCode);
    }

    [Fact]
    public async Task ItineraryDetailUsesCallerIdentityAndExistingAuthorization()
    {
        sender.Setup(x => x.Send(new GetItineraryDetailQuery(9, 42), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ItineraryDetailResponse(9, 100, "Huế", 1, "Ready", null, null,
                true, 100000, 90, [])));
        var result = await Read(new(ChatIntents.Itinerary), new("itinerary", "9"));
        Assert.Contains("Hu", result.Value.Facts);
        Assert.Contains(result.Value.Actions, a => a.Type == "navigate" && a.Url == "/itinerary/9");
    }

    [Theory]
    [InlineData(ChatIntents.MyItineraries)]
    [InlineData(ChatIntents.Itinerary)]
    public async Task OperatorCannotReadTravelerItineraries(string intent)
    {
        Assert.Equal(SupportChatErrors.Forbidden,
            (await Read(new(intent), role: "TourOperator")).ErrorCode);
    }

    [Theory]
    [InlineData(ChatIntents.MyBookings)]
    [InlineData(ChatIntents.Refund)]
    public async Task OperatorWithoutSelectedBookingReceivesExistingManagementLink(string intent)
    {
        var result = await Read(new(intent), role: "TourOperator");
        Assert.Contains(result.Value.Actions, a => a.Type == "navigate" && a.Url == "/partner/bookings");
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.Contains("Chọn", facts.RootElement.GetProperty("guidance").GetString());
    }

    [Fact]
    public async Task ItineraryListRequiresSelectionAndDoesNotAutoReadDetail()
    {
        sender.Setup(x => x.Send(new ListMyItinerariesQuery(42, 1, 5), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ItinerarySummaryPage(
                [new(9, null, "Draft itinerary", 1, "Draft", null, null, true, 2, null)], 1, 5, 1)));
        var result = await Read(new(ChatIntents.Itinerary));
        Assert.Contains(result.Value.Actions, a => a.Type == "selectContext" && a.Context == new ChatResourceContext("itinerary", "9"));
        sender.Verify(x => x.Send(It.IsAny<GetItineraryDetailQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EligibleScheduleBeyondFirstFiveIsIncludedInBothFactsAndActions()
    {
        SetupDetail(Enumerable.Range(10, 5).Select(i => Schedule(i.ToString(), 0))
            .Append(Schedule("99", 10)).ToArray());
        var result = await Read(new(ChatIntents.PrepareBooking, ResourceId: "7", PartySize: 2));
        Assert.Contains(result.Value.Actions, a => a.Url == "/tours/7/book?scheduleId=99&adults=2");
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.Contains(facts.RootElement.GetProperty("schedules").EnumerateArray(),
            schedule => schedule.GetProperty("scheduleId").GetString() == "99");
    }

    [Fact]
    public async Task UnknownCapacityAndOverBudgetDeparturesCannotGenerateBookingActions()
    {
        SetupDetail([Schedule("10", null), Schedule("11", 10) with { AdultPrice = 200000 }, Schedule("12", 10)]);
        var result = await Read(new(ChatIntents.PrepareBooking, ResourceId: "7", PartySize: 2, MaxPrice: 150000));
        var booking = Assert.Single(result.Value.Actions, a => a.Url?.Contains("/book?") == true);
        Assert.Equal("/tours/7/book?scheduleId=12&adults=2", booking.Url);
    }

    [Fact]
    public async Task RepeatedReadsUseFreshQueryResponses()
    {
        var initial = new BookingRefundProgressResponse(1, "BK-1", 100000, "Pending", null, 0,
            DateTimeOffset.UtcNow, null, null, null);
        sender.SetupSequence(x => x.Send(new GetBookingRefundProgressQuery(1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(initial))
            .ReturnsAsync(Result.Success(initial with { Status = "Processed", RefundAmount = 90000 }));
        var first = await Read(new(ChatIntents.Refund, ResourceId: "1"));
        var second = await Read(new(ChatIntents.Refund, ResourceId: "1"));
        Assert.Contains("Pending", first.Value.Facts);
        Assert.Contains("Processed", second.Value.Facts);
        Assert.Contains("90000", second.Value.Facts);
    }

    [Fact]
    public async Task MyItinerariesProjectsFiveItemsAndReportsTheRemainingPage()
    {
        sender.Setup(x => x.Send(new ListMyItinerariesQuery(42, 1, 5), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ItinerarySummaryPage(Enumerable.Range(1, 7)
                .Select(id => new ItinerarySummaryResponse(id, null, $"Itinerary {id}", 1, "Active", null,
                    null, true, 2, null)).ToArray(), 1, 5, 7)));
        var result = await Read(new(ChatIntents.MyItineraries));
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.Equal(5, facts.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(facts.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(5, result.Value.Actions.Count(a => a.Type == "selectContext"));
    }

    [Fact]
    public async Task SelectedItineraryPreservesFiftyStopsAndVietnameseText()
    {
        var items = Enumerable.Range(1, 51).Select(id => new ItineraryDetailItemDto(id, id, id, "Đại Nội Huế",
            null, default, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 30, 10000, false,
            null, null, false, null, null, default, null, null, null)).ToArray();
        sender.Setup(x => x.Send(new GetItineraryDetailQuery(9, 42), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ItineraryDetailResponse(9, 100, "Huế", 1, "Active", null, null,
                true, 100000, 90, items)));
        var result = await Read(new(ChatIntents.Itinerary, ResourceId: "9"));
        using var facts = JsonDocument.Parse(result.Value.Facts);
        Assert.Equal(50, facts.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(facts.RootElement.GetProperty("hasMoreItems").GetBoolean());
        Assert.Contains("Đại Nội Huế", result.Value.Facts);
    }

    [Fact]
    public async Task PublicTourContentIncludesBoundedTermsAndRouteWithoutBreakingUnicode()
    {
        var description = new string('x', 1999) + "🌍" + new string('y', 100);
        var rules = Enumerable.Range(1, 7).Select(day => new TourDetailCancellationRuleDto(day, day * 10)).ToArray();
        var route = Enumerable.Range(1, 7).Select(id => new TourDetailItineraryItemDto(id,
            new(id.ToString(), $"Điểm {id}", null, 0, 0), "09:00", 30, null)).ToArray();
        sender.Setup(x => x.Send(new GetTourDetailQuery(7), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new TourDetailDto("7", "Huế", description, "Huế", ["Huế"], 2,
                100000, "VND", new("Operator", "private-phone"), [], "Bao gồm bữa trưa", "Không gồm vé máy bay",
                new(rules), [new(1, route)], [], false, new(null, 0), DateTime.UtcNow)));
        var result = await Read(new(ChatIntents.TourDetail, ResourceId: "7"));
        using var facts = JsonDocument.Parse(result.Value.Facts);
        var root = facts.RootElement;
        Assert.Equal("Bao gồm bữa trưa", root.GetProperty("inclusions").GetString());
        Assert.Equal("Không gồm vé máy bay", root.GetProperty("exclusions").GetString());
        var projectedDescription = root.GetProperty("description").GetString()!;
        Assert.True(projectedDescription.Length <= 2000);
        Assert.False(char.IsHighSurrogate(projectedDescription[^1]));
        Assert.DoesNotContain('\uFFFD', projectedDescription);
        Assert.True(root.GetProperty("descriptionTruncated").GetBoolean());
        Assert.Equal(5, root.GetProperty("cancellationRules").GetArrayLength());
        Assert.True(root.GetProperty("hasMoreCancellationRules").GetBoolean());
        Assert.Equal(10, root.GetProperty("cancellationRules")[0].GetProperty("cancellationFeePercent").GetDecimal());
        Assert.Equal(5, root.GetProperty("itinerary").GetArrayLength());
        Assert.True(root.GetProperty("hasMoreItineraryItems").GetBoolean());
        Assert.Contains("Điểm 1", result.Value.Facts);
    }

    [Fact]
    public async Task CancellationIsHonoredBeforeAnyRead()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(new(ChatIntents.General), token: cancellation.Token));
    }

    private void SetupSearch(int count)
    {
        sender.Setup(x => x.Send(It.IsAny<SearchToursQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PagedToursResponseDto(1, 5, count, 2, DateTime.UtcNow,
                Enumerable.Range(1, count).Select(id => new TourSearchItemDto(id.ToString(), $"Tour {id}",
                    ["Huế"], "Operator", 2, 100000, "VND", "10", DateTime.UtcNow, "available", 5,
                    "private-thumbnail", null)).ToArray())));
    }

    private void SetupDetail(IReadOnlyList<TourDetailScheduleDto> schedules)
    {
        sender.Setup(x => x.Send(new GetTourDetailQuery(7), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new TourDetailDto("7", "Huế", "Description", "Huế", ["Huế"], 2,
                100000, "VND", new("Operator", "private-phone"), [], null, null, new([]), [], schedules,
                true, new(null, 0), DateTime.UtcNow)));
    }

    private void SetupTrips(IReadOnlyCollection<TripHistoryItemResponse> items)
    {
        sender.Setup(x => x.Send(It.IsAny<ListTravelerTripsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ListTravelerTripsQuery query, CancellationToken _) => Result.Success(new TripHistoryPage(
                query.Tab == ListTravelerTripsQuery.TabCancelled ? items : [], null, 1, 5,
                query.Tab == ListTravelerTripsQuery.TabCancelled ? items.Count : 0)));
    }

    private static TourDetailScheduleDto Schedule(string id, int? slots, bool bookable = true) =>
        new(id, new DateTime(2027, 1, 2, 2, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 3), null,
            null, 100000, null, null, slots, "available", bookable);

    private static TripHistoryItemResponse Trip(long id, bool refund) => new()
    {
        Id = id,
        Kind = TripRecordKinds.CommerceBooking,
        TripType = ListTravelerTripsQuery.TripTypeTourBooking,
        Tab = ListTravelerTripsQuery.TabCancelled,
        Title = $"Trip {id}",
        DepartureAtUtc = DateTimeOffset.UtcNow,
        Status = "Cancelled",
        ReviewableRecord = null,
        Review = null,
        Actions = new(false, false, null, refund),
    };
}