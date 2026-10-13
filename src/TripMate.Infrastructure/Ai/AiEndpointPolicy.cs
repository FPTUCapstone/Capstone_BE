using System.Net;
using System.Net.Sockets;

namespace TripMate.Infrastructure.Ai;

/// <summary>
/// Quy tắc dùng chung cho endpoint của hai tính năng AI (xếp hạng POI và giải thích lịch trình).
/// </summary>
public static class AiEndpointPolicy
{
    /// <summary>
    /// Endpoint có được chấp nhận hay không. HTTPS luôn hợp lệ. HTTP trần chỉ hợp lệ với
    /// <see cref="AiProviderKind.TripMateAi"/> và chỉ khi host nằm trong mạng nội bộ: tên service Docker
    /// (không có dấu chấm), localhost, địa chỉ loopback, địa chỉ riêng hoặc link-local. Nhờ vậy khóa
    /// <c>X-TripMate-Ai-Key</c> không bao giờ đi ra host bên ngoài dưới dạng văn bản thuần.
    /// </summary>
    public static bool IsAllowedEndpoint(string? endpoint, AiProviderKind provider)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp
            && provider == AiProviderKind.TripMateAi
            && IsInternalHost(uri);
    }

    private static bool IsInternalHost(Uri uri)
    {
        string host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (IPAddress.TryParse(uri.IdnHost, out IPAddress? address))
        {
            return IsInternalAddress(address);
        }

        // Tên service trên mạng Docker (ai, tripmate-ai) không có dấu chấm; có dấu chấm nghĩa là tên miền
        // bên ngoài, trừ "localhost" theo RFC 6761 và các tên kết thúc bằng ".localhost" / ".internal".
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.');
    }

    private static bool IsInternalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10 (link-local) và fc00::/7 (unique local).
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => true,                                        // 10.0.0.0/8
            127 => true,                                       // 127.0.0.0/8
            169 => octets[1] == 254,                           // 169.254.0.0/16 (link-local)
            172 => octets[1] >= 16 && octets[1] <= 31,         // 172.16.0.0/12 (mạng mặc định của Docker)
            192 => octets[1] == 168,                           // 192.168.0.0/16
            _ => false,
        };
    }
}