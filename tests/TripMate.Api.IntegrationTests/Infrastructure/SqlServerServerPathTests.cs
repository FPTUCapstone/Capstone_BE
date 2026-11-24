using FluentAssertions;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class SqlServerServerPathTests
{
    [Theory]
    [InlineData(@"C:\SQL\Data\", "a.mdf", @"C:\SQL\Data\a.mdf")]
    [InlineData(@"C:\SQL\Backup", "a.bak", @"C:\SQL\Backup\a.bak")]
    [InlineData("/var/opt/mssql/data/", "a.mdf", "/var/opt/mssql/data/a.mdf")]
    [InlineData("/var/opt/mssql/data", "a.bak", "/var/opt/mssql/data/a.bak")]
    [InlineData(@"C:\", "a.bak", @"C:\a.bak")]
    [InlineData("/", "a.bak", "/a.bak")]
    public void Combine_UsesTheSeparatorOfTheServerPath(string directory, string file, string expected) =>
        SqlServerServerPath.Combine(directory, file).Should().Be(expected);
}