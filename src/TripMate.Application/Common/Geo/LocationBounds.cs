namespace TripMate.Application.Common.Geo;

internal sealed record LocationBounds(
    decimal MinimumLatitude,
    decimal MaximumLatitude,
    decimal MinimumLongitude,
    decimal MaximumLongitude)
{
    public static LocationBounds From(decimal latitude, decimal longitude, int radiusKm)
    {
        var latitudeDelta = radiusKm / GeoDistance.LatitudeKilometersPerDegree;
        var cosine = Math.Abs(Math.Cos(DegreesToRadians((double)latitude)));
        var longitudeDelta = cosine < 0.000001d
            ? 180m
            : Math.Min(
                180m,
                (decimal)(radiusKm
                    / (GeoDistance.LongitudeKilometersPerDegreeAtEquator * cosine)));
        return new LocationBounds(
            Math.Max(-90m, latitude - latitudeDelta),
            Math.Min(90m, latitude + latitudeDelta),
            Math.Max(-180m, longitude - longitudeDelta),
            Math.Min(180m, longitude + longitudeDelta));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}