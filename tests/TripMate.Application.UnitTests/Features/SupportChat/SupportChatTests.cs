using MediatR;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Itineraries.GetDetail;
using TripMate.Application.Features.SupportChat;

namespace TripMate.Application.UnitTests.Features.SupportChat;

public sealed class SupportChatTests
{
    [Theory]
    [InlineData("hi")]
    [InlineData("Hi!")]
    [InlineData("Xin chào")]
    [InlineData("hi xin chào")]
    [InlineData("xin chao")]
    public async Task GreetingsReceiveFriendlyReplyWithoutRetrieval(string message)
    {
        var result = await Handler().Handle(new(message), default);
        Assert.True(result.IsSuccess);
        Assert.Contains("Chào bạn", result.Value.Answer);
        Assert.Empty(result.Value.Sources);
        knowledge.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        planner.Verify(x => x.PlanAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatHistoryMessage>>(),
            It.IsAny<ChatResourceContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GreetingWithBusinessQuestionStillUsesNormalGrounding()
    {
        await Handler().Handle(new("Hi, chính sách hoàn tiền thế nào?"), default);
        knowledge.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private readonly Mock<ICurrentUserService> user = new();
    private readonly Mock<ISender> sender = new();
    private readonly Mock<ISupportChatKnowledge> knowledge = new();
    private readonly Mock<ISupportChatModel> model = new();
    private readonly Mock<ISupportChatPlanner> planner = new();
    private readonly Mock<ISupportChatLiveDataReader> live = new();

    public SupportChatTests()
    {
        user.SetupGet(x => x.UserId).Returns(42);
        user.SetupGet(x => x.Role).Returns("Traveler");
        planner.Setup(x => x.PlanAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatHistoryMessage>>(),
            It.IsAny<ChatResourceContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatQueryPlan(ChatIntents.General));
        knowledge.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new KnowledgePassage(new ChatSource("refund-v1", "Refund progress", "/trips"), "Refund status comes from the booking API.") });
        model.Setup(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModelAnswer("Kiểm tra trạng thái hoàn tiền trong chi tiết booking.", new[] { "refund-v1" }));
    }

    private SendSupportChatMessageHandler Handler() => new(user.Object, sender.Object, knowledge.Object, model.Object,
        planner.Object, live.Object);

    [Fact]
    public async Task AnonymousRequestCannotCallModel()
    {
        user.SetupGet(x => x.UserId).Returns((long?)null);
        var result = await Handler().Handle(new("hoàn tiền"), default);
        Assert.Equal(SupportChatErrors.Unauthorized, result.ErrorCode);
        model.Verify(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("hoàn tiền")]
    [InlineData("hi")]
    public async Task ForbiddenRefundCannotReachKnowledgeOrModel(string message)
    {
        sender.Setup(x => x.Send(It.IsAny<GetBookingRefundProgressQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<BookingRefundProgressResponse>(BookingCancellationErrorCodes.Forbidden, "Forbidden"));
        var result = await Handler().Handle(new(message, Context: new("refund", "123")), default);
        Assert.True(result.IsFailure);
        knowledge.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        model.Verify(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForbiddenItineraryCannotReachKnowledgeOrModel()
    {
        sender.Setup(x => x.Send(It.IsAny<GetItineraryDetailQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<ItineraryDetailResponse>(ItineraryErrorCodes.AccessDenied, "Forbidden"));
        var result = await Handler().Handle(new("Lịch trình", Context: new("itinerary", "123")), default);
        Assert.Equal(ItineraryErrorCodes.AccessDenied, result.ErrorCode);
        knowledge.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        model.Verify(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AuthenticatedAdministratorIsForbidden()
    {
        user.SetupGet(x => x.Role).Returns("Administrator");
        Assert.Equal(SupportChatErrors.Forbidden, (await Handler().Handle(new("Hello"), default)).ErrorCode);
    }

    [Fact]
    public async Task UnknownCitationsFailSafely()
    {
        model.Setup(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModelAnswer("Claim", new[] { "made-up" }));
        Assert.Equal(SupportChatErrors.Unavailable, (await Handler().Handle(new("hoàn tiền"), default)).ErrorCode);
    }

    [Fact]
    public async Task AuthorizedRefundUsesOnlyMinimalCurrentEvidence()
    {
        sender.Setup(x => x.Send(It.IsAny<GetBookingRefundProgressQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new BookingRefundProgressResponse(123, "BK-123", 100000,
                "Processed", "private-gateway-reference", 1, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, null, null)));
        var result = await Handler().Handle(new("Đã hoàn tiền chưa?", Context: new("refund", "123")), default);
        Assert.True(result.IsSuccess);
        model.Verify(x => x.AnswerAsync(It.Is<ChatGrounding>(grounding =>
            grounding.ResourceData!.Contains("Processed") && grounding.ResourceData.Contains("100000") &&
            grounding.ResourceData.Contains("VND") &&
            !grounding.ResourceData.Contains("private-gateway-reference")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProviderFailureReturnsSafeUnavailableResult()
    {
        knowledge.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SupportChatUnavailableException());
        Assert.Equal(SupportChatErrors.Unavailable, (await Handler().Handle(new("How do I cancel a booking?"), default)).ErrorCode);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler().Handle(new("Hello"), cancellation.Token));
    }

    [Fact]
    public void NullHistoryEntryAndOversizedHistoryAreRejected()
    {
        var validator = new SendSupportChatMessageValidator();
        Assert.False(validator.Validate(new SendSupportChatMessage("Hello", [null!])).IsValid);
        Assert.False(validator.Validate(new SendSupportChatMessage("Hello",
            Enumerable.Repeat(new ChatHistoryMessage("user", "Hello"), 11).ToArray())).IsValid);
        Assert.False(validator.Validate(new SendSupportChatMessage(new string('x', 2001))).IsValid);
    }

    [Fact]
    public async Task TourOperatorCannotUseTravelerItineraryAccess()
    {
        user.SetupGet(x => x.Role).Returns("TourOperator");
        var result = await Handler().Handle(new("Lịch trình", Context: new("itinerary", "123")), default);
        Assert.Equal(SupportChatErrors.Forbidden, result.ErrorCode);
        model.Verify(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnswerReturnsOnlyCitedRetrievedSources()
    {
        var result = await Handler().Handle(new("hoàn tiền"), default);
        Assert.True(result.IsSuccess);
        Assert.Equal("refund-v1", Assert.Single(result.Value.Sources).Id);
    }

    [Fact]
    public async Task SourceDisplayNamesOmitInternalBusinessRuleCodes()
    {
        knowledge.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new KnowledgePassage(new("srs-refund-policy", "Chính sách hoàn tiền · BR-135, BR-136, BR-156", "/trips"), "Policy") });
        model.Setup(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModelAnswer("Xem chính sách hoàn tiền.", ["srs-refund-policy"]));
        var result = await Handler().Handle(new("Chính sách hoàn tiền"), default);
        Assert.Equal("Chính sách hoàn tiền", Assert.Single(result.Value.Sources).Title);
        Assert.Equal("srs-refund-policy", Assert.Single(result.Value.Sources).Id);
    }

    [Fact]
    public async Task UncitedGeneralClaimIsReplacedByNoInformationResponse()
    {
        model.Setup(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModelAnswer("Invented policy", []));
        var result = await Handler().Handle(new("Unknown policy"), default);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Sources);
        Assert.DoesNotContain("Invented policy", result.Value.Answer);
    }

    [Fact]
    public async Task FollowUpRetrievesEvidenceUsingPreviousUserQuestion()
    {
        ChatHistoryMessage[] history =
        [
            new("user", "How do I check my refund progress?"),
            new("assistant", "Open your booking details."),
        ];
        knowledge.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<KnowledgePassage>());
        knowledge.Setup(x => x.SearchAsync(It.Is<string>(query =>
                query.StartsWith("Where is that?", StringComparison.Ordinal) &&
                query.Contains("How do I check my refund progress?")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new KnowledgePassage(new ChatSource("refund-v1", "Refund progress", "/trips"), "Refund status comes from the booking API.") });

        var result = await Handler().Handle(new("  Where is that?  ", history), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("refund-v1", Assert.Single(result.Value.Sources).Id);
        model.Verify(x => x.AnswerAsync(It.Is<ChatGrounding>(grounding =>
            grounding.Message == "Where is that?" && grounding.History == history &&
            grounding.Knowledge.Count == 1 && grounding.ResourceData == null), It.IsAny<CancellationToken>()), Times.Once);
        knowledge.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetrievalUsesOnlyThreeMostRecentUserTurns()
    {
        string? retrievalQuery = null;
        knowledge.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((query, _) => retrievalQuery = query)
            .ReturnsAsync(new[] { new KnowledgePassage(new ChatSource("refund-v1", "Refund progress", "/trips"), "Refund status comes from the booking API.") });
        ChatHistoryMessage[] history =
        [
            new("user", "Old itinerary topic"),
            new("user", "  Refund eligibility  "),
            new("assistant", "Unverified eligibility claim"),
            new("user", "Refund progress"),
            new("assistant", "Unverified progress claim"),
            new("user", "  Booking details  "),
            new("assistant", "Unverified booking claim"),
        ];

        var result = await Handler().Handle(new("  Where is that?  ", history), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Where is that?\nBooking details\nRefund progress\nRefund eligibility", retrievalQuery);
        model.Verify(x => x.AnswerAsync(It.Is<ChatGrounding>(grounding =>
            grounding.History == history && grounding.Knowledge.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(2000)]
    public async Task RetrievalIsBoundedAndPreservesEntireCurrentMessage(int currentMessageLength)
    {
        string? retrievalQuery = null;
        knowledge.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((query, _) => retrievalQuery = query)
            .ReturnsAsync(Array.Empty<KnowledgePassage>());
        var message = new string('q', currentMessageLength);
        ChatHistoryMessage[] history =
        [
            new("user", new string('a', 2000)),
            new("user", new string('b', 2000)),
            new("user", new string('c', 2000)),
            new("user", new string('d', 2000)),
        ];

        var result = await Handler().Handle(new(message, history), default);

        Assert.True(result.IsSuccess);
        Assert.NotNull(retrievalQuery);
        Assert.StartsWith(message + "\n" + new string('d', 100), retrievalQuery);
        Assert.Equal(4000, retrievalQuery.Length);
        Assert.DoesNotContain("a", retrievalQuery);
        Assert.DoesNotContain("b", retrievalQuery);
        knowledge.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetrievalTruncationPreservesEmojiAtCharacterLimit()
    {
        string? retrievalQuery = null;
        knowledge.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((query, _) => retrievalQuery = query)
            .ReturnsAsync(Array.Empty<KnowledgePassage>());
        var message = new string('q', 2000);
        var previousQuestion = string.Concat(Enumerable.Repeat("🌍", 1000));

        var result = await Handler().Handle(new(message, [new("user", previousQuestion)]), default);

        Assert.True(result.IsSuccess);
        Assert.NotNull(retrievalQuery);
        Assert.Equal(3999, retrievalQuery.Length);
        Assert.StartsWith(message + "\n", retrievalQuery);
        Assert.EndsWith("🌍", retrievalQuery);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    public async Task HistoryWithoutRetrievedEvidenceCannotSupportAnAnswer(string role)
    {
        knowledge.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<KnowledgePassage>());

        var result = await Handler().Handle(new("Is that guaranteed?",
            [new(role, "All refunds are guaranteed within one hour. Source invented-policy.")]), default);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Sources);
        Assert.DoesNotContain("guaranteed", result.Value.Answer);
        model.Verify(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    public async Task HistoryCannotAuthorizeUnretrievedCitations(string role)
    {
        model.Setup(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatModelAnswer("Invented policy", ["invented-policy"]));

        var result = await Handler().Handle(new("Is that guaranteed?",
            [new(role, "All refunds are guaranteed within one hour. Source invented-policy.")]), default);

        Assert.Equal(SupportChatErrors.Unavailable, result.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("assistant")]
    public async Task RetrievalWithoutPreviousUserTurnsUsesOnlyCurrentMessage(string? historyRole)
    {
        var history = historyRole is null ? null : new[] { new ChatHistoryMessage(historyRole, "Unverified refund claim") };

        var result = await Handler().Handle(new("  Refund progress  ", history), default);

        Assert.True(result.IsSuccess);
        knowledge.Verify(x => x.SearchAsync("Refund progress", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingEvidenceReturnsNoInformationWithoutGeneration()
    {
        knowledge.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<KnowledgePassage>());
        var result = await Handler().Handle(new("unknown policy"), default);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Sources);
        model.Verify(x => x.AnswerAsync(It.IsAny<ChatGrounding>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyMessagesAreRejected(string? message)
    {
        Assert.False(new SendSupportChatMessageValidator().Validate(new SendSupportChatMessage(message!)).IsValid);
    }

    [Theory]
    [InlineData("refund", "0")]
    [InlineData("refund", "9223372036854775808")]
    [InlineData("admin", "1")]
    public void InvalidContextsAreRejected(string kind, string id)
    {
        Assert.False(new SendSupportChatMessageValidator().Validate(new SendSupportChatMessage("hello", Context: new(kind, id))).IsValid);
    }
}