namespace TripMate.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Joins a directory and a file name as the SQL Server host sees them. The server may run on
/// Windows or Linux, so the test process's own Path.Combine separator is not reliable.
/// </summary>
internal static class SqlServerServerPath
{
    internal static string Combine(string directory, string fileName)
    {
        var separator = directory.Contains('\\') ? '\\' : '/';
        var trimmed = directory.EndsWith('\\') || directory.EndsWith('/')
            ? directory[..^1]
            : directory;
        return trimmed + separator + fileName;
    }
}