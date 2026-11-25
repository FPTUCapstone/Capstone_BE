using FluentAssertions;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class SqlServerManagedNamesTests
{
    [Fact]
    public void NewNames_PassOnlyTheirOwnGuard()
    {
        var test = SqlServerManagedNames.NewTestName();
        var template = SqlServerManagedNames.NewTemplateName();
        SqlServerManagedNames.IsTestName(test).Should().BeTrue();
        SqlServerManagedNames.IsTemplateName(test).Should().BeFalse();
        SqlServerManagedNames.IsTemplateName(template).Should().BeTrue();
        SqlServerManagedNames.IsTestName(template).Should().BeFalse();
    }

    [Theory]
    [InlineData("TripMate_Test_")]                                  // prefix only
    [InlineData("TripMate_Test_0123456789abcdef0123456789abcde")]   // 31 chars
    [InlineData("TripMate_Test_0123456789abcdef0123456789abcdef0")] // 33 chars
    [InlineData("TripMate_Test_0123456789abcdef0123456789abcde]")]  // bracket
    [InlineData("TripMateDb")]
    [InlineData("master")]
    [InlineData("")]
    public void IsTestName_RejectsAnythingButPrefixPlus32SafeCharacters(string name) =>
        SqlServerManagedNames.IsTestName(name).Should().BeFalse();

    [Fact]
    public void IsTemplateName_RejectsATestName() =>
        SqlServerManagedNames.IsTemplateName("TripMate_Test_0123456789abcdef0123456789abcdef").Should().BeFalse();
}