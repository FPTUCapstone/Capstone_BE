using System.Security.Cryptography;
using System.Text;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Tours.Emergency.Common;

/// <summary>A token for the evidence a client read, so its update cannot overwrite a newer one.</summary>
internal static class EmergencyEvidenceVersion
{
    public static string Of(EmergencyTourCancellationOperation operation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            operation.EvidenceUrl,
            operation.AuthorityReference,
            operation.EvidenceReference,
            operation.EvidenceAddedLater)))).ToLowerInvariant();
}
