using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class ContractTemplatePersistenceModelTests
{
    [Fact]
    public void Model_MapsPrivateArtifactsAndOptimisticPublicationHead()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(new SqlConnection()).Options);
        var template = context.Model.FindEntityType(typeof(OperatorContractTemplate));
        template.Should().NotBeNull();
        template!.GetTableName().Should().Be("OperatorContractTemplates");
        template.GetSchema().Should().Be("admin");
        template.FindProperty(nameof(OperatorContractTemplate.RowVersion))!.IsConcurrencyToken.Should().BeTrue();
        template.GetIndexes().Should().Contain(index => index.IsUnique && index.GetFilter() == "[status] = 'Published'");
        template.GetIndexes().Should().Contain(index => index.IsUnique && index.Properties.Any(property => property.Name == nameof(OperatorContractTemplate.Version)));
        template.GetForeignKeys().Should().OnlyContain(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.Restrict);

        var head = context.Model.FindEntityType(typeof(ContractTemplatePublicationHead))!;
        head.FindProperty(nameof(ContractTemplatePublicationHead.Id))!.ValueGenerated.Should().Be(ValueGenerated.Never);
        head.FindProperty(nameof(ContractTemplatePublicationHead.RowVersion))!.IsConcurrencyToken.Should().BeTrue();
        head.GetIndexes().Should().ContainSingle(index => index.IsUnique);

        var job = context.Model.FindEntityType(typeof(ContractTemplateRenderJob))!;
        job.GetForeignKeys().Should().ContainSingle(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(OperatorContractTemplate) && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        job.GetIndexes().Should().Contain(index => index.IsUnique && index.Properties.Count == 2);
    }
}