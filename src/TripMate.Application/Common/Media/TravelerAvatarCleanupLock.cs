using System.Security.Cryptography;
using System.Text;

namespace TripMate.Application.Common.Media;

public static class TravelerAvatarCleanupLock
{
    public static string ForPublicId(string publicId) =>
        $"traveler-avatar-cleanup:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicId)))}";
}