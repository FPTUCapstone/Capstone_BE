using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TripMate.Infrastructure.Persistence.Common;

/// <summary>
/// Every timestamp column in database/tripmate_schema_v6.sql is DATETIME2, which carries no
/// offset. A DateTimeOffset property must be explicitly converted to/from plain UTC DateTime for
/// storage — without this, Microsoft.Data.SqlClient throws InvalidCastException when reading the
/// column back ("Unable to cast object of type 'System.DateTime' to type 'System.DateTimeOffset'").
/// Apply to every DateTimeOffset(?) property in an entity configuration.
/// </summary>
public static class PropertyBuilderExtensions
{
    public static PropertyBuilder<DateTimeOffset> AsUtcDateTime2(this PropertyBuilder<DateTimeOffset> builder) =>
        builder.HasConversion(v => v.UtcDateTime, v => new DateTimeOffset(v, TimeSpan.Zero));

    public static PropertyBuilder<DateTimeOffset?> AsUtcDateTime2(this PropertyBuilder<DateTimeOffset?> builder) =>
        builder.HasConversion(
            v => v.HasValue ? v.Value.UtcDateTime : (DateTime?)null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : (DateTimeOffset?)null);
}