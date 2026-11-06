using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Tours.Emergency.Common;

internal static class EmergencyEvidenceValidation
{
    public static bool IsValidHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        string trimmed = value.Trim();
        return trimmed.Length <= EmergencyTourCancellationOperation.EvidenceUrlMaxLength
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo);
    }
}