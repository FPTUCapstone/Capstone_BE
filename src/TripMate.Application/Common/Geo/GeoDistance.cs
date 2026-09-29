namespace TripMate.Application.Common.Geo;

public static class GeoDistance
{
    public const decimal LatitudeKilometersPerDegree = 110.574m;
    public const double LongitudeKilometersPerDegreeAtEquator = 111.320d;

    public static decimal EquirectangularKilometers(
        decimal firstLatitude,
        decimal firstLongitude,
        decimal secondLatitude,
        decimal secondLongitude)
    {
        var longitudeKilometersPerDegree = LongitudeKilometersPerDegree(firstLatitude);
        var latitudeDistance = (secondLatitude - firstLatitude) * LatitudeKilometersPerDegree;
        var longitudeDistance = (secondLongitude - firstLongitude) * longitudeKilometersPerDegree;
        return (decimal)Math.Sqrt(
            Math.Pow((double)latitudeDistance, 2)
            + Math.Pow((double)longitudeDistance, 2));
    }

    public static decimal LongitudeKilometersPerDegree(decimal latitude)
    {
        var cosine = Math.Abs(Math.Cos(DegreesToRadians((double)latitude)));
        return (decimal)(LongitudeKilometersPerDegreeAtEquator * cosine);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}