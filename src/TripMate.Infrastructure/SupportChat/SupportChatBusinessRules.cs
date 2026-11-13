using System.Text;
using System.Text.Json;

using TripMate.Application.Features.SupportChat;

namespace TripMate.Infrastructure.SupportChat;

internal static class SupportChatBusinessRules
{
    private const int MaximumChunkCharacters = 4800;
    private const string Revision = "20261112";
    private const string ResourceName = "TripMate.SupportChat.srs-business-rules-20261112.json";

    internal sealed record ScopedRule(string Section, string Title, string Definition);
    internal sealed record BusinessRule(string Id, string Definition, string UsedIn, bool Retired,
        IReadOnlyList<ScopedRule> Details);
    internal sealed record RuleSnapshot(string SourceUrl, string SourceModifiedAtUtc, string SourceSections,
        IReadOnlyList<BusinessRule> Rules);

    internal static RuleSnapshot Snapshot { get; } = ReadSnapshot();
    internal static IReadOnlyList<KnowledgePassage> Passages { get; } = BuildPassages();

    private static RuleSnapshot ReadSnapshot()
    {
        using var stream = typeof(SupportChatBusinessRules).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Support chat business-rule snapshot is missing.");
        var snapshot = JsonSerializer.Deserialize<RuleSnapshot>(stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Support chat business-rule snapshot is invalid.");
        if (snapshot.Rules.Count == 0 || snapshot.Rules.Select(rule => rule.Id).Distinct().Count() != snapshot.Rules.Count)
            throw new InvalidOperationException("Support chat business-rule inventory is invalid.");
        return snapshot;
    }

    private static IReadOnlyList<KnowledgePassage> BuildPassages()
    {
        var passages = new List<KnowledgePassage>();
        foreach (var topic in Snapshot.Rules.Where(rule => !rule.Retired).GroupBy(Topic))
        {
            var content = new StringBuilder();
            var retrievalText = new StringBuilder();
            var ids = new List<string>();
            var part = 0;
            var title = topic.First().Details.FirstOrDefault()?.Title ?? "Common interface rules";
            foreach (var rule in topic)
            {
                var text = RuleText(rule);
                if (content.Length > 0 && content.Length + text.Length > MaximumChunkCharacters)
                {
                    passages.Add(Passage($"srs-{topic.Key.Replace('.', '-')}-{part++}-{Revision}",
                        $"SRS {title} · {string.Join(", ", ids)}", content.ToString(), retrievalText.ToString()));
                    content.Clear();
                    retrievalText.Clear();
                    ids.Clear();
                }
                content.AppendLine(text);
                retrievalText.AppendLine($"{rule.Id} — {rule.Definition}");
                retrievalText.AppendLine(string.Join(" · ", rule.Details.Select(detail => detail.Title).Distinct()));
                ids.Add(rule.Id);
            }
            if (content.Length > 0)
                passages.Add(Passage($"srs-{topic.Key.Replace('.', '-')}-{part}-{Revision}",
                    $"SRS {title} · {string.Join(", ", ids)}", content.ToString(), retrievalText.ToString()));
        }

        string[] refundRuleIds = ["BR-135", "BR-136", "BR-155", "BR-156", "BR-76", "BR-143", "BR-186"];
        var refundRules = refundRuleIds.Select(id => Snapshot.Rules.Single(rule => rule.Id == id));
        passages.Add(Passage($"srs-refund-policy-{Revision}",
            "Chính sách hủy tour và hoàn tiền · Cancellation and refund policy · BR-135, BR-136, BR-156",
            "Chính sách hoàn tiền chung khi khách hủy tour hoặc nhà điều hành hủy. Refund eligibility and percentages.\n" +
            "Có ba trường hợp riêng, phải nêu rõ người hủy trong từng trường hợp.\n" +
            "1. TRAVELER tự hủy booking. Chỉ trường hợp này áp dụng BR-135: hơn 14 ngày hoàn 100%; từ 10 đến 14 ngày hoàn 50%; dưới 10 ngày hoàn 0%, cần cảnh báo và xác nhận riêng.\n" +
            "2. TOUR OPERATOR hủy booking thông thường theo UC-41 và BR-136. Hoàn 100% số tiền đã thanh toán được xác minh, không áp dụng các mốc phần trăm của Traveler. Hủy booking thông thường áp dụng luồng BR-143 qua UC-42 rồi UC-67, có xác nhận và thu hồi phần chia trước khi hoàn; hoàn 100% không đồng nghĩa hoàn ngay.\n" +
            "3. TOUR OPERATOR hủy LỊCH KHỞI HÀNH DO THỜI TIẾT theo UC-66 và BR-186. Hoàn 100% ngay cho các khách bị ảnh hưởng. Hoàn ngay chỉ áp dụng cho ngoại lệ thời tiết này.\n" +
            string.Join("\n", refundRules.Select(rule => $"{rule.Id} — {rule.Definition}")) +
            "\nExplain the general documented policy without requiring an existing booking. A specific refund amount requires the authorized booking preview and its stored policy version."));
        passages.Add(Passage($"srs-retired-{Revision}", "SRS retired rules · Quy tắc đã ngừng dùng",
            "These are retirement notices only. Never apply these rules as current policies.\n" +
            string.Join("\n", Snapshot.Rules.Where(rule => rule.Retired).Select(rule => $"{rule.Id} — {rule.Definition}"))));
        return passages.AsReadOnly();
    }

    private static string Topic(BusinessRule rule)
    {
        var section = rule.Details.FirstOrDefault()?.Section;
        return section is null ? "common" : string.Join('.', section.Split('.').Take(3));
    }

    private static string RuleText(BusinessRule rule) =>
        $"{rule.Id} — {rule.Definition}\nReferenced sections {rule.UsedIn}\n" +
        string.Join("\n", rule.Details.Select(detail =>
            $"Section {detail.Section} {detail.Title}\n{detail.Definition}"));

    private static KnowledgePassage Passage(string id, string title, string content, string? retrievalText = null) => new(
        new ChatSource(id, title, Snapshot.SourceUrl),
        $"Report 3 SRS snapshot, source modified {Snapshot.SourceModifiedAtUtc}. Documented business requirements.\n{content}",
        retrievalText ?? content);
}
