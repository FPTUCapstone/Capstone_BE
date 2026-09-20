using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class AuditOutcomePersistenceTests
{
    [Fact]
    public void OutcomeMapping_PreservesLegacyNullAndUsesConstrainedStringColumns()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(new SqlConnection()).Options);
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AuditLog))!;
        var table = StoreObjectIdentifier.Table("AuditLogs", "dbo");
        var outcome = entity.FindProperty(nameof(AuditLog.Result))!;
        outcome.GetColumnName(table).Should().Be("result");
        outcome.GetColumnType().Should().Be("varchar(20)");
        outcome.IsNullable.Should().BeTrue();
        var converter = outcome.GetTypeMapping().Converter!;
        converter.ConvertToProvider(AuditOutcome.Success).Should().Be("Success");
        converter.ConvertToProvider(AuditOutcome.Failure).Should().Be("Failure");
        converter.ConvertFromProvider("Failure").Should().Be(AuditOutcome.Failure);
        converter.ConvertToProvider(null).Should().BeNull();
        outcome.GetDefaultValueSql().Should().BeNull();
        var reason = entity.FindProperty(nameof(AuditLog.Reason))!;
        reason.GetColumnName(table).Should().Be("reason");
        reason.GetColumnType().Should().Be("nvarchar(1000)");
        reason.IsNullable.Should().BeTrue();
    }
}