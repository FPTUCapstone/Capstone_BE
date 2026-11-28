namespace TripMate.Api.Controllers.V1.Requests;

public sealed record SendGroupMessageRequest(
    string MessageType,
    string Content,
    string? MetadataJson = null,
    Guid? ClientMessageId = null);

public sealed record TogglePaymentRequest(bool IsPaid, long? ExpectedRevision = null);

public sealed record VotePollRequest(int OptionIndex, long? ExpectedRevision = null);
public sealed record MarkGroupChatReadRequest(long ThroughMessageId);