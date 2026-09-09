using System.Security.Cryptography;

using Konscious.Security.Cryptography;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// Argon2id hashing per the OWASP Password Storage Cheat Sheet minimum recommendation
/// (m=19 MiB, t=2, p=1), self-describing so the parameters can be raised later without
/// invalidating already-stored hashes.
/// </summary>
public class PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MemorySizeKb = 19_456;
    private const int Iterations = 2;
    private const int DegreeOfParallelism = 1;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = ComputeHash(password, salt, MemorySizeKb, Iterations, DegreeOfParallelism, HashSize);

        return string.Join(
            '.',
            MemorySizeKb,
            Iterations,
            DegreeOfParallelism,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string password, string passwordHash)
    {
        var parts = passwordHash.Split('.', 5);
        if (parts.Length != 5
            || !int.TryParse(parts[0], out var memorySizeKb)
            || !int.TryParse(parts[1], out var iterations)
            || !int.TryParse(parts[2], out var degreeOfParallelism))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[3]);
        var expectedHash = Convert.FromBase64String(parts[4]);

        var actualHash = ComputeHash(
            password,
            salt,
            memorySizeKb,
            iterations,
            degreeOfParallelism,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static byte[] ComputeHash(
        string password,
        byte[] salt,
        int memorySizeKb,
        int iterations,
        int degreeOfParallelism,
        int hashSize)
    {
        using var argon2 = new Argon2id(System.Text.Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memorySizeKb,
            Iterations = iterations,
            DegreeOfParallelism = degreeOfParallelism,
        };

        return argon2.GetBytes(hashSize);
    }
}