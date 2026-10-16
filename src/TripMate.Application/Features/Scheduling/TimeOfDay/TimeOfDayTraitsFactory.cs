using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.TimeOfDay;

public static class TimeOfDayTraitsFactory
{
    /// <summary>Traits of loaded places; needs <c>Category</c> and <c>PoiTags.Tag</c> included.</summary>
    public static IReadOnlyDictionary<long, TimeOfDayTraits> From(IEnumerable<PointOfInterest> places) =>
        places
            .GroupBy(poi => poi.Id)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var poi = group.First();
                    return new TimeOfDayTraits(
                        poi.IndoorOutdoor,
                        poi.HasShelter,
                        poi.Category?.Name,
                        poi.ScenicScore,
                        poi.PoiTags.Select(mapping => mapping.Tag?.Name).OfType<string>().ToArray());
                });
}