namespace TripMate.Domain.Entities;

public sealed class CancellationPolicyTemplate
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;
    private readonly List<CancellationPolicyTemplateRule> _rules = [];

    private CancellationPolicyTemplate()
    {
    }

    public long Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public IReadOnlyCollection<CancellationPolicyTemplateRule> Rules => _rules.AsReadOnly();

    public static CancellationPolicyTemplate Create(string name, string? description,
        DateTimeOffset nowUtc, IEnumerable<CancellationPolicyTemplateRule> rules)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
            throw new ArgumentException("A valid policy name is required.", nameof(name));
        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalizedDescription?.Length > DescriptionMaxLength)
            throw new ArgumentException("The description is too long.", nameof(description));
        var templateRules = rules?.OrderByDescending(rule => rule.DaysBeforeDeparture).ToArray()
            ?? throw new ArgumentNullException(nameof(rules));
        if (templateRules.Length == 0 || templateRules.Select(rule => rule.DaysBeforeDeparture).Distinct().Count() != templateRules.Length)
            throw new ArgumentException("Policy rules must have unique cutoff days.", nameof(rules));

        var template = new CancellationPolicyTemplate
        {
            Name = name.Trim(),
            Description = normalizedDescription,
            IsActive = true,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
        foreach (CancellationPolicyTemplateRule rule in templateRules)
        {
            rule.AttachTo(template);
            template._rules.Add(rule);
        }
        return template;
    }
}