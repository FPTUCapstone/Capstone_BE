namespace TripMate.Api.Controllers.V1.Requests;

public sealed record SendGroupMessageRequest(
    string MessageType,
    string Content,
    string? MetadataJson = null);

public sealed record TogglePaymentRequest(bool IsPaid);

public sealed record VotePollRequest(int OptionIndex);