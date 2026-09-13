namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.Messages in database/tripmate_schema_v7.sql — SRS §5.3 Application Messages List (MSG01–MSG130).
/// </summary>
public class Message
{
    public string MessageCode { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    public string ContentTemplate { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
}
