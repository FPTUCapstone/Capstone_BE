using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Departures.Add;

public sealed record AddTourDepartureRequest(
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint,
    int TotalCapacity,
    int MinimumCapacity = 1,
    DateTimeOffset? RegistrationDeadlineAtUtc = null,
    decimal? AdultPrice = null,
    decimal? ChildPrice = null,
    decimal? InfantPrice = null,
    decimal? MeetingLatitude = null,
    decimal? MeetingLongitude = null);

public sealed record AddTourDepartureCommand(
    long TourId,
    Guid IdempotencyKey,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint,
    int TotalCapacity,
    int MinimumCapacity = 1,
    DateTimeOffset? RegistrationDeadlineAtUtc = null,
    decimal? AdultPrice = null,
    decimal? ChildPrice = null,
    decimal? InfantPrice = null,
    decimal? MeetingLatitude = null,
    decimal? MeetingLongitude = null)
    : IRequest<Result<AddTourDepartureResponse>>;

public sealed record AddTourDepartureResponse(
    long TourId,
    long ScheduleId,
    string Status,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint,
    int TotalCapacity,
    int MinimumCapacity,
    int ReservedCapacity,
    DateTimeOffset? RegistrationDeadlineAtUtc,
    decimal AdultPrice,
    decimal? ChildPrice,
    decimal? InfantPrice,
    decimal? MeetingLatitude,
    decimal? MeetingLongitude,
    bool IsReplay = false);