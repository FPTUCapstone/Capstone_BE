using Microsoft.AspNetCore.DataProtection;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Security;

internal sealed class DataProtectionPushTokenProtector(IDataProtectionProvider provider)
    : IPushTokenProtector
{
    private readonly IDataProtector protector = provider.CreateProtector(
        "TripMate.PushDeviceToken.v1");

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => protector.Unprotect(protectedValue);
}