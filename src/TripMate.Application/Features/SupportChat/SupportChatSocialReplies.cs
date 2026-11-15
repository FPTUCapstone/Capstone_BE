using System.Text;
using System.Text.RegularExpressions;

namespace TripMate.Application.Features.SupportChat;

internal static class SupportChatSocialReplies
{
    internal static string? GetReply(string message)
    {
        var text = Regex.Replace(message.Normalize(NormalizationForm.FormC).Trim()
            .TrimEnd('!', '.', ',', '?', '…').Trim().ToLowerInvariant(), @"\s+", " ");
        return text switch
        {
            "hi" or "hey" or "hi 👋" or "👋" or "hi xin chào" or "hi xin chao" or
            "xin chào" or "xin chao" or "chào" or "chao" or "chào bạn" or "chao ban" or
            "chào tripmate" or "xin chào tripmate" =>
                "Chào bạn! Mình là trợ lý TripMate. Bạn muốn tìm tour, lên kế hoạch hay cần hỗ trợ điều gì?",
            "hello" or "hello tripmate" =>
                "Hello! I'm TripMate's assistant. What would you like to explore or get help with?",
            "cảm ơn" or "cảm ơn bạn" or "cam on" or "cam on ban" =>
                "Rất vui được giúp bạn! Bạn cần mình hỗ trợ thêm điều gì không?",
            "thanks" or "thank you" => "You're welcome! What else can I help you with?",
            _ => null,
        };
    }
}