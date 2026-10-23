namespace TripMate.Infrastructure.Ai;

/// <summary>Nhà cung cấp AI cho xếp hạng POI và giải thích lịch trình.</summary>
public enum AiProviderKind
{
    /// <summary>Google Gemini (mặc định, giữ nguyên hành vi hiện tại).</summary>
    Gemini = 0,

    /// <summary>
    /// Service AI riêng của nhóm (repo Capstone_AI): model mở chạy trên Ollama, gọi qua mạng nội bộ.
    /// </summary>
    TripMateAi = 1,
}

/// <summary>Hằng số dùng chung khi gọi service TripMate AI.</summary>
public static class TripMateAiHeaders
{
    /// <summary>Header mang khóa dùng chung; service AI so khớp với biến môi trường AI_API_KEY.</summary>
    public const string ApiKey = "X-TripMate-Ai-Key";
}