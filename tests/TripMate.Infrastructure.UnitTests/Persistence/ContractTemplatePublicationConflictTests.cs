using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class ContractTemplatePublicationConflictTests
{
    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    [InlineData(1205)]
    public void NumberBearingNonSqlException_IsNotPublicationConflict(int number)
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().Options);
        var exception = new DbUpdateException("Save failed", new FakeSqlException(number,
            "Violation of UNIQUE KEY constraint 'UX_OperatorContractTemplates_PublicationKey'."));

        context.IsContractTemplatePublicationConflict(exception).Should().BeFalse();
    }

    private sealed class FakeSqlException(int number, string message) : Exception(message)
    {
        public int Number { get; } = number;
    }
}