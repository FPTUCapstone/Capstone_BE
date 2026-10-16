using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

using Xunit.Abstractions;

namespace TripMate.Api.IntegrationTests.Tours;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class TourIndexEpochAllocatorCollection
{
    public const string CollectionName = "TM70 A4.1a owned SQL";
}

[Collection(TourIndexEpochAllocatorCollection.CollectionName)]
[Trait("Category", "SqlServer")]
public sealed class TourIndexEpochAllocatorSchemaSqlServerTests(ITestOutputHelper output)
{
    private const string Migration = "20261016_add_tour_index_epoch_allocator.sql";
    private const string Sequence = "commerce.TourIndexGenerationSequence";
    private const string Epochs = "commerce.TourIndexEpochs";
    private const string Trigger = "commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1";
    private const string LockResource = "TM70:A41A:EpochAllocator:Migration:v1";
    private const string ConnectionEnvironment = "TRIPMATE_SQLSERVER_TEST_CONNECTION";
    private const string RunManifestEnvironment = "TM70_A41A_EVIDENCE_DIRECTORY";
    private static readonly string[] PriorMigrations =
    [
        "20260912_add_travel_group_creation_requests.sql",
        "20260914_add_group_invitation_operations.sql",
        "20260914_add_scheduling_request_generation.sql",
        "20260915_add_tour_search_fields.sql",
        "20260915_extend_scheduling_request_contract.sql",
        "20260917_add_group_join_operations.sql",
        "20260918_add_audit_result_reason.sql",
        "20260919_allow_named_rest_items.sql",
        "20260920_add_itinerary_version_operations.sql",
        "20260923_add_tour_media.sql",
        "20260927_add_tour_media_management.sql",
        "20260928_add_trip_review_parent.sql",
        "20260929_add_recommendation_behavior_events.sql",
        "20260929_add_trip_review_media.sql",
        "20260930_add_trip_review_media_recovery.sql",
        "20261001_add_itinerary_item_friendly_explanation.sql",
        "20261002_add_operator_license_unique.sql",
        "20261003_add_commercial_service_disclosures.sql",
        "20261004_add_scheduling_generation_reservation.sql",
        "20261007_allow_staff_role.sql",
        "20261007_extend_trip_review_typed_parents.sql",
        "20261008_add_operator_document_cleanup_outbox.sql",
        "20261008_add_uc13_navigation_execution.sql",
        "20261009_add_tour_categories.sql",
        "20261012_add_payout_lifecycle_foundation.sql",
        "20261012_add_traveler_avatar_cleanup.sql",
        "20261012_add_traveler_profile_date_of_birth.sql",
        "20261013_remove_traveler_profile_default_budget.sql",
        "20261014_add_trip_review_rating_index.sql",
        "20261015_add_tour_package_authoring.sql",
        "20261015_poi_name_accent_insensitive.sql",
        "20261016_add_tour_destination_summary.sql"
    ];

    private const string PinnedSource = "690b2013687f3eb1bb37499831a55d737e2d6dc9";
    private static readonly string[] PinnedSha256 =
    [
        "C8509F76358C611BDEE5106053BECD914C2BBD0274DF3D246378A695598E931D",
        "60F8B066BB266D5BA476C2D7491E8191414F9D1648FC1D16569DB5C1396C9796",
        "B7330B971442973B7DE699CD0FD75CB6899DE09466ECCC76566C4B65C71A8E17",
        "D8438353E679777CE3D923FCDEA30179A3A75A6EFE9968F2BB71BD277503186C",
        "C5B512FE29C46182A437B5DC6B9331A77EF5719CB9F2C9C87506738F552C427D",
        "F2227CFE1566AFB6125716B17A10FADB3B570C80B638247DB334167EAB50D72F",
        "E4F328AB817A513CE8A9C3AAFD55054C3FCE8941AE2BBF0F7D490B7A9274EFE7",
        "07F01CF2EB0B7BBC1C68BF56F388FA11555FFC7A474F3C2EAEC8B27F046F1833",
        "4B78A6E5F687BCC9BBA8EC6EAB9370A9E71F1D2CF17506B00D51476EDB088391",
        "74A9CE63B2C28B9716360AE783770673DFAA6DB8D81484518C38C8CCD59D405E",
        "E97400420787FC4EB59F48ABE41C8BEA65748522100BA878728E152A94AC9B3F",
        "E1D3D52B52FDA661DFE7CCBD1D082511D34CCE9AD39F94AC121D1A87D434C3A3",
        "4DCD9BCC00DAC1148796BAF57658A19E5BC270E6A12F2AD4C5D89D67A5B5290A",
        "BAC7E77C36D1E55B58AE769C747AAC73E7BF8A766EEEB0978C3310A919B8B4B1",
        "21AC7103BE8ADD3D2C52643D6BB2E4FBA0D574A7BA55F76D22D8FFFDE40846C3",
        "4FDB8FE1B0595E384912D0B05AB50BA6B633C47BBBC253D78085EF5847AC6A05",
        "F287837432FD15FE0BA543AB2CB7BAF80F01A7B02E0A52421DC6F7C817ADFCAA",
        "27F4F39F4FFF9A5ED7EDD735024049CD594510EAE610F6EAE661AF191F2AE6BA",
        "F35A3AF8CFA9BA06009723CD1D18D29C718593D65DD785927542950FEE42B05B",
        "8EB8DC9C8860B8A323593B37CD3627C6329487D86A9E4404CFD149DA0B6FFDD0",
        "60B8E38BEC0A8C0275D528147C128CC0E4F21B0D1B0FB6EFF2FDCB814AA76341",
        "F544CEF9B9B78158D5A0959B15A11452C76449BCF8A2D72F4876872A51EFAA0A",
        "A3EADA132C284384E9D368F46A5715D29DD1A56BC2B0D3D6D241C092BCF551CF",
        "076021B1AFA96D1ED2221CE562921584051262F19ECED98E5D07E039F177FCB0",
        "F0C38CEC7EE9F6A903384D944496CCA4BBF8D1C1AB962007D63412790ED62101",
        "4D7EAC1B83A29A5DA04886ED26262F7A990103CB6D9C3A8C392EC7D091AF3348",
        "882021698616B072389FD83350DCE7B211327CF6411325C4CC3E7490FC142CC3",
        "86577A51F619AED3E97EB96D3BEE164318AE94A96FC35AE71C13B8BB28BDC0CB",
        "694EB1412EE09CB933D9162D57F4DC58353273A411A194DAE8A9FB4F56A3D01B",
        "D6D1171FDA85F4508FF00B4EC9E7FFAC351B6DBC66A855266946AC9416776CF7",
        "673ACED5C2AC6E8150F2B8249F469F35ED1D8CF8D8F02639D1AF9B9D0B9761DC",
        "61E68C2284942345C17E4BA3336742169C76AF6445765F5FE91E8B4BE692050E",
        "7E5D2C931903FA65A538139642E207EDA941864796C5EC0F6DC579D36386251A",
    ];
    private static readonly Lazy<Task<IReadOnlyList<string>>> PinnedScripts = new(ReadPinnedScriptsAsync);

    [SqlServerFact]
    public async Task EA01_FreshMigration_HasIndependentExactColumnsKeysAndSequence()
    {
        await using var owned = await CreateAsync();
        var db = owned.Database;
        var before = await Uc35InventoryAsync(db);
        await ApplyAsync(db);
        (await TextAsync(db, "SELECT STRING_AGG(CONVERT(nvarchar(max),CONCAT(column_id,':',name,':',system_type_id,':',max_length,':',is_nullable,':',COALESCE(collation_name,'-'),':',is_identity,':',is_computed,':',default_object_id)) COLLATE Latin1_General_100_BIN2, '|') WITHIN GROUP (ORDER BY column_id) FROM sys.columns WHERE object_id=OBJECT_ID(N'commerce.TourIndexEpochs');"))
            .Should().Be("1:index_epoch:36:16:0:-:0:0:0|2:build_id:36:16:0:-:0:0:0|3:collection_name:231:512:0:Latin1_General_100_BIN2:0:0:0|4:model_version:231:512:0:Latin1_General_100_BIN2:0:0:0|5:document_format_version:231:128:0:Latin1_General_100_BIN2:0:0:0|6:policy_version:231:128:0:Latin1_General_100_BIN2:0:0:0|7:epoch_state:167:24:0:Latin1_General_100_BIN2:0:0:0|8:created_at_utc:42:8:0:-:0:0:0|9:row_version:189:8:0:-:0:0:0");
        (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID(N'commerce.TourIndexEpochs') AND name IN ('PK_TourIndexEpochs','UQ_TourIndexEpochs_BuildId','UQ_TourIndexEpochs_Collection');")).Should().Be(3);
        (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'commerce.TourIndexEpochs') AND is_disabled=0 AND is_not_trusted=0 AND is_not_for_replication=0;")).Should().Be(7);
        (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'commerce.TourIndexEpochs');")).Should().Be(0);
        (await TextAsync(db, "SELECT STRING_AGG(CONVERT(nvarchar(max),CONCAT(k.name,':',k.type,':',i.type,':',i.is_unique,':',i.is_disabled,':',i.has_filter,':',i.ignore_dup_key,':',c.name,':',ic.key_ordinal,':',ic.is_descending_key,':',ic.is_included_column)) COLLATE Latin1_General_100_BIN2, '|') WITHIN GROUP (ORDER BY k.name) FROM sys.key_constraints k JOIN sys.indexes i ON i.object_id=k.parent_object_id AND i.index_id=k.unique_index_id JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE k.parent_object_id=OBJECT_ID(N'commerce.TourIndexEpochs');"))
            .Should().Be("PK_TourIndexEpochs:PK:1:1:0:0:0:index_epoch:1:0:0|UQ_TourIndexEpochs_BuildId:UQ:2:1:0:0:0:build_id:1:0:0|UQ_TourIndexEpochs_Collection:UQ:2:1:0:0:0:collection_name:1:0:0");
        (await TextAsync(db, "SELECT CONCAT(precision,':',scale) FROM sys.columns WHERE object_id=OBJECT_ID(N'commerce.TourIndexEpochs') AND name='created_at_utc';")).Should().Be("27:7");
        // Reviewed literal of the R2 immutable-tuple and inactive-lifecycle body, UTF-16LE/LF.
        (await TextAsync(db, "SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',REPLACE(definition,CHAR(13)+CHAR(10),CHAR(10))),2) FROM sys.sql_modules WHERE object_id=OBJECT_ID(N'commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1');"))
            .Should().Be("CDCB144D6370BEF2F6B2B75BA58D9FD4635298780871FBB186058CEF8A25A55E");
        var expectedChecks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CK_TourIndexEpochs_IndexEpoch"] = "([index_epoch]<>CONVERT([uniqueidentifier],'00000000-0000-0000-0000-000000000000'))",
            ["CK_TourIndexEpochs_BuildId"] = "([build_id]<>CONVERT([uniqueidentifier],'00000000-0000-0000-0000-000000000000'))",
            ["CK_TourIndexEpochs_Collection"] = "(datalength([collection_name])>(0) AND datalength([collection_name])=datalength(ltrim(rtrim([collection_name]))))",
            ["CK_TourIndexEpochs_Model"] = "(datalength([model_version])>(0) AND datalength([model_version])=datalength(ltrim(rtrim([model_version]))))",
            ["CK_TourIndexEpochs_Document"] = "(datalength([document_format_version])>(0) AND datalength([document_format_version])=datalength(ltrim(rtrim([document_format_version]))))",
            ["CK_TourIndexEpochs_Policy"] = "(datalength([policy_version])>(0) AND datalength([policy_version])=datalength(ltrim(rtrim([policy_version]))))",
            ["CK_TourIndexEpochs_InactiveState"] = "([epoch_state]='Provisioned' AND datalength([epoch_state])=(11) OR [epoch_state]='Paused' AND datalength([epoch_state])=(6) OR [epoch_state]='Retired' AND datalength([epoch_state])=(7))"
        };
        foreach (var expected in expectedChecks)
            (await TextAsync(db, $"SELECT definition FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'{Epochs}') AND name=N'{expected.Key}';")).Should().Be(expected.Value);
        (await TextAsync(db, "SELECT STRING_AGG(CONVERT(nvarchar(max),CONCAT(o.name,':',o.type)) COLLATE Latin1_General_100_BIN2, '|') WITHIN GROUP (ORDER BY o.name) FROM sys.objects o WHERE o.object_id IN (OBJECT_ID(N'commerce.TourIndexEpochs'),OBJECT_ID(N'commerce.TourIndexGenerationSequence')) OR o.parent_object_id=OBJECT_ID(N'commerce.TourIndexEpochs');"))
            .Should().Be("CK_TourIndexEpochs_BuildId:C |CK_TourIndexEpochs_Collection:C |CK_TourIndexEpochs_Document:C |CK_TourIndexEpochs_InactiveState:C |CK_TourIndexEpochs_IndexEpoch:C |CK_TourIndexEpochs_Model:C |CK_TourIndexEpochs_Policy:C |PK_TourIndexEpochs:PK|TourIndexEpochs:U |TourIndexGenerationSequence:SO|TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1:TR|UQ_TourIndexEpochs_BuildId:UQ|UQ_TourIndexEpochs_Collection:UQ");
        await AssertSequenceAsync(db);
        (await Uc35InventoryAsync(db)).Should().Be(before);
        (await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Epochs};")).Should().Be(0);
    }

    [SqlServerFact]
    public async Task EA02_MissingUc35Prerequisite_FailsWithoutObjectsOrOpenTransaction()
    {
        await using var owned = await CreateAsync();
        await owned.Database.ExecuteNonQueryAsync("ALTER TABLE commerce.Tours DROP CONSTRAINT DF_Tours_DestinationSummary;");
        await RejectMigrationAsync(owned.Database, "TM70.A41a.Prerequisite");
        (await owned.Database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.objects WHERE name IN ('TourIndexEpochs','TourIndexGenerationSequence');")).Should().Be(0);
    }

    [SqlServerFact]
    public async Task EA03_EpochKeysAndInactiveOnlyStates_AreEnforcedByDatabase()
    {
        await using var owned = await CreateAsync(migrate: true);
        var db = owned.Database;
        var epoch = Guid.NewGuid(); var build = Guid.NewGuid();
        await db.ExecuteNonQueryAsync(Insert(epoch, build, "owned-one"));
        await RejectSqlAsync(db, Insert(epoch, Guid.NewGuid(), "owned-two"));
        await RejectSqlAsync(db, Insert(Guid.NewGuid(), build, "owned-three"));
        await RejectSqlAsync(db, Insert(Guid.NewGuid(), Guid.NewGuid(), "owned-one"));
        await RejectSqlAsync(db, Insert(Guid.Empty, Guid.NewGuid(), "owned-zero"));
        await RejectSqlAsync(db, Insert(Guid.NewGuid(), Guid.Empty, "owned-zero-build"));
        foreach (var state in new[] { "Active", "Paused", "Retired", "Unknown", "Provisioned " })
            await RejectSqlAsync(db, Insert(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), state));
        await RejectSqlAsync(db, $"UPDATE {Epochs} SET epoch_state='Active';");
        await RejectSqlAsync(db, $"UPDATE {Epochs} SET policy_version=NULL;");
        await RejectSqlAsync(db, Insert(Guid.NewGuid(), Guid.NewGuid(), " "));
    }

    [SqlServerFact]
    public async Task EA03_TextBoundsAndNulls_AreRejectedOnInsert()
    {
        await using var owned = await CreateAsync(migrate: true);
        for (var column = 2; column <= 5; column++)
        {
            foreach (var invalid in new[] { "NULL", "N''", "N' '", "N' leading'", "N'trailing '", "REPLICATE(N'x',513)" })
            {
                var values = new[] { "NEWID()", "NEWID()", "N'bounded'", "N'model'", "N'doc'", "N'policy'", "'Provisioned'", "'2026-10-16T00:00:00'" };
                values[column] = invalid;
                await RejectSqlAsync(owned.Database, $"INSERT {Epochs}(index_epoch,build_id,collection_name,model_version,document_format_version,policy_version,epoch_state,created_at_utc) VALUES({string.Join(',', values)});");
            }
        }
        (await owned.Database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Epochs};")).Should().Be(0);
    }
    [SqlServerFact]
    public async Task EA04_ImmutableTupleAndMultirowLifecycle_AreAtomicAndByteSensitive()
    {
        await using var owned = await CreateAsync(migrate: true);
        var db = owned.Database;
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await db.ExecuteNonQueryAsync(Insert(first, Guid.NewGuid(), "one") + Insert(second, Guid.NewGuid(), "two"));
        foreach (var change in new[] { "index_epoch=NEWID()", "build_id=NEWID()", "collection_name=collection_name+'x'", "model_version='MODEL'", "document_format_version='other'", "policy_version='OTHER'", "created_at_utc=DATEADD(second,1,created_at_utc)" })
            await RejectSqlAsync(db, $"UPDATE {Epochs} SET {change} WHERE index_epoch='{first:D}';", "TM70.A41a.ImmutableBinding");
        await RejectSqlAsync(db, $"UPDATE {Epochs} SET policy_version=CASE WHEN index_epoch='{first:D}' THEN policy_version ELSE 'changed' END, epoch_state='Paused';", "TM70.A41a.ImmutableBinding");
        (await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Epochs} WHERE epoch_state='Provisioned';")).Should().Be(2);
        await db.ExecuteNonQueryAsync($"UPDATE {Epochs} SET index_epoch=index_epoch,policy_version=policy_version,epoch_state=epoch_state;");
        await db.ExecuteNonQueryAsync($"UPDATE {Epochs} SET epoch_state='Paused'; UPDATE {Epochs} SET epoch_state='Paused';");
        await RejectSqlAsync(db, $"UPDATE {Epochs} SET epoch_state='Provisioned';", "TM70.A41a.InvalidTransition");
        await db.ExecuteNonQueryAsync($"UPDATE {Epochs} SET epoch_state='Retired'; UPDATE {Epochs} SET epoch_state='Retired';");
        await RejectSqlAsync(db, $"UPDATE {Epochs} SET epoch_state='Provisioned';", "TM70.A41a.InvalidTransition");
        (await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Epochs} WHERE epoch_state='Retired';")).Should().Be(2);
    }

    [SqlServerFact]
    public async Task EA05_ConcurrentAllocations_AreDistinctPositiveValues()
    {
        await using var owned = await CreateAsync(migrate: true);
        using var ready = new CountdownEvent(8);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var c = new SqlConnection(owned.Database.ConnectionString); await c.OpenAsync();
            ready.Signal(); await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return Convert.ToInt64(await new SqlCommand($"SELECT NEXT VALUE FOR {Sequence};", c).ExecuteScalarAsync());
        }).ToArray();
        var allReady = ready.Wait(TimeSpan.FromSeconds(30));
        release.TrySetResult();
        var values = await Task.WhenAll(tasks);
        allReady.Should().BeTrue();
        values.Should().OnlyHaveUniqueItems().And.OnlyContain(x => x > 0);
    }

    [SqlServerFact]
    public async Task EA06_Rollback_DoesNotReuseConsumedGeneration()
    {
        await using var owned = await CreateAsync(migrate: true);
        var db = owned.Database;
        await using var c = new SqlConnection(db.ConnectionString); await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var consumed = Convert.ToInt64(await new SqlCommand($"SELECT NEXT VALUE FOR {Sequence};", c, tx).ExecuteScalarAsync());
        await new SqlCommand(Insert(Guid.NewGuid(), Guid.NewGuid(), "rolled-back"), c, tx).ExecuteNonQueryAsync();
        await tx.RollbackAsync();
        (await NextAsync(db)).Should().BeGreaterThan(consumed);
        (await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Epochs};")).Should().Be(0);
    }

    [SqlServerFact]
    public async Task EA07_OrderedObservationsAndEA08_Reconnect_DoNotResetSequenceOrEpoch()
    {
        await using var owned = await CreateAsync(migrate: true);
        var db = owned.Database;
        var epoch = Guid.NewGuid(); await db.ExecuteNonQueryAsync(Insert(epoch, Guid.NewGuid(), "reconnect"));
        await using var ordered = new SqlConnection(db.ConnectionString); await ordered.OpenAsync();
        var observed = new List<long>();
        var competing = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            observed.Add(Convert.ToInt64(await new SqlCommand($"SELECT NEXT VALUE FOR {Sequence};", ordered).ExecuteScalarAsync()));
            var observedCheckpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var competitor = Task.Run(async () => { await observedCheckpoint.Task; return await NextAsync(db); });
            observedCheckpoint.SetResult();
            competing.Add(await competitor.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        observed.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        observed.Concat(competing).Should().OnlyHaveUniqueItems();
        for (var i = 1; i < observed.Count; i++) observed[i].Should().BeGreaterThan(competing[i - 1]);
        (await NextAsync(db)).Should().BeGreaterThan(competing[^1]);
        (await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Epochs} WHERE index_epoch='{epoch:D}' AND epoch_state='Provisioned';")).Should().Be(1);
    }

    [SqlServerFact]
    public async Task EA09_ActualMigratedTargetSequence_ExhaustsAtBigintBoundary()
    {
        await using var owned = await CreateAsync(migrate: true);
        await AssertSequenceAsync(owned.Database);
        await AssertStorageAsync(owned.Database.ConnectionString, allowOwnSessions: true);
        // Explicitly authorized boundary mutation: only the current run's dedicated database.
        await owned.Database.ExecuteNonQueryAsync($"ALTER SEQUENCE {Sequence} RESTART WITH 9223372036854775807;");
        (await NextAsync(owned.Database)).Should().Be(long.MaxValue);
        var act = async () => await NextAsync(owned.Database);
        var error = await act.Should().ThrowAsync<SqlException>(); error.Which.Number.Should().Be(11728);
        output.WriteLine("EA09 actual target, no clone; metadata verified before test-only RESTART.");
    }

    [SqlServerFact]
    public async Task EA10_CompatibleRerun_PreservesObjectsEpochRowversionAndProgress()
    {
        await using var owned = await CreateAsync(migrate: true);
        var db = owned.Database;
        await db.ExecuteNonQueryAsync(Insert(Guid.NewGuid(), Guid.NewGuid(), "rerun"));
        var used = await NextAsync(db);
        var before = await TextAsync(db, $"SELECT CONCAT(OBJECT_ID(N'{Epochs}'),':',OBJECT_ID(N'{Sequence}'),':',CONVERT(varchar(32),row_version,2),':',CONVERT(varchar(64),s.current_value)) FROM {Epochs} CROSS JOIN sys.sequences s WHERE s.object_id=OBJECT_ID(N'{Sequence}');");
        await ApplyAsync(db);
        (await TextAsync(db, $"SELECT CONCAT(OBJECT_ID(N'{Epochs}'),':',OBJECT_ID(N'{Sequence}'),':',CONVERT(varchar(32),row_version,2),':',CONVERT(varchar(64),s.current_value)) FROM {Epochs} CROSS JOIN sys.sequences s WHERE s.object_id=OBJECT_ID(N'{Sequence}');")).Should().Be(before);
        (await NextAsync(db)).Should().BeGreaterThan(used);
    }

    [SqlServerFact]
    public async Task EA11_PartialSequenceOnly_IsNotRepaired()
    {
        await using var owned = await CreateAsync();
        var db = owned.Database;
        await db.ExecuteNonQueryAsync($"CREATE SEQUENCE {Sequence} AS bigint START WITH 1 INCREMENT BY 1 MINVALUE 1 MAXVALUE 9223372036854775807 NO CYCLE NO CACHE;");
        var used = await NextAsync(db);
        await RejectMigrationAsync(db, "TM70.A41a.PartialSchema");
        (await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'{Epochs}');")).Should().Be(0);
        (await NextAsync(db)).Should().BeGreaterThan(used);
    }

    [SqlServerTheory]
    [InlineData("ALTER SEQUENCE commerce.TourIndexGenerationSequence CACHE 10;")]
    [InlineData("ALTER TABLE commerce.TourIndexEpochs NOCHECK CONSTRAINT CK_TourIndexEpochs_Model; ALTER TABLE commerce.TourIndexEpochs CHECK CONSTRAINT CK_TourIndexEpochs_Model;")]
    [InlineData("ALTER TABLE commerce.TourIndexEpochs DROP CONSTRAINT CK_TourIndexEpochs_Model; ALTER TABLE commerce.TourIndexEpochs WITH CHECK ADD CONSTRAINT CK_TourIndexEpochs_Model CHECK (1=1);")]
    [InlineData("CREATE INDEX IX_TourIndexEpochs_Extra ON commerce.TourIndexEpochs(model_version);")]
    [InlineData("ALTER TABLE commerce.TourIndexEpochs DROP CONSTRAINT CK_TourIndexEpochs_Policy; ALTER TABLE commerce.TourIndexEpochs ALTER COLUMN policy_version nvarchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL; ALTER TABLE commerce.TourIndexEpochs WITH CHECK ADD CONSTRAINT CK_TourIndexEpochs_Policy CHECK (DATALENGTH(policy_version)>0 AND DATALENGTH(policy_version)=DATALENGTH(LTRIM(RTRIM(policy_version))));")]
    [InlineData("ALTER SEQUENCE commerce.TourIndexGenerationSequence CYCLE;")]
    [InlineData("ALTER TABLE commerce.TourIndexEpochs NOCHECK CONSTRAINT CK_TourIndexEpochs_InactiveState;")]
    [InlineData("DISABLE TRIGGER commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1 ON commerce.TourIndexEpochs;")]
    [InlineData("ALTER TRIGGER commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1 ON commerce.TourIndexEpochs AFTER INSERT, UPDATE AS RETURN;")]
    [InlineData("ALTER TABLE commerce.TourIndexEpochs DROP CONSTRAINT UQ_TourIndexEpochs_BuildId;")]
    public async Task EA12_IncompatibleDefinition_IsRejectedWithoutRepair(string corruption)
    {
        await using var owned = await CreateAsync(migrate: true);
        await owned.Database.ExecuteNonQueryAsync(corruption);
        var before = await Uc35InventoryAsync(owned.Database);
        await RejectMigrationAsync(owned.Database, string.Equals(corruption, "ALTER TABLE commerce.TourIndexEpochs DROP CONSTRAINT UQ_TourIndexEpochs_BuildId;", StringComparison.Ordinal) ? "TM70.A41a.PartialSchema" : "TM70.A41a.IncompatibleSchema");
        (await Uc35InventoryAsync(owned.Database)).Should().Be(before);
    }

    [SqlServerTheory]
    [InlineData("DROP TRIGGER commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1;")]
    [InlineData("ALTER TABLE commerce.TourIndexEpochs DROP CONSTRAINT CK_TourIndexEpochs_Model;")]
    [InlineData("DROP TRIGGER commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1; DROP SEQUENCE commerce.TourIndexGenerationSequence;")]
    public async Task EA11_PartialSubset_IsNotCompletedOrRepaired(string corruption)
    {
        await using var owned = await CreateAsync(migrate: true);
        await owned.Database.ExecuteNonQueryAsync(corruption);
        await RejectMigrationAsync(owned.Database, "TM70.A41a.PartialSchema");
        (await owned.Database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Epochs};")).Should().Be(0);
    }

    [SqlServerFact]
    public async Task EA12_NameOnlyView_IsRejectedBeforeCreatingOtherObjects()
    {
        await using var owned = await CreateAsync();
        await owned.Database.ExecuteNonQueryAsync($"CREATE VIEW {Epochs} AS SELECT 1 AS wrong_shape;");
        await RejectMigrationAsync(owned.Database, "TM70.A41a.IncompatibleSchema");
        (await owned.Database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM sys.sequences WHERE object_id=OBJECT_ID(N'{Sequence}');")).Should().Be(0);
    }

    [SqlServerTheory]
    [InlineData("EXEC sys.sp_rename N'commerce.CK_TourIndexEpochs_Model',N'ck_TourIndexEpochs_Model',N'OBJECT';")]
    [InlineData("EXEC sys.sp_rename N'commerce.TourIndexGenerationSequence',N'tourIndexGenerationSequence',N'OBJECT';")]
    [InlineData("EXEC sys.sp_rename N'commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1',N'tr_TourIndexEpochs_BindingAndInactiveLifecycle_v1',N'OBJECT';")]
    [InlineData("EXEC sys.sp_rename N'commerce.TourIndexEpochs',N'tourIndexEpochs',N'OBJECT';")]
    public async Task EA12_ExactNames_RejectCaseOnlyRenames(string corruption)
    {
        await using var owned = await CreateAsync(migrate: true);
        await owned.Database.ExecuteNonQueryAsync(corruption);
        await RejectMigrationAsync(owned.Database, "TM70.A41a.IncompatibleSchema");
    }
    [SqlServerTheory]
    [InlineData("EXEC sys.sp_rename N'commerce.CK_TourIndexEpochs_Model',N'CK_TourIndexEpochs_Model ',N'OBJECT';")]
    [InlineData("EXEC sys.sp_rename N'commerce.TourIndexGenerationSequence',N'TourIndexGenerationSequence ',N'OBJECT';")]
    public async Task EA12_ExactBytes_RejectTrailingSpaceNames(string corruption)
    {
        await using var owned = await CreateAsync(migrate: true);
        await owned.Database.ExecuteNonQueryAsync(corruption);
        await RejectMigrationAsync(owned.Database, "TM70.A41a.IncompatibleSchema");
    }
    [SqlServerFact]
    public async Task EA13_TransactionLock_SerializesMigrationAndRejectsLockErrors()
    {
        await using var owned = await CreateAsync();
        await using var a = new SqlConnection(owned.Database.ConnectionString); await a.OpenAsync();
        await using var tx = (SqlTransaction)await a.BeginTransactionAsync();
        var lockSql = $"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'{LockResource}',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0,@DbPrincipal='public'; SELECT @r;";
        Convert.ToInt32(await new SqlCommand(lockSql, a, tx).ExecuteScalarAsync()).Should().BeGreaterThanOrEqualTo(0);
        var migration = ApplyAsync(owned.Database);
        await WaitForLockWaitAsync(a, tx);
        migration.IsCompleted.Should().BeFalse();
        await tx.CommitAsync(); await migration.WaitAsync(TimeSpan.FromSeconds(30));
        // A second runner now validates the committed schema, rather than racing CREATE.
        await ApplyAsync(owned.Database);
        await using var b = new SqlConnection(owned.Database.ConnectionString); await b.OpenAsync();
        await using var lockTx = (SqlTransaction)await a.BeginTransactionAsync();
        await new SqlCommand(lockSql, a, lockTx).ExecuteScalarAsync();
        var text = await File.ReadAllTextAsync(Path.Combine(Root(), "database", "migrations", Migration));
        text.Should().Contain("@LockTimeout = 15000");
        var zeroWaitText = text.Replace("@LockTimeout = 15000", "@LockTimeout = 0", StringComparison.Ordinal);
        var act = () => ExecuteScriptOnConnectionAsync(b, zeroWaitText);
        await act.Should().ThrowAsync<SqlException>().WithMessage("*TM70.A41a.MigrationLock*");
        Convert.ToInt32(await new SqlCommand("SELECT @@TRANCOUNT;", b).ExecuteScalarAsync()).Should().Be(0);
        await lockTx.RollbackAsync();
        await ApplyAsync(owned.Database);
    }

    [SqlServerFact]
    public async Task EA14_UtcRowversionAndRollback_DoNotPromiseContiguousTokens()
    {
        await using var owned = await CreateAsync(migrate: true);
        var db = owned.Database;
        await db.ExecuteNonQueryAsync(Insert(Guid.NewGuid(), Guid.NewGuid(), "utc"));
        var first = await TextAsync(db, $"SELECT CONVERT(varchar(32),row_version,2) FROM {Epochs};");
        await db.ExecuteNonQueryAsync($"UPDATE {Epochs} SET epoch_state=epoch_state;");
        (await TextAsync(db, $"SELECT CONVERT(varchar(32),row_version,2) FROM {Epochs};")).Should().NotBe(first);
        await db.ExecuteNonQueryAsync($"BEGIN TRANSACTION; UPDATE {Epochs} SET epoch_state='Paused'; ROLLBACK TRANSACTION;");
        (await TextAsync(db, $"SELECT epoch_state FROM {Epochs};")).Should().Be("Provisioned");
        (await TextAsync(db, $"SELECT CONVERT(varchar(40),created_at_utc,126) FROM {Epochs};")).Should().Be("2026-10-16T00:00:00.1234567");
    }

    // Pure RED/GREEN tests do not contact Docker or SQL and do not trust CI env flags.
    [Fact]
    public void EA16_CiOwnershipProof_RequiresLiveRunnerAndContainerEvidence()
    {
        ValidateCiContainer(CiContainerFixture(), "{\"Labels\":{\"abc123\":\"\"}}", "abc123", DateTimeOffset.Parse("2026-10-16T00:00:00Z"))
            .Should().Be("owned-sql");
    }

    [Theory]
    [InlineData("\"abc123\":\"\"", "\"wrong\":\"\"")]
    [InlineData("\"Running\":true", "\"Running\":false")]
    [InlineData("mcr.microsoft.com/mssql/server:2022-latest", "other/image:latest")]
    [InlineData("\"Mounts\":[]", "\"Mounts\":[{\"Type\":\"bind\"}]")]
    [InlineData("github_network_0123456789abcdef0123456789abcdef", "shared-network")]
    [InlineData("\"HostPort\":\"1433\"", "\"HostPort\":\"1434\"")]
    [InlineData("2026-10-16T00:01:00Z", "2026-10-15T00:01:00Z")]
    [InlineData("\"sqlserver\"", "\"other\"")]
    public void EA16_CiOwnershipProof_RejectsUnownedOrSharedTargets(string from, string to)
    {
        var act = () => ValidateCiContainer(CiContainerFixture().Replace(from, to, StringComparison.Ordinal), "{\"Labels\":{\"abc123\":\"\"}}", "abc123", DateTimeOffset.Parse("2026-10-16T00:00:00Z"));
        act.Should().Throw<InvalidOperationException>();
    }

    private static string CiContainerFixture() => """
        {"Id":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","Hostname":"owned-sql","Image":"mcr.microsoft.com/mssql/server:2022-latest","Labels":{"abc123":""},"Running":true,"Created":"2026-10-16T00:01:00Z","Mounts":[],"Ports":{"1433/tcp":[{"HostIp":"0.0.0.0","HostPort":"1433"}]},"Networks":{"github_network_0123456789abcdef0123456789abcdef":{"Aliases":["sqlserver","owned-sql"]}}}
        """;

    private static string ValidateCiContainer(string containerJson, string networkJson, string runnerLabel, DateTimeOffset jobStarted)
    {
        static void Require(bool valid) { if (!valid) throw new InvalidOperationException("CI SQL service ownership evidence is missing or incompatible."); }
        using var document = JsonDocument.Parse(containerJson);
        using var networkDocument = JsonDocument.Parse(networkJson);
        var c = document.RootElement;
        Require(Regex.IsMatch(c.GetProperty("Id").GetString() ?? "", "^[a-f0-9]{64}$"));
        Require(c.GetProperty("Image").GetString() == "mcr.microsoft.com/mssql/server:2022-latest");
        Require(c.GetProperty("Running").GetBoolean());
        Require(c.GetProperty("Labels").TryGetProperty(runnerLabel, out _));
        Require(c.GetProperty("Mounts").GetArrayLength() == 0);
        Require(c.GetProperty("Created").GetDateTimeOffset() >= jobStarted);
        var ports = c.GetProperty("Ports").GetProperty("1433/tcp");
        Require(ports.EnumerateArray().Any(x => x.GetProperty("HostPort").GetString() == "1433"
            && x.GetProperty("HostIp").GetString() is "0.0.0.0" or "127.0.0.1"));
        var networks = c.GetProperty("Networks").EnumerateObject().ToArray();
        Require(networks.Length == 1 && Regex.IsMatch(networks[0].Name, "^github_network_[a-f0-9]{32}$"));
        Require(networks[0].Value.GetProperty("Aliases").EnumerateArray().Any(x => x.GetString() == "sqlserver"));
        Require(networkDocument.RootElement.GetProperty("Labels").TryGetProperty(runnerLabel, out _));
        var hostname = c.GetProperty("Hostname").GetString() ?? "";
        Require(Regex.IsMatch(hostname, "^[a-zA-Z0-9-]{1,63}$"));
        return hostname;
    }

    private static async Task<string> VerifyGitHubServiceAsync(SqlConnectionStringBuilder connection)
    {
        if (!OperatingSystem.IsLinux()
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"
            || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "FPTUCapstone/Capstone_BE"
            || Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") != "Backend tests"
            || Environment.GetEnvironmentVariable("GITHUB_JOB") != "test"
            || connection.IntegratedSecurity || connection.UserID != "sa"
            || connection.DataSource is not ("localhost,1433" or "127.0.0.1,1433"))
            throw new InvalidOperationException("Only the approved local instance or the owned GitHub Actions SQL service is authorized.");
        var pid = Environment.ProcessId;
        string? runnerRoot = null;
        DateTimeOffset jobStarted = default;
        for (var depth = 0; depth < 64 && pid > 1; depth++)
        {
            var executable = File.ResolveLinkTarget($"/proc/{pid}/exe", true)?.FullName;
            if (Path.GetFileName(executable) == "Runner.Worker")
            {
                runnerRoot = Directory.GetParent(Path.GetDirectoryName(executable)!)?.FullName;
                using var worker = Process.GetProcessById(pid);
                jobStarted = worker.StartTime.ToUniversalTime();
                break;
            }
            var stat = await File.ReadAllTextAsync($"/proc/{pid}/stat");
            pid = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        }
        if (runnerRoot is null) throw new InvalidOperationException("Live GitHub runner ancestry not verified.");
        // actions/runner DockerCommandManager.Initialize + IOUtil.GetSha256Hash protocol.
        var configuration = await File.ReadAllTextAsync(Path.Combine(runnerRoot, ".runner"));
        var label = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration.ToLowerInvariant()))).ToLowerInvariant()[..6];
        var ids = (await DockerReadAsync("ps", "--quiet", "--no-trunc", "--filter", "label=" + label,
            "--filter", "ancestor=mcr.microsoft.com/mssql/server:2022-latest")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length != 1 || !Regex.IsMatch(ids[0], "^[a-f0-9]{64}$"))
            throw new InvalidOperationException("Exactly one current-runner SQL container must be present.");
        const string format = "{\"Id\":{{json .Id}},\"Hostname\":{{json .Config.Hostname}},\"Image\":{{json .Config.Image}},\"Labels\":{{json .Config.Labels}},\"Running\":{{json .State.Running}},\"Created\":{{json .Created}},\"Mounts\":{{json .Mounts}},\"Ports\":{{json .NetworkSettings.Ports}},\"Networks\":{{json .NetworkSettings.Networks}}}";
        var containerJson = await DockerReadAsync("inspect", "--format", format, ids[0]);
        using var container = JsonDocument.Parse(containerJson);
        var networks = container.RootElement.GetProperty("Networks").EnumerateObject().ToArray();
        if (networks.Length != 1 || !Regex.IsMatch(networks[0].Name, "^github_network_[a-f0-9]{32}$"))
            throw new InvalidOperationException("Job-local container network not verified.");
        var networkJson = await DockerReadAsync("network", "inspect", "--format", "{\"Labels\":{{json .Labels}}}", networks[0].Name);
        return ValidateCiContainer(containerJson, networkJson, label, jobStarted);
    }

    private static async Task<string> DockerReadAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--host"); start.ArgumentList.Add("unix:///var/run/docker.sock");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Local Docker evidence unavailable.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Local Docker read-only ownership probe failed.");
        return await stdout;
    }
    [SqlServerFact]
    public async Task EA16_PinnedGitSnapshot_HasOnlyApprovedHistoricalScripts()
    {
        var scripts = await ReadPinnedScriptsAsync();
        scripts.Count.Should().Be(33);
        scripts[0].Should().Contain("CREATE");
        scripts[^1].Should().Contain("destination_summary");
    }

    private static async Task<IReadOnlyList<string>> ReadPinnedScriptsAsync()
    {
        await ReadGitAsync("cat-file", "-e", PinnedSource + "^{commit}");
        var manifest = Encoding.UTF8.GetString(await ReadGitAsync("ls-tree", "-r", "--name-only", PinnedSource, "--", "database/migrations"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var expected = PriorMigrations.Select(x => "database/migrations/" + x).ToArray();
        if (!manifest.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("BLOCKED: pinned Git migration manifest does not match the approved 32 scripts.");
        var paths = new[] { "database/tripmate_schema_v7.sql" }.Concat(expected).ToArray();
        var scripts = new List<string>(paths.Length);
        for (var i = 0; i < paths.Length; i++)
        {
            var bytes = await ReadGitAsync("cat-file", "blob", PinnedSource + ":" + paths[i]);
            VerifyPinnedHash(i, bytes);
            scripts.Add(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
        }
        return scripts.AsReadOnly();
    }

    [Fact]
    public void EA16_PinnedGitHash_RejectsTamperedBytes()
    {
        var act = () => VerifyPinnedHash(0, [0, 1, 2]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*pinned Git resource hash*");
    }

    private static void VerifyPinnedHash(int index, byte[] bytes)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), PinnedSha256[index], StringComparison.Ordinal))
            throw new InvalidOperationException("BLOCKED: pinned Git resource hash does not match the approved snapshot.");
    }

    private static async Task<byte[]> ReadGitAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("BLOCKED: Git CLI unavailable; no working-tree fallback.");
        using var bytes = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(copy, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException("BLOCKED: pinned Git object unavailable; no working-tree fallback.");
        return bytes.ToArray();
    }
    private static string Insert(Guid epoch, Guid build, string collection, string state = "Provisioned") =>
        $"INSERT {Epochs}(index_epoch,build_id,collection_name,model_version,document_format_version,policy_version,epoch_state,created_at_utc) VALUES('{epoch:D}','{build:D}',N'{collection}',N'model',N'tm70-public-fields-v1',N'tm70-a41a-synthetic-v1','{state}','2026-10-16T00:00:00.1234567');";
    private static Task<long> NextAsync(SqlServerTestDatabase db) => db.ExecuteScalarAsync<long>($"SELECT NEXT VALUE FOR {Sequence};");
    private static async Task<string> TextAsync(SqlServerTestDatabase db, string sql)
    { await using var c = new SqlConnection(db.ConnectionString); await c.OpenAsync(); return Convert.ToString(await new SqlCommand(sql, c).ExecuteScalarAsync()) ?? ""; }
    private static async Task RejectSqlAsync(SqlServerTestDatabase db, string sql, string? message = null)
    { var act = () => db.ExecuteNonQueryAsync(sql); var error = await act.Should().ThrowAsync<SqlException>(); if (message is not null) error.Which.Message.Should().Contain(message); }
    private static async Task RejectMigrationAsync(SqlServerTestDatabase db, string message)
    { var act = () => ApplyAsync(db); await act.Should().ThrowAsync<SqlException>().WithMessage("*" + message + "*"); }
    private static Task ApplyAsync(SqlServerTestDatabase db) => db.ExecuteScriptAsync(Path.Combine(Root(), "database", "migrations", Migration));
    private static async Task AssertSequenceAsync(SqlServerTestDatabase db)
    {
        (await TextAsync(db, $"SELECT CONCAT(system_type_id,':',CONVERT(varchar(64),start_value),':',CONVERT(varchar(64),minimum_value),':',CONVERT(varchar(64),maximum_value),':',CONVERT(varchar(64),increment),':',is_cycling,':',is_cached,':',COALESCE(CONVERT(varchar(64),cache_size),'NULL')) FROM sys.sequences WHERE object_id=OBJECT_ID(N'{Sequence}');"))
            .Should().Be("127:1:1:9223372036854775807:1:0:0:NULL");
    }
    private static async Task<string> Uc35InventoryAsync(SqlServerTestDatabase db) => await TextAsync(db, "SELECT (SELECT o.name,o.type,COALESCE(OBJECT_DEFINITION(o.object_id),'') AS definition FROM sys.objects o WHERE o.is_ms_shipped=0 AND o.object_id NOT IN (COALESCE(OBJECT_ID(N'commerce.TourIndexEpochs'),-1),COALESCE(OBJECT_ID(N'commerce.TourIndexGenerationSequence'),-1)) AND o.parent_object_id<>COALESCE(OBJECT_ID(N'commerce.TourIndexEpochs'),-1) ORDER BY o.object_id FOR JSON PATH);");
    private static string Root()
    { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null) { if (File.Exists(Path.Combine(d.FullName, "TripMate.slnx"))) return d.FullName; d = d.Parent; } throw new InvalidOperationException("Repository root not found."); }

    private async Task<OwnedDatabase> CreateAsync(bool migrate = false)
    {
        var scripts = await PinnedScripts.Value; // Verify all Git objects/hashes BEFORE SQL writes.
        var configured = Environment.GetEnvironmentVariable(ConnectionEnvironment) ?? throw new InvalidOperationException("SQL opt-in required.");
        await AssertStorageAsync(configured);
        var existing = await DatabaseNamesAsync(configured);
        var db = await SqlServerTestDatabase.CreateEmptyAsync();
        var owned = new OwnedDatabase(db, configured, output);
        try
        {
            existing.Should().NotContain(db.DatabaseName);
            db.DatabaseName.Should().MatchRegex("^TripMate_Test_[0-9a-f]{32}$");
            output.WriteLine("OWNED_CREATED " + db.DatabaseName);
            owned.Record("CREATED");
            foreach (var script in scripts)
            {
                await using var bootstrap = new SqlConnection(db.ConnectionString);
                await bootstrap.OpenAsync();
                await ExecuteScriptOnConnectionAsync(bootstrap, script);
            }
            (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'commerce.Tours') AND name='destination_summary' AND max_length=600 AND is_nullable=0;")).Should().Be(1);
            (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'commerce.Tours') AND name='CK_Tours_Status' AND definition LIKE '%Published%';")).Should().Be(1);
            if (migrate) await ApplyAsync(db);
            return owned;
        }
        catch { await owned.DisposeAsync(); throw; }
    }
    private static async Task AssertStorageAsync(string connectionString, bool allowOwnSessions = false)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        string machine, prefix, principal;
        var localWindows = OperatingSystem.IsWindows();
        if (OperatingSystem.IsWindows())
        {
            if (!builder.IntegratedSecurity) throw new InvalidOperationException("Approved local Windows principal required.");
            machine = "DAT"; prefix = @"D:\"; principal = WindowsIdentity.GetCurrent().Name;
        }
        else
        {
            machine = await VerifyGitHubServiceAsync(builder);
            prefix = "/var/opt/mssql/"; principal = "sa";
        }
        await using var c = new SqlConnection(builder.ConnectionString); await c.OpenAsync();
        const string sql = "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')),CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),ORIGINAL_LOGIN(),(SELECT COUNT(*) FROM sys.master_files WHERE LEFT(physical_name,LEN(@prefix))<>@prefix),(SELECT MIN(v.available_bytes) FROM sys.master_files f CROSS APPLY sys.dm_os_volume_stats(f.database_id,f.file_id) v),(SELECT SUM(CONVERT(bigint,size))*8192 FROM sys.master_files WHERE database_id=3),(SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process=1 AND session_id<>@@SPID);";
        await using var command = new SqlCommand(sql, c);
        command.Parameters.AddWithValue("@prefix", prefix);
        await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
        reader.GetString(0).Should().BeEquivalentTo(machine);
        if (localWindows) reader.GetString(1).Should().BeEquivalentTo("MSSQLSERVER01"); else reader.IsDBNull(1).Should().BeTrue();
        reader.GetString(2).Should().Be(principal);
        reader.GetInt32(3).Should().Be(0);
        reader.GetInt64(4).Should().BeGreaterThan(reader.GetInt64(5), "the model allocation must fit before creation; no fabricated reserve threshold");
        if (!allowOwnSessions) reader.GetInt32(6).Should().Be(0, "do not overlap another SQL test workload");
        await reader.DisposeAsync();
        var defaults = Convert.ToString(await new SqlCommand("SELECT CONCAT(CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultDataPath')),'|',CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultLogPath')));", c).ExecuteScalarAsync()) ?? "";
        defaults.Split('|').Should().HaveCount(2).And.OnlyContain(x => x.StartsWith(prefix, localWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }
    private static async Task<HashSet<string>> DatabaseNamesAsync(string configured)
    {
        var b = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", Pooling = false };
        await using var c = new SqlConnection(b.ConnectionString); await c.OpenAsync();
        await using var r = await new SqlCommand("SELECT name FROM sys.databases;", c).ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.Ordinal); while (await r.ReadAsync()) names.Add(r.GetString(0)); return names;
    }
    private static async Task WaitForLockWaitAsync(SqlConnection observer, SqlTransaction tx)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var count = Convert.ToInt32(await new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id=DB_ID() AND session_id<>@@SPID AND wait_type LIKE 'LCK_M_%';", observer, tx).ExecuteScalarAsync(deadline.Token));
            if (count > 0) return;
            await Task.Yield(); deadline.Token.ThrowIfCancellationRequested();
        }
    }
    private static async Task ExecuteScriptOnConnectionAsync(SqlConnection c, string text)
    {
        foreach (var batch in Regex.Split(text, @"^\s*GO\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        { if (string.IsNullOrWhiteSpace(batch)) continue; await using var cmd = new SqlCommand(batch, c) { CommandTimeout = 120 }; await cmd.ExecuteNonQueryAsync(); }
    }
    private sealed class OwnedDatabase(SqlServerTestDatabase database, string masterConfiguration, ITestOutputHelper output) : IAsyncDisposable
    {
        public SqlServerTestDatabase Database { get; } = database;
        public void Record(string state)
        {
            var line = $"{DateTimeOffset.UtcNow:O}|{Database.DatabaseName}|{state}";
            var directory = Environment.GetEnvironmentVariable(RunManifestEnvironment);
            if (!string.IsNullOrEmpty(directory)) File.AppendAllText(Path.Combine(directory, "owned-databases.log"), line + Environment.NewLine);
        }
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            (await DatabaseNamesAsync(masterConfiguration)).Should().NotContain(Database.DatabaseName);
            Record("REMOVED_AND_ABSENCE_VERIFIED"); output.WriteLine("OWNED_REMOVED " + Database.DatabaseName);
        }
    }
}