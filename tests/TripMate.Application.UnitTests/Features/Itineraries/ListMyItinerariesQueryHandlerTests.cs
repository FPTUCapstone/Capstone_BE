using FluentAssertions;

using TripMate.Application.Features.Itineraries.List;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class ListMyItinerariesQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_PopulatesCreatedAtTransportModeAndTopThreeVisitPreviewStops()
    {
        await using var dbContext = TestDbContext.Create();
        const long travelerUserId = 42;
        var category = PoiCategory.Create("Heritage", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();

        var stop1 = PointOfInterest.Create(category, "Bảo tàng Chăm", 16.0601m, 108.2229m, 1, Now);
        var stop2 = PointOfInterest.Create(category, "Cầu Rồng", 16.0611m, 108.2272m, 1, Now);
        var stop3 = PointOfInterest.Create(category, "Chùa Linh Ứng", 16.1000m, 108.2770m, 1, Now);
        var stop4 = PointOfInterest.Create(category, "Bãi biển Mỹ Khê", 16.0592m, 108.2464m, 1, Now);
        var restCafe = PointOfInterest.Create(category, "Quán nghỉ chân", 16.0620m, 108.2250m, 1, Now);
        dbContext.PointsOfInterest.AddRange(stop1, stop2, stop3, stop4, restCafe);
        await dbContext.SaveChangesAsync();

        var request = CreateRequest(travelerUserId, TransportMode.Car, Now.AddMinutes(-15));
        dbContext.SchedulingRequests.Add(request);
        await dbContext.SaveChangesAsync();

        var generated = Itinerary.CreateCspGenerated(
            request,
            "Generated itinerary - 24 Nov 2026",
            Now.AddHours(2),
            Now.AddHours(8));
        generated.AddItem(ItineraryItem.CreateVisit(1, stop1.Id, Now.AddHours(2), Now.AddHours(3), true, 60_000m, null));
        generated.AddItem(ItineraryItem.CreateRest(2, Now.AddHours(3), Now.AddHours(3.5), "Nghỉ tự do"));
        generated.AddItem(ItineraryItem.CreateRest(3, restCafe.Id, Now.AddHours(3.5), Now.AddHours(4), "Nghỉ uống nước"));
        generated.AddItem(ItineraryItem.CreateVisit(4, stop2.Id, Now.AddHours(4), Now.AddHours(5), false, 0m, null));
        generated.AddItem(ItineraryItem.CreateVisit(5, stop3.Id, Now.AddHours(5), Now.AddHours(6), false, 0m, null));
        generated.AddItem(ItineraryItem.CreateVisit(6, stop4.Id, Now.AddHours(6), Now.AddHours(7), false, 0m, null));

        var manualTime = Now.AddHours(-2);
        var manual = Itinerary.CreateManual(travelerUserId, "Lịch tự tạo", Itinerary.DraftStatus, manualTime);

        dbContext.Itineraries.AddRange(generated, manual);
        await dbContext.SaveChangesAsync();

        var handler = new ListMyItinerariesQueryHandler(dbContext, new FakeDateTimeProvider { UtcNow = Now });
        var result = await handler.Handle(
            new ListMyItinerariesQuery(travelerUserId, 1, 20),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var generatedSummary = result.Value.Items.Single(item => item.ItineraryId == generated.Id);
        generatedSummary.CreatedAt.Should().Be(request.RequestedAtUtc);
        generatedSummary.TransportMode.Should().Be(TransportMode.Car);
        generatedSummary.StopCount.Should().Be(4);
        generatedSummary.PreviewStopNames.Should().Equal("Bảo tàng Chăm", "Cầu Rồng", "Chùa Linh Ứng");

        var manualSummary = result.Value.Items.Single(item => item.ItineraryId == manual.Id);
        manualSummary.CreatedAt.Should().Be(manualTime);
        manualSummary.TransportMode.Should().BeNull();
        manualSummary.PreviewStopNames.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_SortNewestPutsNewestDraftBeforeUpcomingActiveWhilePriorityPreservesPriorityOrder()
    {
        await using var dbContext = TestDbContext.Create();
        const long travelerUserId = 42;

        var activeReq = CreateRequest(travelerUserId, TransportMode.Motorbike, Now.AddMinutes(-30));
        var draftReq = CreateRequest(travelerUserId, TransportMode.Walking, Now.AddMinutes(-5));
        dbContext.SchedulingRequests.AddRange(activeReq, draftReq);
        await dbContext.SaveChangesAsync();

        var upcomingActive = Itinerary.CreateCspGenerated(
            activeReq,
            "Active Trip",
            Now.AddHours(4),
            Now.AddHours(8));
        upcomingActive.Accept(Now);

        var newestDraft = Itinerary.CreateCspGenerated(
            draftReq,
            "Newest Draft",
            Now.AddDays(2),
            Now.AddDays(2).AddHours(4));

        dbContext.Itineraries.Add(upcomingActive);
        await dbContext.SaveChangesAsync();
        dbContext.Itineraries.Add(newestDraft);
        await dbContext.SaveChangesAsync();

        var handler = new ListMyItinerariesQueryHandler(dbContext, new FakeDateTimeProvider { UtcNow = Now });

        var priorityResult = await handler.Handle(
            new ListMyItinerariesQuery(travelerUserId, 1, 20),
            CancellationToken.None);
        var explicitPriorityResult = await handler.Handle(
            new ListMyItinerariesQuery(travelerUserId, 1, 20, ListMyItinerariesQuery.PrioritySort),
            CancellationToken.None);
        var newestResult = await handler.Handle(
            new ListMyItinerariesQuery(travelerUserId, 1, 20, ListMyItinerariesQuery.NewestSort),
            CancellationToken.None);

        priorityResult.IsSuccess.Should().BeTrue();
        priorityResult.Value.Items.Select(item => item.ItineraryId)
            .Should().Equal(upcomingActive.Id, newestDraft.Id);

        explicitPriorityResult.IsSuccess.Should().BeTrue();
        explicitPriorityResult.Value.Items.Select(item => item.ItineraryId)
            .Should().Equal(upcomingActive.Id, newestDraft.Id);

        newestResult.IsSuccess.Should().BeTrue();
        newestResult.Value.Items.Select(item => item.ItineraryId)
            .Should().Equal(newestDraft.Id, upcomingActive.Id);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("priority", true)]
    [InlineData("newest", true)]
    [InlineData("NEWEST", true)]
    [InlineData("Priority", true)]
    [InlineData("random", false)]
    public void Validator_ValidatesSortParameter(string? sort, bool expectedValid)
    {
        var validator = new ListMyItinerariesQueryValidator();
        var result = validator.Validate(new ListMyItinerariesQuery(1, 1, 20, sort));

        result.IsValid.Should().Be(expectedValid);
    }

    private static SchedulingRequest CreateRequest(
        long travelerUserId,
        TransportMode transportMode,
        DateTimeOffset requestedAtUtc)
    {
        var request = SchedulingRequest.Create(
            travelerUserId,
            Guid.NewGuid(),
            new string('a', SchedulingRequest.RequestHashLength),
            requestedAtUtc.AddHours(2),
            "Asia/Ho_Chi_Minh",
            16.0544m,
            108.2022m,
            16.0544m,
            108.2022m,
            null,
            true,
            360,
            transportMode,
            10m,
            500_000m,
            "[]",
            RestPreference.None,
            requestedAtUtc);
        var owner = Guid.NewGuid();
        request.ClaimGeneration(owner, requestedAtUtc.AddMinutes(1), requestedAtUtc);
        request.CompleteGeneration(owner, requestedAtUtc);
        return request;
    }
}