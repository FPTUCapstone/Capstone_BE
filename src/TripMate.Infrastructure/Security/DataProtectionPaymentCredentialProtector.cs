using Microsoft.AspNetCore.DataProtection;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Security;

/// <summary>
/// Reversible encryption for payOS credential references backed by ASP.NET Core Data Protection.
/// The purpose string scopes the derived key to this protector; a key for PII cannot decrypt
/// payment credentials and vice versa. Raw values are never logged — only reference tokens are
/// persisted in the database.
/// </summary>
public sealed class DataProtectionPaymentCredentialProtector : IPaymentCredentialProtector
{
    private const string Purpose = "TripMate.OperatorPaymentChannel.Credential.v1";

    private readonly IDataProtector _protector;

    public DataProtectionPaymentCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string rawCredential) => _protector.Protect(rawCredential);

    public string Unprotect(string referenceToken) => _protector.Unprotect(referenceToken);
}