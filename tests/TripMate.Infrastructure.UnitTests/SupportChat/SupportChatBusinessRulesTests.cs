using System.Text.RegularExpressions;

using TripMate.Infrastructure.SupportChat;

namespace TripMate.Infrastructure.UnitTests.SupportChat;

public sealed class SupportChatBusinessRulesTests
{
    [Fact]
    public void SnapshotCoversEveryCurrentRuleAndRetirementNotice()
    {
        var rules = SupportChatBusinessRules.Snapshot.Rules;
        Assert.Equal(201, rules.Count(rule => !rule.Retired));
        Assert.Equal(186, rules.Count(rule => !rule.Retired && rule.Id.StartsWith("BR-", StringComparison.Ordinal)));
        Assert.Equal(15, rules.Count(rule => rule.Id.StartsWith("CR-", StringComparison.Ordinal)));
        Assert.Equal(new[] { "BR-112", "BR-113", "BR-116", "BR-157" }, rules.Where(rule => rule.Retired).Select(rule => rule.Id));
        Assert.Equal(rules.Count, rules.Select(rule => rule.Id).Distinct().Count());
        var corpus = string.Join("\n", SupportChatBusinessRules.Passages.Select(passage => passage.Content));
        Assert.All(rules, rule => Assert.Matches($@"\b{Regex.Escape(rule.Id)}\b", corpus));
    }

    [Fact]
    public void PassagesRetainScopedRulesAndDocumentProvenance()
    {
        Assert.Equal("2026-11-12T12:32:53.377Z", SupportChatBusinessRules.Snapshot.SourceModifiedAtUtc);
        Assert.All(SupportChatBusinessRules.Passages, passage =>
        {
            Assert.Equal(SupportChatBusinessRules.Snapshot.SourceUrl, passage.Source.Url);
            Assert.Contains("2026-11-12", passage.Content);
            Assert.InRange(passage.Content.Length, 1, 6500);
        });
        Assert.Contains(SupportChatBusinessRules.Passages, passage =>
            passage.Content.Contains("BR-115", StringComparison.Ordinal) &&
            passage.Content.Contains("Only an account holding the Staff role may moderate", StringComparison.Ordinal));
        Assert.DoesNotContain(SupportChatBusinessRules.Passages.Where(passage => passage.Source.Id != "srs-retired-20261112"),
            passage => passage.Content.Contains("24-hour full-refund rule", StringComparison.Ordinal));
    }

    [Fact]
    public void BroadRefundPolicyHasCurrentTiersAndPolicySnapshotCaveatTogether()
    {
        var policy = Assert.Single(SupportChatBusinessRules.Passages, passage => passage.Source.Id == "srs-refund-policy-20261112");
        foreach (var id in new[] { "BR-135", "BR-136", "BR-155", "BR-156", "BR-76", "BR-143", "BR-186" })
            Assert.Contains(id, policy.Content);
        Assert.Contains("more than 14 days", policy.Content);
        Assert.Contains("10 to 14 days", policy.Content);
        Assert.Contains("less than 10 days", policy.Content);
        Assert.Contains("stored policy version", policy.Content);
        Assert.Contains("Hoàn ngay chỉ áp dụng", policy.Content);
        Assert.Contains("Hủy booking thông thường", policy.Content);
        Assert.Contains("không áp dụng các mốc phần trăm của Traveler", policy.Content);
    }

    [Fact]
    public void SearchIndexIncludesAllRulesWithoutDuplicatingEveryScopedDefinition()
    {
        var passages = SupportChatBusinessRules.Passages;
        var index = string.Join("\n", passages.Select(passage => passage.RetrievalText));
        var grounding = string.Join("\n", passages.Select(passage => passage.Content));
        Assert.All(SupportChatBusinessRules.Snapshot.Rules, rule => Assert.Contains(rule.Id, index));
        Assert.True(index.Length < grounding.Length * 0.65);
        Assert.Contains("Only an account holding the Staff role may moderate", grounding);
    }
}