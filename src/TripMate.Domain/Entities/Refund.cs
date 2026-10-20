using TripMate.Domain.Common;
namespace TripMate.Domain.Entities;

public class Refund : BaseEntity { public long BookingId { get; private set; } public long? TransactionId { get; private set; } public decimal Amount { get; private set; } public string Status { get; private set; } = string.Empty; public DateTimeOffset? ProcessedAtUtc { get; private set; } }