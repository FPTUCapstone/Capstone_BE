namespace TripMate.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Naming convention and safety guard for databases the test fixture is allowed to create and drop.
/// A managed name is a known prefix followed by exactly 32 ASCII letters, digits or underscores.
/// </summary>
internal static class SqlServerManagedNames
{
    internal const string TestPrefix = "TripMate_Test_";
    internal const string TemplatePrefix = "TripMate_Template_";

    private const int SuffixLength = 32;

    internal static string NewTestName() => TestPrefix + Guid.NewGuid().ToString("N");

    internal static string NewTemplateName() => TemplatePrefix + Guid.NewGuid().ToString("N");

    internal static bool IsTestName(string name) => HasPrefixAndSafeSuffix(name, TestPrefix);

    internal static bool IsTemplateName(string name) => HasPrefixAndSafeSuffix(name, TemplatePrefix);

    private static bool HasPrefixAndSafeSuffix(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal)
        && name.Length == prefix.Length + SuffixLength
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}