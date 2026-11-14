using System.Text.Json.Serialization;

using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.SupportChat;

public sealed record ChatHistoryMessage(string Role, string Content);
public sealed record ChatResourceContext(string Kind, string Id);
public sealed record ChatSource(string Id, string Title, string Url);
public sealed record KnowledgePassage(ChatSource Source, string Content,
    [property: JsonIgnore] string? RetrievalText = null);
public sealed record ChatModelAnswer(string Answer, IReadOnlyList<string> SourceIds);
public sealed record ChatGrounding(string Message, IReadOnlyList<ChatHistoryMessage> History,
    IReadOnlyList<KnowledgePassage> Knowledge, string? ResourceData, string? AccountRole = null);
public sealed record ChatAction(string Type, string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatResourceContext? Context = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Prompt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null);
public sealed record SupportChatResponse(string Answer, IReadOnlyList<ChatSource> Sources, ChatResourceContext? Context,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ChatAction>? Actions = null);
public sealed record SendSupportChatMessage(string Message, IReadOnlyList<ChatHistoryMessage>? History = null,
    ChatResourceContext? Context = null) : IRequest<Result<SupportChatResponse>>;

public interface ISupportChatKnowledge
{
    Task<IReadOnlyList<KnowledgePassage>> SearchAsync(string question, CancellationToken cancellationToken);
}

public interface ISupportChatModel
{
    Task<ChatModelAnswer?> AnswerAsync(ChatGrounding grounding, CancellationToken cancellationToken);
}

public static class ChatIntents
{
    public const string General = "general";
    public const string SearchTours = "searchTours";
    public const string TourDetail = "tourDetail";
    public const string PrepareBooking = "prepareBooking";
    public const string MyBookings = "myBookings";
    public const string MyItineraries = "myItineraries";
    public const string Refund = "refund";
    public const string Itinerary = "itinerary";
    public static readonly string[] All = [General, SearchTours, TourDetail, PrepareBooking,
        MyBookings, MyItineraries, Refund, Itinerary];
}

public sealed record ChatQueryPlan(string Intent, string? Search = null, string? Destination = null,
    string? ResourceId = null, string? DepartureDate = null, long? MaxPrice = null, int? PartySize = null,
    int? ChildCount = null, int? InfantCount = null)
{
    // PartySize retains the adult count for compatibility with existing plans.
    public long? RequestedParticipants => PartySize.HasValue
        ? (long)PartySize.Value + (ChildCount ?? 0) + (InfantCount ?? 0) : null;
}

public interface ISupportChatPlanner
{
    Task<ChatQueryPlan> PlanAsync(string message, IReadOnlyList<ChatHistoryMessage> history,
        ChatResourceContext? context, CancellationToken cancellationToken);
}

public sealed record ChatLiveData(string Facts, IReadOnlyList<ChatAction> Actions);

public interface ISupportChatLiveDataReader
{
    Task<Result<ChatLiveData>> ReadAsync(ChatQueryPlan plan, long userId, string role,
        ChatResourceContext? context, CancellationToken cancellationToken);
}

public sealed class SupportChatUnavailableException : Exception;

public static class SupportChatErrors
{
    public const string Unauthorized = "support_chat.unauthorized";
    public const string Forbidden = "support_chat.forbidden";
    public const string Unavailable = "support_chat.unavailable";
    public const string InvalidRequest = "support_chat.invalid_request";
}