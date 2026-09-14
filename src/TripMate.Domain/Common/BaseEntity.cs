namespace TripMate.Domain.Common;

/// <summary>
/// Every table in database/tripmate_schema_v7.sql uses a BIGINT IDENTITY primary key generated
/// by SQL Server, not a client-generated value — leave Id at its default (0) when constructing a
/// new entity and let the database assign it on insert.
/// </summary>
public abstract class BaseEntity
{
    public long Id { get; set; }
}