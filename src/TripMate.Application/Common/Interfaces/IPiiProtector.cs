namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Reversible, at-rest encryption for a single PII field (BR-33). Distinct from
/// <c>IOtpProtectionService</c>, which is one-way HMAC verification and cannot be decrypted.
/// </summary>
public interface IPiiProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
