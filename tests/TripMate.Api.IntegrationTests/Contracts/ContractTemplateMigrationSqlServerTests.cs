using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Contracts;

public sealed class ContractTemplateMigrationSqlServerTests
{
    private static string Script => Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261123_admin_operator_contract_templates.sql");

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_IsAdditiveIdempotent_AndSeedsExactlyOneEmptyHead()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE TABLE dbo.Users(user_id BIGINT PRIMARY KEY, status VARCHAR(24)); INSERT dbo.Users VALUES(9,'Active');");
        await database.ExecuteScriptAsync(Script);
        await database.ExecuteScriptAsync(Script);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Users WHERE user_id=9 AND status='Active';")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM admin.ContractTemplatePublicationHeads WHERE head_id=1 AND generation=0 AND current_version=0 AND current_published_template_id IS NULL;")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID('admin');")).Should().Be(3);
        Func<Task> deleteActor = () => database.ExecuteNonQueryAsync("DELETE dbo.Users WHERE user_id=9;");
        await InsertPublishedAsync(database, 1);
        await deleteActor.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_EnforcesOnePublishedVersion_AndPreservesArchivedHistory()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE TABLE dbo.Users(user_id BIGINT PRIMARY KEY); INSERT dbo.Users VALUES(9);");
        await database.ExecuteScriptAsync(Script);
        await InsertPublishedAsync(database, 1);
        Func<Task> clearPreviewRevision = () => database.ExecuteNonQueryAsync("UPDATE admin.OperatorContractTemplates SET preview_revision=NULL WHERE version=1;");
        var checkViolation = await clearPreviewRevision.Should().ThrowAsync<SqlException>();
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().Options);
        context.IsContractTemplatePublicationConflict(checkViolation.Which).Should().BeFalse();
        Func<Task> clearAcknowledgment = () => database.ExecuteNonQueryAsync("UPDATE admin.OperatorContractTemplates SET preview_acknowledged_revision=NULL WHERE version=1;");
        await clearAcknowledgment.Should().ThrowAsync<SqlException>();
        Func<Task> another = () => InsertPublishedAsync(database, 2);
        var duplicatePublication = await another.Should().ThrowAsync<SqlException>();
        context.IsContractTemplatePublicationConflict(new DbUpdateException("Concurrent publication", duplicatePublication.Which)).Should().BeTrue();
        await database.ExecuteNonQueryAsync("UPDATE admin.OperatorContractTemplates SET status='Archived',archived_at=SYSUTCDATETIME() WHERE version=1;");
        await InsertPublishedAsync(database, 2);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM admin.OperatorContractTemplates WHERE status='Published';")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM admin.OperatorContractTemplates WHERE version=1 AND status='Archived' AND source_storage_key='source';")).Should().Be(1);
        await database.ExecuteScriptAsync(Script);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_ClassifiesVersionAndIdempotencyKeyConflicts()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE TABLE dbo.Users(user_id BIGINT PRIMARY KEY); INSERT dbo.Users VALUES(9);");
        await database.ExecuteScriptAsync(Script);
        await InsertPublishedAsync(database, 1);
        await database.ExecuteNonQueryAsync("UPDATE admin.OperatorContractTemplates SET status='Archived',archived_at=SYSUTCDATETIME(),publication_idempotency_key='11111111-1111-1111-1111-111111111111' WHERE version=1;");
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().Options);
        Func<Task> sameVersion = () => InsertPublishedAsync(database, 1);
        var versionConflict = await sameVersion.Should().ThrowAsync<SqlException>();
        context.IsContractTemplatePublicationConflict(versionConflict.Which).Should().BeTrue();

        await InsertPublishedAsync(database, 2);
        Func<Task> sameKey = () => database.ExecuteNonQueryAsync("UPDATE admin.OperatorContractTemplates SET publication_idempotency_key='11111111-1111-1111-1111-111111111111' WHERE version=2;");
        var keyConflict = await sameKey.Should().ThrowAsync<SqlException>();
        context.IsContractTemplatePublicationConflict(keyConflict.Which).Should().BeTrue();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RejectsIncompatibleExistingColumns_WithoutPartialTables()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE TABLE dbo.Users(user_id BIGINT PRIMARY KEY); EXEC('CREATE SCHEMA admin'); CREATE TABLE admin.OperatorContractTemplates(template_id INT PRIMARY KEY);");
        Func<Task> migrate = () => database.ExecuteScriptAsync(Script);
        await migrate.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID('admin');")).Should().Be(1);
    }

    private static Task InsertPublishedAsync(SqlServerTestDatabase database, int version) => database.ExecuteNonQueryAsync($"""
        INSERT admin.OperatorContractTemplates(template_type,language,title,version,draft_revision,status,
            source_storage_key,source_sha256,source_file_name,variables_manifest_json,validation_valid,render_state,
            preview_storage_key,preview_sha256,preview_revision,preview_acknowledged_revision,
            created_by_user_id,updated_by_user_id,published_by_user_id,created_at,updated_at,published_at)
        VALUES('OperatorPartnership','vi','Synthetic fixture',{version},1,'Published','source',REPLICATE('a',64),
            'fixture.docx','[]',1,'Ready','preview',REPLICATE('b',64),1,1,9,9,9,SYSUTCDATETIME(),SYSUTCDATETIME(),SYSUTCDATETIME());
        """);
}