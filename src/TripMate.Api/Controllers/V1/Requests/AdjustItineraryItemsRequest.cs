namespace TripMate.Api.Controllers.V1.Requests;

public sealed record AdjustItineraryItemsRequest(IReadOnlyCollection<long> OrderedVisitPoiIds);