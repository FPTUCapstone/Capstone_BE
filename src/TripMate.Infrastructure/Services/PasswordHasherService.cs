using Microsoft.AspNetCore.Identity;
using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

public sealed class PasswordHasherService : IPasswordHasherService, IPasswordHasher
{
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password)
    {
        return _hasher.HashPassword(null!, password);
    }

    public bool Verify(string password, string passwordHash)
    {
        var result = _hasher.VerifyHashedPassword(
            null!,
            passwordHash,
            password
        );

        return result == PasswordVerificationResult.Success ||
               result == PasswordVerificationResult.SuccessRehashNeeded;
    }
}
