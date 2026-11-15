using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class PushDeviceToken : BaseEntity
{
    public const int ProtectedTokenMaxLength = 2000;
    public const int TokenHashLength = 64;

    private PushDeviceToken()
    {
    }

    public Guid InstallationId { get; private set; }
    public long UserId { get; private set; }
    public User User { get; private set; } = null!;
    public string ProtectedToken { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public static PushDeviceToken Register(
        User user,
        Guid installationId,
        string protectedToken,
        string tokenHash,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (installationId == Guid.Empty)
        {
            throw new ArgumentException("Installation ID is required.", nameof(installationId));
        }

        ValidateToken(protectedToken, tokenHash);
        return new PushDeviceToken
        {
            User = user,
            InstallationId = installationId,
            ProtectedToken = protectedToken,
            TokenHash = tokenHash,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    public void Reassign(User user, string protectedToken, string tokenHash, DateTimeOffset nowUtc)
        => Reassign(user, InstallationId, protectedToken, tokenHash, nowUtc);

    public void Reassign(
        User user,
        Guid installationId,
        string protectedToken,
        string tokenHash,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (installationId == Guid.Empty)
        {
            throw new ArgumentException("Installation ID is required.", nameof(installationId));
        }

        ValidateToken(protectedToken, tokenHash);
        User = user;
        UserId = user.Id;
        InstallationId = installationId;
        ProtectedToken = protectedToken;
        TokenHash = tokenHash;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
        DeactivatedAtUtc = null;
    }

    public void Deactivate(DateTimeOffset nowUtc)
    {
        DeactivatedAtUtc ??= nowUtc.ToUniversalTime();
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    private static void ValidateToken(string protectedToken, string tokenHash)
    {
        if (string.IsNullOrWhiteSpace(protectedToken)
            || protectedToken.Length > ProtectedTokenMaxLength)
        {
            throw new ArgumentException("Protected token is invalid.", nameof(protectedToken));
        }

        if (tokenHash.Length != TokenHashLength || tokenHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Token hash must be a SHA-256 hexadecimal value.", nameof(tokenHash));
        }
    }
}