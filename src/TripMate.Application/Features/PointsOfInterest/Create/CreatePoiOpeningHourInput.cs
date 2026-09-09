namespace TripMate.Application.Features.PointsOfInterest.Create;

public sealed record CreatePoiOpeningHourInput(
    int? DayOfWeek,
    TimeOnly? OpenTime,
    TimeOnly? CloseTime,
    bool IsClosed);
