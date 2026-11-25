using FluentAssertions;

using TripMate.Api.DevSearch;
namespace TripMate.Api.IntegrationTests.Tours;

public sealed class DevTourAccessTests
{
    [Theory]
    [InlineData("Development", true, "correct-key-at-least-32-characters-long", "correct-key-at-least-32-characters-long", true)]
    [InlineData("Staging", true, "correct-key-at-least-32-characters-long", "correct-key-at-least-32-characters-long", true)]
    [InlineData("Production", true, "correct-key-at-least-32-characters-long", "correct-key-at-least-32-characters-long", false)]
    [InlineData("Development", false, "correct-key-at-least-32-characters-long", "correct-key-at-least-32-characters-long", false)]
    [InlineData("Development", true, "", "", false)]
    [InlineData("Development", true, "short", "short", false)]
    [InlineData("Development", true, "correct-key-at-least-32-characters-long", "wrong", false)]
    public void Gate_RequiresEnvironmentOptInAndStrongExactKey(string env, bool enabled, string key, string supplied, bool expected)
        => DevTourAccess.Allows(env, enabled, key, supplied).Should().Be(expected);
}