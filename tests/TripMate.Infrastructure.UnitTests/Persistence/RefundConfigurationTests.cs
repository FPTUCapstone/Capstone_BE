using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class RefundConfigurationTests
{
    [Fact]
    public void RefundConfiguration_MapsAllDateTimeOffsetsAsUtcDateTime2()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var entity = context.Model.FindEntityType(typeof(Refund))!;

        foreach (string propertyName in new[]
                 {
                     nameof(Refund.RequestedAtUtc), nameof(Refund.ProcessedAtUtc),
                     nameof(Refund.LastAttemptAtUtc), nameof(Refund.NextAttemptAtUtc),
                 })
        {
            entity.FindProperty(propertyName)!.GetValueConverter().Should().NotBeNull(
                $"{propertyName} must persist DateTimeOffset as UTC DATETIME2");
        }
    }
}