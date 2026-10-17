using Microsoft.AspNetCore.DataProtection;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Security;

/// <summary>
/// Reversible PII encryption backed by ASP.NET Core Data Protection (BR-33). The purpose
/// string is a fixed discriminator scoping the derived key to this one field, so a protector
/// for a different field can never decrypt this one. Key persistence/rotation is a deployment
/// concern (configure a durable key ring, e.g. a certificate or blob store, outside this class)
/// — losing the key ring makes every value encrypted with it permanently unreadable.
/// </summary>
public sealed class DataProtectionPiiProtector : IPiiProtector
{
    private const string Purpose = "TripMate.BookingParticipant.IdDocumentNumber.v1";

    private readonly IDataProtector protector;

    public DataProtectionPiiProtector(IDataProtectionProvider provider)
    {
        protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => protector.Unprotect(protectedValue);
}
