using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Chat;

namespace TripMate.Application.Features.ItineraryShares;

public static class ItineraryShareErrors
{
    public const string Invalid = "ItineraryShares.Invalid";
    public const string NotFound = "ItineraryShares.NotFound";
    public const string Forbidden = "ItineraryShares.Forbidden";
    public const string Conflict = "ItineraryShares.Conflict";
}

public sealed record ShareItinerary(long ActorId, long ItineraryId, string? ConversationKind,
    long ConversationId, Guid ClientRequestId) : IRequest<Result<ItineraryShareSent>>;
public sealed record GetItineraryShare(long ActorId, Guid ShareId) : IRequest<Result<ItineraryShareDto>>;
public sealed record ItineraryShareReceipt(Guid ShareId, long MessageId);
// Internal application result. Only Receipt is returned by the HTTP endpoint.
public sealed record ItineraryShareSent(ItineraryShareReceipt Receipt, bool Created, GroupMessageDto? TravelMessage);
public sealed record ItineraryShareItemDto(int SequenceNo, string Name, DateTimeOffset? ArrivalUtc,
    DateTimeOffset? DepartureUtc, int StayDurationMinutes, decimal? Latitude, decimal? Longitude, string? Address);
public sealed record ItineraryShareDto(Guid ShareId, string Title, DateTimeOffset SharedAtUtc,
    int ItineraryVersion, IReadOnlyList<ItineraryShareItemDto> Items);