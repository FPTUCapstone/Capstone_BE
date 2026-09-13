namespace TripMate.Application.Common.Interfaces;

public record GoogleTokenPayload(string Email, string Subject, string FullName, string? Picture);

public interface IGoogleTokenValidator
{
    Task<GoogleTokenPayload?> ValidateAsync(string idToken, CancellationToken cancellationToken = default);
}
