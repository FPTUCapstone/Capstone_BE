using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class OperatorContractSigningPersistenceModelTests
{
    [Fact]
    public void Contract_evidence_is_private_concurrent_and_owner_bound()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(new SqlConnection()).Options);
        var contract = db.Model.FindEntityType(typeof(OperatorContract))!;
        Assert.Equal("admin", contract.GetSchema());
        Assert.True(contract.FindProperty(nameof(OperatorContract.RowVersion))!.IsConcurrencyToken);
        Assert.Contains(contract.GetIndexes(), item => item.IsUnique && item.GetFilter() == "[status] <> 'Voided'");
        Assert.All(contract.GetForeignKeys(), item => Assert.Equal(DeleteBehavior.Restrict, item.DeleteBehavior));
        var challenge = db.Model.FindEntityType(typeof(OperatorContractSigningChallenge))!;
        Assert.True(challenge.FindProperty(nameof(OperatorContractSigningChallenge.RowVersion))!.IsConcurrencyToken);
        Assert.Equal(4000, challenge.FindProperty(nameof(OperatorContractSigningChallenge.ProtectedOtp))!.GetMaxLength());
        Assert.Contains(challenge.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(OperatorContract) && key.Properties.Count == 2);
        var exemption = db.Model.FindEntityType(typeof(OperatorContractLegacyExemption))!;
        Assert.Equal(nameof(OperatorContractLegacyExemption.OperatorUserId), exemption.FindPrimaryKey()!.Properties.Single().Name);
    }
}