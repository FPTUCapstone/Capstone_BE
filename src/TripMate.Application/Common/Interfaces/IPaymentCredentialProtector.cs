namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Reversible at-rest encryption for payOS credential values (ClientId, ApiKey, ChecksumKey).
/// Raw credential values are never stored in the database; only the reference tokens returned by
/// <see cref="Protect"/> are persisted. <see cref="Unprotect"/> is for server-side use only and
/// must never be called in a path that serialises the result to a log or API response.
/// </summary>
public interface IPaymentCredentialProtector
{
    string Protect(string rawCredential);

    string Unprotect(string referenceToken);
}
