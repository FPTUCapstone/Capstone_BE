using System.Data.Common;
using System.Text.Json;

using MediatR;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.SupportChat;

namespace TripMate.Application.UnitTests.Features.SupportChat;

public sealed class SupportChatUnifiedHandlerTests
{
    private readonly Mock<ICurrentUserService> user = new();
    private readonly Mock<ISender> sender = new();
    private readonly Mock<ISupportChatKnowledge> knowledge = new();
    private readonly Mock<ISupportChatModel> model = new();
    private readonly Mock<ISupportChatPlanner> planner = new();
    private readonly Mock<ISupportChatLiveDataReader> live = new();

    public SupportChatUnifiedHandlerTests()
    {
        user.SetupGet(value => value.UserId).Returns(42);
        user.SetupGet(value => value.Role).Returns("Traveler");
        knowledge.Setup(value => value.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<KnowledgePassage>());
        planner.Setup(value => value.PlanAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatHistoryMessage>>(),
            It.IsAny<ChatResourceContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatQueryPlan(ChatIntents.SearchTours));
        model.Setup(value => value.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModelAnswer("Tour hiện tại có giá 500.000 VND/người lớn.", []));
    }

    private SendSupportChatMessageHandler Handler() => new(user.Object, sender.Object, knowledge.Object,
        model.Object, planner.Object, live.Object);

    [Fact]
    public async Task LiveDataAnswersWithoutStaticDocumentMatchAndReturnsServerActions()
    {
        var action = new ChatAction("navigate", "Xem tour", "/tours/7");
        live.Setup(value => value.ReadAsync(It.IsAny<ChatQueryPlan>(), 42, "Traveler", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ChatLiveData("{\"tourId\":\"7\",\"adultPrice\":500000}", [action])));
        var result = await Handler().Handle(new("Bạn có gợi ý tour nào không?"), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(action, Assert.Single(result.Value.Actions!));
        Assert.Null(result.Value.Context);
        Assert.Empty(result.Value.Sources);
        model.Verify(value => value.AnswerAsync(It.Is<ChatGrounding>(grounding =>
            grounding.ResourceData!.Contains("500000") && grounding.AccountRole == "Traveler"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrivateContextIsAuthorizedBeforePlanningOrAnyLiveQuery()
    {
        sender.Setup(value => value.Send(It.IsAny<GetBookingRefundProgressQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<BookingRefundProgressResponse>(BookingCancellationErrorCodes.Forbidden, "Forbidden"));
        var result = await Handler().Handle(new("Xem refund", Context: new("refund", "9")), default);
        Assert.Equal(BookingCancellationErrorCodes.Forbidden, result.ErrorCode);
        planner.VerifyNoOtherCalls();
        live.VerifyNoOtherCalls();
        model.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlannedPrivateResourceDenialCannotReachAnswerGeneration()
    {
        planner.Setup(value => value.PlanAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatHistoryMessage>>(),
            null, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatQueryPlan(ChatIntents.Refund, ResourceId: "999"));
        live.Setup(value => value.ReadAsync(It.IsAny<ChatQueryPlan>(), 42, "Traveler", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<ChatLiveData>(BookingCancellationErrorCodes.Forbidden, "Forbidden"));
        var result = await Handler().Handle(new("Xem booking 999"), default);
        Assert.Equal(BookingCancellationErrorCodes.Forbidden, result.ErrorCode);
        model.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadOnlyBookingPreparationPassesOnlyAuthenticatedIdentityAndNeverExecutesMutation()
    {
        planner.Setup(value => value.PlanAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatHistoryMessage>>(),
            null, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatQueryPlan(ChatIntents.PrepareBooking, Search: "Đà Lạt", PartySize: 2));
        live.Setup(value => value.ReadAsync(It.Is<ChatQueryPlan>(plan => plan.PartySize == 2), 42, "Traveler", null,
            It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success(new ChatLiveData("Review booking form",
                [new("navigate", "Review booking", "/tours/7/book?scheduleId=8&adults=2")])));
        var result = await Handler().Handle(new("Đặt tour Đà Lạt cho 2 người"), default);
        Assert.True(result.IsSuccess);
        Assert.Contains("adults=2", Assert.Single(result.Value.Actions!).Url);
        sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GeneralQuestionsStillRequireGroundedEvidence()
    {
        planner.Setup(value => value.PlanAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatHistoryMessage>>(),
            null, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatQueryPlan(ChatIntents.General));
        var result = await Handler().Handle(new("Bịa chính sách"), default);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Sources);
        live.VerifyNoOtherCalls();
        model.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnavailableDatabaseProducesRetryableFailureWithoutModelCall()
    {
        live.Setup(value => value.ReadAsync(It.IsAny<ChatQueryPlan>(), 42, "Traveler", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnavailableDatabaseException());
        var result = await Handler().Handle(new("Tìm tour"), default);
        Assert.Equal(SupportChatErrors.Unavailable, result.ErrorCode);
        model.VerifyNoOtherCalls();
    }

    [Fact]
    public void ActionsSerializeOnlyTheirApplicableFieldsAndNoActionsRemainOptional()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var response = new SupportChatResponse("Choose a tour", [], null,
            [new("navigate", "View tour", "/tours/7"),
             new("selectContext", "Ask about tour", Context: new("tour", "7"), Prompt: "Xem tour này")]);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, options));
        var actions = document.RootElement.GetProperty("actions");
        Assert.Equal(3, actions[0].EnumerateObject().Count());
        Assert.False(actions[0].TryGetProperty("context", out _));
        Assert.False(actions[0].TryGetProperty("prompt", out _));
        Assert.Equal(4, actions[1].EnumerateObject().Count());
        Assert.False(actions[1].TryGetProperty("url", out _));
        using var noActions = JsonDocument.Parse(JsonSerializer.Serialize(new SupportChatResponse("Help", [], null), options));
        Assert.False(noActions.RootElement.TryGetProperty("actions", out _));
    }

    private sealed class UnavailableDatabaseException : DbException;
}