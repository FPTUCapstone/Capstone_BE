using System.Globalization;

using FluentAssertions;

using TripMate.Application.Common.Locks;

namespace TripMate.Application.UnitTests.Common.Locks;

public sealed class AccountStatusLockResourceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(long.MaxValue)]
    public void Create_WithPositiveAccountId_UsesCanonicalInvariantResource(long accountId)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            AccountStatusLockResource.Create(accountId)
                .Should().Be($"TripMate:AccountStatus:{accountId.ToString(CultureInfo.InvariantCulture)}");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Create_WithNonPositiveAccountId_Throws(long accountId)
    {
        var action = () => AccountStatusLockResource.Create(accountId);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("accountId");
    }

    [Fact]
    public void Normalize_DeduplicatesAndSortsAscending()
    {
        AccountStatusLockResource.Normalize([long.MaxValue, 7, 1, 7, 2, 1])
            .Should().Equal(1, 2, 7, long.MaxValue);
    }

    [Fact]
    public void Normalize_WithNullCollection_Throws()
    {
        var action = () => AccountStatusLockResource.Normalize(null!);

        action.Should().Throw<ArgumentNullException>()
            .WithParameterName("accountIds");
    }

    [Fact]
    public void Normalize_WithEmptyCollection_Throws()
    {
        var action = () => AccountStatusLockResource.Normalize([]);

        action.Should().Throw<ArgumentException>()
            .WithParameterName("accountIds");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Normalize_WithAnyNonPositiveAccountId_Throws(long invalidAccountId)
    {
        var action = () => AccountStatusLockResource.Normalize([2, invalidAccountId, 1]);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("accountIds");
    }
}