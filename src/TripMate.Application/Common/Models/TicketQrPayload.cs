namespace TripMate.Application.Common.Models;

/// <summary><see cref="QrToken"/> and <see cref="Signature"/> are stored; <see cref="Content"/> goes into the QR image.</summary>
public sealed record TicketQrPayload(string QrToken, string Signature, string Content);

public sealed record TicketQrVerification(long TicketId, string QrToken, DateTimeOffset IssuedAtUtc);
