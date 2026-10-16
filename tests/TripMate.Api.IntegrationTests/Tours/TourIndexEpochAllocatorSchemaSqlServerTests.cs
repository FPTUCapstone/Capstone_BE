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
        var hostname = ValidateCiContainer(containerJson, networkJson, label, jobStarted);
        VerifiedForensicContainer = ids[0]; // Evidence only; never an isolation-policy exception.
        return hostname;
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
    [Fact]
    public void EA16_CiSessionRule_AllowsOnlyProvenLiveOwnedIdleConnection()
    {
        var session = new IsolationSession(61, Guid.Parse("11111111-1111-1111-1111-111111111111"), "sleeping", "TripMate_Test_0123456789abcdef0123456789abcdef", false, 0, 123, "127.0.0.1", 50000, "Microsoft Data SqlClient", "host", "sa", DateTime.UtcNow);
        ValidateCiIsolation([session], [session.ConnectionId], session.Database, [session.Database]);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("active")]
    [InlineData("transaction")]
    [InlineData("database")]
    [InlineData("unowned-database")]
    public void EA16_CiSessionRule_BlocksUnverifiedWorkloads(string kind)
    {
        const string owned = "TripMate_Test_0123456789abcdef0123456789abcdef";
        var session = new IsolationSession(61, Guid.Parse("11111111-1111-1111-1111-111111111111"), "sleeping", owned, false, 0, 123, "127.0.0.1", 50000, "sqlcmd", "host", "sa", DateTime.UtcNow);
        if (kind == "active") session = session with { HasRequest = true };
        if (kind == "transaction") session = session with { OpenTransactions = 1 };
        if (kind == "database") session = session with { Database = "master" };
        var proof = kind == "unknown" ? Array.Empty<Guid>() : new[] { session.ConnectionId };
        var databases = kind == "unowned-database" ? new[] { owned, "TripMate_Test_ffffffffffffffffffffffffffffffff" } : new[] { owned };
        var act = () => ValidateCiIsolation([session], proof, owned, databases);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EA16_CiSessionRule_EmptyOwnedContainerNeedsNoSessionWhitelist()
        => ValidateCiIsolation([], [], null, []);

    [Fact]
    public void EA16_CiOwnershipProof_DifferentRunnerAndNetworkLabelsRemainBlocked()
    {
        var wrongRunner = () => ValidateCiContainer(CiContainerFixture(), "{\"Labels\":{\"abc123\":\"\"}}", "ffffff", DateTimeOffset.Parse("2026-10-16T00:00:00Z"));
        var wrongNetwork = () => ValidateCiContainer(CiContainerFixture(), "{\"Labels\":{\"ffffff\":\"\"}}", "abc123", DateTimeOffset.Parse("2026-10-16T00:00:00Z"));
        wrongRunner.Should().Throw<InvalidOperationException>();
        wrongNetwork.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EA16_WindowsPolicy_StillBlocksAnyOtherSession()
    {
        ValidateWindowsSessions(0, false);
        var act = () => ValidateWindowsSessions(1, false);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EA16_InstanceIdentity_StillRequiresExactApprovedInstance()
    {
        ValidateServerIdentity(true, "DAT", "MSSQLSERVER01", "DAT");
        ValidateServerIdentity(false, "owned-sql", null, "owned-sql");
        var wrongWindows = () => ValidateServerIdentity(true, "DAT", "OTHER", "DAT");
        var wrongContainer = () => ValidateServerIdentity(false, "other-sql", null, "owned-sql");
        wrongWindows.Should().Throw<InvalidOperationException>();
        wrongContainer.Should().Throw<InvalidOperationException>();
    }

    [SqlServerFact]
    public async Task EA16_RealOwnedConnections_UnknownStaleAndConcurrentCreationRemainBlocked()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionEnvironment)!;
        var baseline = await DatabaseNamesAsync(configured);
        string created;
        await using (var owned = await CreateAsync())
        {
            created = owned.Database.DatabaseName;
            Func<Task> second = async () => { await using var extra = await CreateAsync(); };
            await second.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fixture lease*");
            await using var registered = await owned.OpenVerifiedConnectionAsync();
            var master = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", Pooling = false };
            await using var observer = new SqlConnection(master.ConnectionString);
            await observer.OpenAsync();
            var sessions = await ReadSessionsAsync(observer);
            ValidateCiIsolation(sessions, LiveOwnedConnectionIds(), created, [created]);
            var windows = () => ValidateWindowsSessions(sessions.Count, false);
            windows.Should().Throw<InvalidOperationException>();
            SqlConnection.ClearAllPools();
            ValidateCiIsolation(await ReadSessionsAsync(observer), LiveOwnedConnectionIds(), created, [created]);
            var poolBuilder = new SqlConnectionStringBuilder(owned.Database.ConnectionString) { Pooling = true, MinPoolSize = 0 };
            Guid pooledId;
            await using (var pooled = new SqlConnection(poolBuilder.ConnectionString))
            {
                await pooled.OpenAsync();
                await using var identify = new SqlCommand("SELECT connection_id FROM sys.dm_exec_connections WHERE session_id=@@SPID;", pooled);
                pooledId = (Guid)(await identify.ExecuteScalarAsync())!;
            }
            (await ReadSessionsAsync(observer)).Should().Contain(x => x.ConnectionId == pooledId, "a closed client can leave its own idle pooled server session");
            SqlConnection.ClearAllPools();
            await WaitForVerifiedPoolRetirementAsync(observer, pooledId);
            (await ReadSessionsAsync(observer)).Should().NotContain(x => x.ConnectionId == pooledId, "current-process pool drain closes its own idle physical connection");
            await using (var unknown = new SqlConnection(owned.Database.ConnectionString))
            {
                await unknown.OpenAsync();
                var snapshot = await ReadSessionsAsync(observer);
                snapshot.Should().HaveCount(sessions.Count + 1);
                var act = () => ValidateCiIsolation(snapshot, LiveOwnedConnectionIds(), created, [created]);
                act.Should().Throw<InvalidOperationException>(); // Same process/login/program still has no live-handle proof.
            }
            await using (var tx = await registered.Connection.BeginTransactionAsync())
            {
                var snapshot = await ReadSessionsAsync(observer);
                var act = () => ValidateCiIsolation(snapshot, LiveOwnedConnectionIds(), created, [created]);
                act.Should().Throw<InvalidOperationException>();
                await tx.RollbackAsync();
            }
            await registered.Connection.ChangeDatabaseAsync("master");
            var wrongDatabase = await ReadSessionsAsync(observer);
            var changed = () => ValidateCiIsolation(wrongDatabase, LiveOwnedConnectionIds(), created, [created]);
            changed.Should().Throw<InvalidOperationException>();
            await registered.Connection.CloseAsync();
            LiveOwnedConnectionIds().Should().BeEmpty();
            await registered.Connection.OpenAsync();
            LiveOwnedConnectionIds().Should().BeEmpty("reopening invalidates the captured client connection identity");
            var reopened = await ReadSessionsAsync(observer);
            var stale = () => ValidateCiIsolation(reopened, LiveOwnedConnectionIds(), created, [created]);
            stale.Should().Throw<InvalidOperationException>();
            output.WriteLine("CONTROLLED_SQL_EVIDENCE: live DMV connection binding accepted; unregistered/same-process, transaction, wrong DB, stale handle and second creation blocked; Windows policy preserved. This is not a Linux guard bypass.");
        }
        var after = await DatabaseNamesAsync(configured);
        after.Should().NotContain(created);
        after.Should().BeEquivalentTo(baseline, "only the current-run database may be removed");
    }
    [Theory]
    [InlineData("0100007F:A2AB", "0100007F:0599", 41643, true)]
    [InlineData("0100007F:A2AC", "0100007F:0599", 41643, false)]
    [InlineData("0200007F:A2AB", "0100007F:0599", 41643, false)]
    [InlineData("0100007F:0599", "0100007F:A2AB", 41643, false)]
    [InlineData("0000000000000000FFFF00000100007F:A2AB", "0000000000000000FFFF00000100007F:0599", 41643, true)]
    [InlineData("malformed", "0100007F:0599", 41643, false)]
    public void EA17_ForensicSocketCorrelation_RequiresExactClientDirection(string local, string remote, int port, bool expected)
        => ForensicSocketMatches(local, remote, "127.0.0.1", port).Should().Be(expected);

    [Fact]
    public void EA17_ForensicOutput_RejectsRawSecretBearingOrMalformedLines()
    {
        var act = () => SanitizeForensicProbe("PROCESS|1|2|3|SqlServerEngine|" + new string('a', 64) + "|4|5|" + new string('b', 64) + "\nRAW|Password=sentinel-not-a-secret");
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("sentinel");
    }

    [Fact]
    public void EA17_ForensicOutput_AcceptsOnlyBoundedTypedRecords()
    {
        const string line = "SOCKET|tcp|0100007F:A2AB|0100007F:0599|01|12345";
        SanitizeForensicProbe(line).Should().BeEquivalentTo([line]);
        var act = () => SanitizeForensicProbe(new string('x', 65537));
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("FdMissing")]
    [InlineData("GlobUnexpanded")]
    [InlineData("ReadlinkPermissionMessage")]
    [InlineData("ProcessGone")]
    [InlineData("StartTicksInvalid")]
    [InlineData("ProcessChanged")]
    [InlineData("NamespaceMismatch")]
    [InlineData("SocketInodeMismatch")]
    [InlineData("ReadlinkUnclassified")]
    public void EA18_FinalDiagnosticSchema_AcceptsExplicitLimitations(string category)
        => SanitizeForensicProbe($"DIAG|{category}|1").Should().ContainSingle();

    [Theory]
    [InlineData("Valid", "LINK|2|7|123|1|10|10")]
    [InlineData("FdMissing", "DIAG|FdMissing|1")]
    [InlineData("GlobUnexpanded", "DIAG|GlobUnexpanded|1")]
    [InlineData("ReadlinkPermissionMessage", "DIAG|ReadlinkPermissionMessage|1")]
    [InlineData("ProcessGone", "DIAG|ProcessGone|1")]
    [InlineData("StartTicksInvalid", "DIAG|StartTicksInvalid|1")]
    [InlineData("ProcessChanged", "DIAG|ProcessChanged|1")]
    [InlineData("NamespaceMismatch", "DIAG|NamespaceMismatch|1")]
    [InlineData("SocketInodeMismatch", "DIAG|SocketInodeMismatch|1")]
    [InlineData("ReadlinkUnclassified", "DIAG|ReadlinkUnclassified|1")]
    [InlineData("StaleSocket", "DIAG|SocketInodeMismatch|1")]
    [InlineData("ReuseBeforeScan", "DIAG|ProcessChanged|1")]
    public async Task EA18_ActualProcCollector_DistinguishesSyntheticFailures(string scenario, string expected)
    {
        // Execute the actual shipped shell against a synthetic proc tree, never real SQL/Docker.
        var root = Path.Combine(Path.GetTempPath(), "tm70-forensic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        static string Stat(int ticks) => "2 (synthetic) S 1 " + string.Join(' ', Enumerable.Repeat("0", 17)) + " " + ticks + "\n";
        void Put(string relative, string text)
        {
            var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        try
        {
            Put("stat", "btime 100\n");
            foreach (var pid in new[] { "1", "2", "self" })
            {
                Put(pid + "/stat", Stat(10));
                Put(pid + "/status", "Uid:\t10001\t10001\t10001\t10001\nNSpid:\t" + (pid == "self" ? "3" : pid) + "\n");
                Put(pid + "/ns/pid", scenario == "NamespaceMismatch" && pid == "2" ? "pid:[200]\n" : "pid:[100]\n");
                Put(pid + "/ns/net", "net:[101]\n");
                Put(pid + "/exe", scenario == "ReuseBeforeScan" && pid == "2" ? "REUSEMETA" : "/opt/mssql/bin/sqlservr\n");
                Put(pid + "/cgroup", "0::/synthetic\n");
                Directory.CreateDirectory(Path.Combine(root, pid, "fd"));
            }
            Put("net/tcp", "0: 0100007F:8287 0100007F:0599 01 0 0 0 0 0 123\n");
            Put("net/tcp6", "");
            Put("1/fd/0", "pipe:[9]");
            if (scenario != "GlobUnexpanded") Put("2/fd/7", scenario switch
            {
                "ReadlinkPermissionMessage" => "DENY",
                "ProcessGone" => "EXIT",
                "ProcessChanged" => "REUSE",
                "SocketInodeMismatch" => "socket:[456]",
                "StaleSocket" => "STALE",
                "ReadlinkUnclassified" => "UNKNOWN",
                _ => "socket:[123]"
            });
            if (scenario == "FdMissing") Directory.Delete(Path.Combine(root, "2", "fd"), true);
            if (scenario == "StartTicksInvalid") Put("2/stat", "2 (synthetic) S 1\n");
            Put("bin/readlink", """
                #!/bin/sh
                [ "$1" != -v ] || shift
                [ "$1" != -- ] || shift
                f=$1
                if [ ! -f "$f" ]; then printf 'readlink: unavailable\n' >&2; exit 1; fi
                v=$(cat "$f")
                case "$v" in
                  DENY) printf 'readlink: Permission denied\n' >&2; exit 1;;
                  EXIT) rm -f "${f%/fd/*}/stat"; printf 'readlink: No such file or directory\n' >&2; exit 1;;
                  REUSE) sed 's/ 10$/ 42/' "${f%/fd/*}/stat" > "${f%/fd/*}/changed"; mv "${f%/fd/*}/changed" "${f%/fd/*}/stat"; printf 'socket:[123]\n';;
                  REUSEMETA) sed 's/ 10$/ 42/' "${f%/exe}/stat" > "${f%/exe}/changed"; mv "${f%/exe}/changed" "${f%/exe}/stat"; printf '/opt/mssql/bin/sqlservr\n';;
                  STALE) sed 's/ 123$/ 456/' "$TM70_FAKE_PROC/net/tcp" > "$TM70_FAKE_PROC/net/changed"; mv "$TM70_FAKE_PROC/net/changed" "$TM70_FAKE_PROC/net/tcp"; printf 'socket:[123]\n';;
                  UNKNOWN) printf 'Password=sentinel-diagnostic-private\n' >&2; exit 1;;
                  *) printf '%s\n' "$v";;
                esac
                """);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(root, "bin/readlink"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var shell = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "sh.exe") : "/bin/sh";
            var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.Environment["TM70_FAKE_PROC"] = root.Replace('\\', '/');
            var separator = OperatingSystem.IsWindows() ? ";" : ":";
            start.Environment["PATH"] = Path.Combine(root, "bin").Replace('\\', '/') + separator + Path.GetDirectoryName(shell)!.Replace('\\', '/') + separator + start.Environment["PATH"];
            Put("collector.sh", ForensicProcScript.Replace("/proc", "${TM70_FAKE_PROC}", StringComparison.Ordinal));
            foreach (var arg in new[] { Path.Combine(root, "collector.sh").Replace('\\', '/'), "33415", "2", "0100007F:8287", "0000000000000000FFFF00000100007F:8287" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            var result = await stdout; var syntheticError = await stderr;
            var errorKinds = Regex.Matches(syntheticError, @"(?:line [0-9]+|unexpected [a-z]+|not found|bad substitution|syntax error|Illegal number|unbound variable)", RegexOptions.CultureInvariant).Select(x => x.Value).Take(8);
            process.ExitCode.Should().Be(0, "synthetic shell failure categories: {0}", string.Join(',', errorKinds));
            result.Should().NotContain("sentinel");
            SanitizeForensicProbe(result).Should().Contain(expected);
            if (scenario != "Valid") result.Should().NotContain("LINK|2|7|123|1|");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EA18_DockerInventory_ParsesOnlyFixedNumericFieldsAndUnzonedStartTime()
    {
        var rows = ParseForensicTop("PID PPID UID STARTED\n2341 2330 10001 Fri Oct 16 17:23:57 2026\n");
        rows.Should().ContainSingle().Which.Should().Be(new ForensicHostProcess(2341, 2330, 10001, "2026-10-16T17:23:57"));
    }

    [Theory]
    [InlineData("PID COMMAND\n1 Password=sentinel-private\n")]
    [InlineData("PID PPID UID STARTED\n1 0 1 bad-date\n")]
    [InlineData("PID PPID UID STARTED\n0 0 1 Fri Oct 16 17:23:57 2026\n")]
    public void EA18_DockerInventory_RejectsUnexpectedOrSecretBearingFields(string raw)
    {
        var act = () => ParseForensicTop(raw);
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("sentinel");
    }

    private sealed record ForensicHostProcess(int HostPid, int ParentPid, int Uid, string StartWithoutTimezone);
    private static ForensicHostProcess[] ParseForensicTop(string raw)
    {
        static InvalidOperationException Rejected() => new("Diagnostic inventory schema rejected; raw output withheld.");
        if (raw.Length > 65536) throw Rejected();
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 2 or > 65 || !Regex.IsMatch(lines[0], @"^PID\s+PPID\s+UID\s+STARTED$", RegexOptions.CultureInvariant)) throw Rejected();
        var result = new List<ForensicHostProcess>();
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 8 || !int.TryParse(fields[0], out var pid) || pid <= 0 || !int.TryParse(fields[1], out var parent) || parent < 0
                || !int.TryParse(fields[2], out var uid) || uid < 0
                || !DateTime.TryParseExact(string.Join(' ', fields.Skip(3)), "ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time)) throw Rejected();
            result.Add(new(pid, parent, uid, time.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (result.Select(x => x.HostPid).Distinct().Count() != result.Count) throw Rejected();
        return result.ToArray();
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static extern nint ForensicReadLink(string path, byte[] buffer, nuint size);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "opendir", SetLastError = true)]
    private static extern nint ForensicOpenDirectory(string path);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "closedir")]
    private static extern int ForensicCloseDirectory(nint directory);

    private static (string? Target, int Errno) ReadForensicLink(string path)
    {
        var bytes = new byte[4096];
        var size = ForensicReadLink(path, bytes, (nuint)bytes.Length);
        var errno = size < 0 ? System.Runtime.InteropServices.Marshal.GetLastPInvokeError() : 0;
        return (size is >= 0 and < 4096 ? Encoding.UTF8.GetString(bytes, 0, (int)size) : null, errno);
    }

    private static async Task<object[]> CaptureForensicHostProcessesAsync(ForensicHostProcess[] inventory, string[] rows, CancellationToken cancellationToken)
    {
        // Only daemon-attested PIDs of the already verified container, with the runner's existing UID.
        var result = new List<object>();
        var inodeSet = rows.Where(x => x.StartsWith("SOCKET|", StringComparison.Ordinal)).Select(x => x.Split('|')[5]).ToHashSet(StringComparer.Ordinal);
        var totalFds = 0;
        foreach (var process in inventory.Take(16))
        {
            if (cancellationToken.IsCancellationRequested) break;
            var root = "/proc/" + process.HostPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var fields = new Dictionary<string, object?> { ["HostPid"] = process.HostPid };
            result.Add(fields); // Preserve sanitized partial-stage evidence on access/race/deadline failure.
            try
            {
                static string Ticks(string stat)
                {
                    var close = stat.LastIndexOf(')');
                    var parts = close < 0 ? [] : stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length > 19 && ulong.TryParse(parts[19], out var ticks) && ticks > 0 ? ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NA";
                }
                async Task<string> Read(string path)
                {
                    await using var stream = File.OpenRead(path);
                    var bytes = new byte[16385]; var size = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken);
                    if (size == bytes.Length) throw new InvalidOperationException("Bound");
                    return Encoding.UTF8.GetString(bytes, 0, size);
                }
                var before = Ticks(await Read(root + "/stat")); fields["StartTicks"] = before;
                var status = await Read(root + "/status");
                fields["CgroupSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(await Read(root + "/cgroup")))).ToLowerInvariant();
                var nspid = status.Split('\n').FirstOrDefault(x => x.StartsWith("NSpid:", StringComparison.Ordinal))?[6..].Trim() ?? "";
                fields["NSpid"] = Regex.IsMatch(nspid, @"^[0-9]+(?:\s+[0-9]+){0,15}$", RegexOptions.CultureInvariant) ? Regex.Split(nspid, @"\s+").Select(x => int.TryParse(x, out var value) ? value : 0).ToArray() : [];
                foreach (var ns in new[] { "pid", "net" })
                {
                    var link = ReadForensicLink(root + "/ns/" + ns);
                    fields[ns + "NamespaceErrno"] = link.Errno;
                    var match = Regex.Match(link.Target ?? "", @"^[a-z]+:\[([0-9]+)\]$", RegexOptions.CultureInvariant);
                    fields[ns + "Namespace"] = match.Success ? match.Groups[1].Value : "NA";
                }
                var exe = ReadForensicLink(root + "/exe"); fields["ExecutableReadErrno"] = exe.Errno;
                fields["ExecutableKind"] = exe.Target switch { "/opt/mssql/bin/sqlservr" => "SqlServerEngine", "/opt/mssql/bin/sqltelemetry" => "SqlTelemetry", "/opt/mssql/bin/sqlagent" => "SqlAgent", null => "Unavailable", _ => "Other" };
                fields["ExecutableSha256"] = "NA";
                try
                {
                    await using var executable = File.OpenRead(root + "/exe");
                    if (executable.Length <= 67108864) fields["ExecutableSha256"] = Convert.ToHexString(await SHA256.HashDataAsync(executable, cancellationToken)).ToLowerInvariant();
                }
                catch (Exception) { fields["ExecutableHashUnavailable"] = true; }
                var directory = ForensicOpenDirectory(root + "/fd");
                var directoryErrno = directory == 0 ? System.Runtime.InteropServices.Marshal.GetLastPInvokeError() : 0;
                fields["FdDirectoryErrno"] = directoryErrno;
                var links = new List<object>(); fields["ExactInodeFdCandidates"] = links;
                var failures = new Dictionary<int, int>(); fields["FdReadErrnoCounts"] = failures;
                if (directory != 0)
                {
                    ForensicCloseDirectory(directory);
                    foreach (var path in Directory.EnumerateFileSystemEntries(root + "/fd"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++totalFds > 4096) { fields["FdScanBoundReached"] = true; break; }
                        if (!int.TryParse(Path.GetFileName(path), out var fd) || fd < 0) continue;
                        var link = ReadForensicLink(path);
                        if (link.Errno != 0) { failures[link.Errno] = failures.GetValueOrDefault(link.Errno) + 1; continue; }
                        var match = Regex.Match(link.Target ?? "", @"^socket:\[([0-9]+)\]$", RegexOptions.CultureInvariant);
                        if (!match.Success || !inodeSet.Contains(match.Groups[1].Value)) continue;
                        if (links.Count >= 16) { fields["LinkBoundReached"] = true; break; }
                        links.Add(new { Fd = fd, Inode = match.Groups[1].Value, FdStable = ReadForensicLink(path).Target == link.Target });
                    }
                }
                var after = Ticks(await Read(root + "/stat"));
                fields["StartTicksAfter"] = after; fields["ProcessIncarnationStable"] = before != "NA" && before == after;
                fields["Limitation"] = "Host FD candidates only; correlate namespace, exact socket and fresh executable evidence separately. No authorization.";
            }
            catch (Exception)
            {
                fields["Incomplete"] = true;
                fields["FailureKind"] = cancellationToken.IsCancellationRequested ? "Deadline" : "ProcAccessRaceOrUnavailable";
            }
        }
        return result.ToArray();
    }

    private static bool ForensicSocketMatches(string local, string remote, string? clientAddress, int? clientPort)
    {
        static (System.Net.IPAddress Address, int Port) Decode(string value)
        {
            var parts = value.Split(':');
            if (parts.Length != 2 || parts[0].Length is not (8 or 32) || parts[1].Length != 4) throw new FormatException();
            var bytes = Convert.FromHexString(parts[0]);
            for (var i = 0; i < bytes.Length; i += 4) Array.Reverse(bytes, i, 4);
            var ip = new System.Net.IPAddress(bytes);
            return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, Convert.ToInt32(parts[1], 16));
        }
        try
        {
            if (clientPort is not (> 0 and <= 65535) || !System.Net.IPAddress.TryParse(clientAddress, out var client)) return false;
            if (client.IsIPv4MappedToIPv6) client = client.MapToIPv4();
            var a = Decode(local); var b = Decode(remote);
            return a.Port == clientPort && a.Address.Equals(client) && b.Port == 1433 && System.Net.IPAddress.IsLoopback(b.Address);
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }
    private static string[] SanitizeForensicProbe(string raw)
    {
        if (raw.Length > 65536) throw new InvalidOperationException("Forensic output exceeded its bound; raw output withheld.");
        var rows = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        const string endpoint = "(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{32}):[0-9A-Fa-f]{4}";
        const string hash = "(?:[a-f0-9]{64}|NA)";
        const string number = "(?:[0-9]+|NA)";
        var pattern = "^(?:SOCKET\\|tcp6?\\|" + endpoint + "\\|" + endpoint + "\\|[0-9A-Fa-f]{2}\\|[0-9]+"
            + "|PROCESS\\|[0-9]+\\|[0-9]+\\|[0-9]+\\|(?:SqlServerEngine|SqlTelemetry|SqlAgent|SqlCmd|Shell|Other|Unavailable)\\|" + hash + "\\|" + number + "\\|" + number + "\\|" + hash
            + "|LINK\\|[0-9]+\\|[0-9]+\\|[0-9]+\\|[01]\\|[0-9]+\\|[0-9]+"
            + "|IDENTITY\\|[0-9]+\\|" + number + "\\|(?:[0-9]+(?:,[0-9]+)*|NA)\\|" + number + "\\|" + number
            + "|DIAG\\|(?:FdMissing|FdNotEnumerable|GlobUnexpanded|ReadlinkPermissionMessage|ProcessGone|StartTicksInvalid|ProcessChanged|NamespaceMismatch|SocketInodeMismatch|ReadlinkUnclassified)\\|[0-9]+"
            + "|SCAN\\|[0-9]+\\|[0-9]+\\|[0-9]+"
            + "|OWNER\\|[0-9]+\\|[0-9]+\\|[0-9]+\\|[0-9]+\\|[a-f0-9]{64}\\|[0-9]+\\|[0-9]+\\|[a-f0-9]{64}"
            + "|PROBE\\|[0-9]+\\|[01]|BOOT\\|[0-9]+\\|[0-9]+|NSROOT\\|" + number + "\\|" + number + "\\|" + hash
            + "|LIMIT\\|(?:ToolsUnavailable|ProcUnavailable|SocketNotFound|FdReadUnavailable|ScanBoundReached|ProcChanged|InvalidPort)\\|[0-9]+)$";
        if (rows.Length > 256 || rows.Any(row => !Regex.IsMatch(row, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))))
            throw new InvalidOperationException("Forensic output schema rejected; raw output withheld.");
        return rows;
    }

    private static string? VerifiedForensicContainer;
    private static int ForensicCaptured;
    // No cmdline, environ, process comm, healthcheck arguments/output, privilege escalation or writes.
    private const string ForensicProcScript = """
        port=$1; candidate=$2; client4=$3; client6=$4
        for tool in awk readlink sha256sum getconf; do
          command -v "$tool" >/dev/null 2>&1 || { printf 'LIMIT|ToolsUnavailable|0\n'; exit 0; }
        done
        case "$port:$candidate" in *[!0-9:]*|:*) printf 'LIMIT|InvalidPort|0\n'; exit 0;; esac
        case "$client4:$client6" in *[!0-9A-F:]*) printf 'LIMIT|InvalidPort|0\n'; exit 0;; esac
        printf 'BOOT|%s|%s\n' "$(awk '$1=="btime" {print $2}' /proc/stat)" "$(getconf CLK_TCK)"
        nsnum() { readlink "$1" 2>/dev/null | awk -F'[][]' 'NF>1 {print $2; found=1} END {if (!found) print "NA"}'; }
        filehash() { value=$(sha256sum "$1" 2>/dev/null | awk '{print $1}'); if [ ${#value} -eq 64 ]; then printf '%s' "$value"; else printf NA; fi; }
        printf 'NSROOT|%s|%s|%s\n' "$(nsnum /proc/1/ns/pid)" "$(nsnum /proc/1/ns/net)" "$(filehash /proc/1/cgroup)"
        rootns=$(nsnum /proc/self/ns/pid)
        rootnet=$(nsnum /proc/self/ns/net)
        ticks_of() { awk '{sub(/^.*\) /, ""); print $20}' "$1" 2>/dev/null; }
        positive() { case "$1" in ''|*[!0-9]*|0) return 1;; *) return 0;; esac; }
        metadata_count=0; metadata_bound=0
        emit_proc() {
          proc_pid=$1
          if [ "$metadata_count" -ge 96 ]; then
            if [ "$metadata_bound" -eq 0 ]; then printf 'LIMIT|ScanBoundReached|96\n'; metadata_bound=1; fi
            return 1
          fi
          metadata_count=$((metadata_count+1))
          if [ ! -r "/proc/$proc_pid/stat" ]; then printf 'LIMIT|ProcUnavailable|%s\n' "$proc_pid"; return; fi
          data=$(awk '{sub(/^.*\) /, ""); print $2, $20}' "/proc/$proc_pid/stat" 2>/dev/null)
          set -- $data; parent=${1:-0}; ticks=${2:-0}
          uid=$(awk '$1=="Uid:" && $2 ~ /^[0-9]+$/ {print $2;exit}' "/proc/$proc_pid/status" 2>/dev/null)
          nspid=$(awk '$1=="NSpid:" {for(i=2;i<=NF;i++){if($i !~ /^[0-9]+$/) exit; printf "%s%s",(i==2?"":","),$i}}' "/proc/$proc_pid/status" 2>/dev/null)
          printf 'IDENTITY|%s|%s|%s|%s|%s\n' "$proc_pid" "${uid:-NA}" "${nspid:-NA}" "$(nsnum /proc/$proc_pid/ns/pid)" "$(nsnum /proc/$proc_pid/ns/net)"
          path=$(readlink "/proc/$proc_pid/exe" 2>/dev/null); tag=Other
          case "$path" in /opt/mssql/bin/sqlservr) tag=SqlServerEngine;; /opt/mssql/bin/sqltelemetry) tag=SqlTelemetry;; /opt/mssql/bin/sqlagent) tag=SqlAgent;; /opt/mssql-tools*/bin/sqlcmd) tag=SqlCmd;; /bin/sh|/bin/bash|/bin/dash|/usr/bin/sh|/usr/bin/bash|/usr/bin/dash) tag=Shell;; '') tag=Unavailable;; esac
          printf 'PROCESS|%s|%s|%s|%s|%s|%s|%s|%s\n' "$proc_pid" "$parent" "$ticks" "$tag" "$(filehash /proc/$proc_pid/exe)" "$(nsnum /proc/$proc_pid/ns/pid)" "$(nsnum /proc/$proc_pid/ns/net)" "$(filehash /proc/$proc_pid/cgroup)"
          again=$(awk '{sub(/^.*\) /, ""); print $20}' "/proc/$proc_pid/stat" 2>/dev/null)
          [ "$again" = "$ticks" ] || printf 'LIMIT|ProcChanged|%s\n' "$proc_pid"
        }
        for q in "$candidate"; do
          if [ -d "/proc/$q" ]; then printf 'PROBE|%s|1\n' "$q"; else printf 'PROBE|%s|0\n' "$q"; fi
        done
        inodes=''
        for table in tcp tcp6; do
          if [ ! -r "/proc/net/$table" ]; then printf 'LIMIT|ProcUnavailable|0\n'; continue; fi
          snapshot=$(awk -v a="$client4" -v b="$client6" -v t="$table" '($2==a || $2==b) && ($3=="0100007F:0599" || $3=="0000000000000000FFFF00000100007F:0599" || $3=="00000000000000000000000001000000:0599") && $4=="01" && $10 ~ /^[0-9]+$/ {if (++n<=16) print "SOCKET|"t"|"$2"|"$3"|"$4"|"$10}' "/proc/net/$table")
          [ -z "$snapshot" ] || printf '%s\n' "$snapshot"
          found=$(printf '%s\n' "$snapshot" | awk -F'|' 'NF==6 {print $6}')
          inodes="$inodes $found"
        done
        if [ -z "$(printf '%s' "$inodes" | awk '{$1=$1;print}')" ]; then printf 'LIMIT|SocketNotFound|%s\n' "$candidate"; exit 0; fi
        nproc=0; nfd=0; links=0; readok=0
        missing=0; notenum=0; glob=0; permissionmessage=0; gone=0; invalidticks=0; changed=0; nsbad=0; inodebad=0; unclassified=0
        for dir in "/proc/$candidate" /proc/[0-9]*; do
          [ -d "$dir" ] || continue
          [ "$dir" != "/proc/$candidate" ] || [ "$nproc" -eq 0 ] || continue
          nproc=$((nproc+1)); [ "$nproc" -le 64 ] || { printf 'LIMIT|ScanBoundReached|%s\n' "$nproc"; break; }
          q=${dir#/proc/}
          if [ ! -r "$dir/stat" ]; then unclassified=$((unclassified+1)); continue; fi
          scan_ticks=$(ticks_of "$dir/stat")
          if ! positive "$scan_ticks"; then invalidticks=$((invalidticks+1)); continue; fi
          emit_proc "$q" || continue
          if [ "$scan_ticks" != "$(ticks_of "$dir/stat")" ]; then changed=$((changed+1)); continue; fi
          pns=$(nsnum "$dir/ns/pid"); nns=$(nsnum "$dir/ns/net")
          if [ "$pns" = NA ] || [ "$nns" = NA ] || [ "$rootns" = NA ] || [ "$rootnet" = NA ] || [ "$pns" != "$rootns" ] || [ "$nns" != "$rootnet" ]; then nsbad=$((nsbad+1)); continue; fi
          if [ ! -d "$dir/fd" ]; then
            if [ ! -d "$dir" ]; then gone=$((gone+1)); elif [ "$scan_ticks" != "$(ticks_of "$dir/stat")" ]; then changed=$((changed+1)); elif [ -r "$dir" ] && [ -x "$dir" ]; then missing=$((missing+1)); else unclassified=$((unclassified+1)); fi
            continue
          fi
          if [ ! -r "$dir/fd" ] || [ ! -x "$dir/fd" ]; then notenum=$((notenum+1)); continue; fi
          for fd in "$dir"/fd/*; do
            case "${fd##*/}" in '*') glob=$((glob+1)); continue;; ''|*[!0-9]*) unclassified=$((unclassified+1)); continue;; esac
            nfd=$((nfd+1)); [ "$nfd" -le 4096 ] || { printf 'LIMIT|ScanBoundReached|%s\n' "$nfd"; break 2; }
            target=$(LC_ALL=C readlink -v -- "$fd" 2>&1); linkrc=$?
            if [ "$linkrc" -ne 0 ]; then
              if [ ! -e "$dir/stat" ] && [ -r "$dir" ] && [ -x "$dir" ]; then gone=$((gone+1)); else
                case "$target" in *'Permission denied'*|*'Operation not permitted'*) permissionmessage=$((permissionmessage+1));; *) unclassified=$((unclassified+1));; esac
              fi
              continue
            fi
            readok=$((readok+1)); matched=0
            for inode in $inodes; do
              if [ "$target" = "socket:[$inode]" ]; then
                matched=1; before=$scan_ticks
                if [ ! -r "$dir/stat" ]; then unclassified=$((unclassified+1)); continue; fi
                ownerexe=$(filehash "$dir/exe"); ownergroup=$(filehash "$dir/cgroup")
                emit_proc "$q" || continue
                a=$q; depth=0
                while [ "$a" -gt 1 ] && [ "$depth" -lt 8 ]; do
                  a=$(awk '{sub(/^.*\) /, ""); print $2}' "/proc/$a/stat" 2>/dev/null)
                  case "$a" in ''|*[!0-9]*) break;; esac
                  emit_proc "$a"; depth=$((depth+1))
                done
                after=$(ticks_of "$dir/stat")
                if ! positive "$after"; then invalidticks=$((invalidticks+1)); continue; fi
                if [ "$before" != "$after" ]; then changed=$((changed+1)); continue; fi
                current=0
                for table in tcp tcp6; do
                  hit=$(awk -v a="$client4" -v b="$client6" -v ino="$inode" '($2==a || $2==b) && ($3=="0100007F:0599" || $3=="0000000000000000FFFF00000100007F:0599" || $3=="00000000000000000000000001000000:0599") && $4=="01" && $10==ino {print 1;exit}' "/proc/net/$table" 2>/dev/null)
                  [ "$hit" != 1 ] || current=1
                done
                if [ "$current" -ne 1 ]; then inodebad=$((inodebad+1)); continue; fi
                stable=0
                if [ "$ownerexe" != NA ] && [ "$ownergroup" != NA ] && [ "$(filehash "$dir/exe")" = "$ownerexe" ] && [ "$(filehash "$dir/cgroup")" = "$ownergroup" ] && [ "$(ticks_of "$dir/stat")" = "$before" ] && [ "$(readlink "$fd" 2>/dev/null)" = "$target" ] && [ "$(nsnum "$dir/ns/pid")" = "$rootns" ] && [ "$(nsnum "$dir/ns/net")" = "$rootnet" ]; then
                  stable=1
                  printf 'OWNER|%s|%s|%s|%s|%s|%s|%s|%s\n' "$q" "${fd##*/}" "$inode" "$before" "$ownerexe" "$rootns" "$rootnet" "$ownergroup"
                fi
                printf 'LINK|%s|%s|%s|%s|%s|%s\n' "$q" "${fd##*/}" "$inode" "$stable" "$before" "$after"
                links=$((links+1)); [ "$links" -lt 16 ] || break 3
              fi
            done
            case "$target" in 'socket:['*']') [ "$matched" -ne 0 ] || inodebad=$((inodebad+1));; esac
          done
        done
        printf 'SCAN|%s|%s|%s\n' "$nproc" "$nfd" "$readok"
        for entry in FdMissing:$missing FdNotEnumerable:$notenum GlobUnexpanded:$glob ReadlinkPermissionMessage:$permissionmessage ProcessGone:$gone StartTicksInvalid:$invalidticks ProcessChanged:$changed NamespaceMismatch:$nsbad SocketInodeMismatch:$inodebad ReadlinkUnclassified:$unclassified; do
          [ "${entry#*:}" -eq 0 ] || printf 'DIAG|%s|%s\n' "${entry%:*}" "${entry#*:}"
        done
        """;

    private static async Task<string> ForensicDockerAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--host"); start.ArgumentList.Add("unix:///var/run/docker.sock");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Forensic Docker CLI unavailable.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(); throw new InvalidOperationException("Forensic CLI timed out; no container process was terminated."); }
        await stderr; // Never expose stderr or exception diagnostics containing command arguments.
        var text = await stdout;
        if (process.ExitCode != 0 || text.Length > 65536) throw new InvalidOperationException("Forensic probe unavailable or oversized; raw output withheld.");
        return text;
    }

    private static async Task<string> CaptureForensicOnceAsync(SqlConnection observer, IReadOnlyList<IsolationSession> sessions, IReadOnlyList<string> databases)
    {
        if (Interlocked.Exchange(ref ForensicCaptured, 1) != 0) return "AlreadyCapturedOnce";
        if (!OperatingSystem.IsLinux()) return "UnavailableOutsideLinuxCI";
        var report = new Dictionary<string, object?>
        {
            ["CapturedAtUtc"] = DateTime.UtcNow,
            ["SqlBefore"] = JsonDocument.Parse(RedactedSessions(sessions)).RootElement.Clone(),
            ["SqlDatabaseIds"] = sessions.Select(x => new { x.SessionId, x.DatabaseId }).ToArray(),
            ["UserDatabaseCount"] = databases.Count,
            ["UnknownDatabaseCount"] = databases.Count(x => x != Volatile.Read(ref CurrentOwnedDatabase)),
            ["Stage"] = "BeforeTargetReverification"
        };
        try
        {
            await VerifyGitHubServiceAsync(new SqlConnectionStringBuilder(observer.ConnectionString));
            var container = VerifiedForensicContainer ?? throw new InvalidOperationException();
            const string healthFormat = "{\"Id\":{{json .Id}},\"ImageId\":{{json .Image}},\"HostPid\":{{json .State.Pid}},\"StartedAt\":{{json .State.StartedAt}},\"HealthStatus\":{{json .State.Health.Status}},\"FailingStreak\":{{json .State.Health.FailingStreak}},\"History\":[{{range $i,$v := .State.Health.Log}}{{if $i}},{{end}}{\"Start\":{{json $v.Start}},\"End\":{{json $v.End}},\"ExitCode\":{{json $v.ExitCode}}}{{end}}]}";
            using var health = JsonDocument.Parse(await ForensicDockerAsync("inspect", "--format", healthFormat, container));
            report["Container"] = health.RootElement.Clone();
            report["Stage"] = "ContainerReverifiedBeforeProcessProbe";
            var osProbeClock = Stopwatch.StartNew();
            ForensicHostProcess[] hostInventory = [];
            try
            {
                hostInventory = ParseForensicTop(await ForensicDockerAsync("top", container, "-eo", "pid,ppid,uid,lstart"));
                report["HostInventory"] = hostInventory;
                report["HostInitPidPresent"] = hostInventory.Any(x => x.HostPid == health.RootElement.GetProperty("HostPid").GetInt32());
            }
            catch (Exception) { report["HostInventoryLimitation"] = "InventoryUnavailableOrSchemaRejected"; }
            var proofs = LiveOwnedConnectionIds();
            var current = Volatile.Read(ref CurrentOwnedDatabase);
            var first = sessions.FirstOrDefault(x => current is null || !proofs.Contains(x.ConnectionId) || x.Database != current
                || x.Status != "sleeping" || x.HasRequest || x.OpenTransactions != 0);
            string[] rows = [];
            if (first is not null && first.ClientPort is > 0 and <= 65535 && first.ClientProcessId > 0)
            {
                var ip = System.Net.IPAddress.Parse(first.ClientAddress ?? "");
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                string Encode(System.Net.IPAddress address)
                {
                    var bytes = address.GetAddressBytes();
                    for (var i = 0; i < bytes.Length; i += 4) Array.Reverse(bytes, i, 4);
                    return Convert.ToHexString(bytes) + ":" + first.ClientPort.Value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
                }
                var ipv4 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? Encode(ip) : "0:0000";
                var ipv6 = Encode(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? ip.MapToIPv6() : ip);
                try
                {
                    rows = SanitizeForensicProbe(await ForensicDockerAsync("exec", container, "sh", "-c", ForensicProcScript, "tm70-read-only-probe", first.ClientPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), first.ClientProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), ipv4, ipv6));
                }
                catch (Exception) { report["ContainerProcLimitation"] = "ProbeUnavailableTimeoutOrSchemaRejected"; }
            }
            report["ProbeRows"] = rows;
            report["Stage"] = "ProcessProbeCapturedBeforeSqlRecheck";
            var remaining = TimeSpan.FromSeconds(30) - osProbeClock.Elapsed;
            if (remaining > TimeSpan.Zero && hostInventory.Length != 0)
            {
                using var hostDeadline = new CancellationTokenSource(remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5));
                report["HostProcProbe"] = await CaptureForensicHostProcessesAsync(hostInventory, rows, hostDeadline.Token);
                report["HostProbeTruncated"] = hostInventory.Length > 16 || hostDeadline.IsCancellationRequested;
            }
            else report["HostProbeLimitation"] = "InventoryOrTimeBudgetUnavailable";
            var after = await ReadSessionsAsync(observer);
            var stable = first is not null && after.Any(x => x.SessionId == first.SessionId && x.ConnectionId == first.ConnectionId && x.ConnectedAt == first.ConnectedAt && x.ClientPort == first.ClientPort && x.ClientAddress == first.ClientAddress
                && x.ClientProcessId == first.ClientProcessId && x.DatabaseId == first.DatabaseId && x.Database == first.Database
                && x.Status == first.Status && x.OpenTransactions == first.OpenTransactions && x.HasRequest == first.HasRequest);
            var socketMatches = rows.Where(row => row.StartsWith("SOCKET|", StringComparison.Ordinal)).Where(row =>
            { var parts = row.Split('|'); return first is not null && ForensicSocketMatches(parts[2], parts[3], first.ClientAddress, first.ClientPort); }).ToArray();
            report["Evidence"] = new
            {
                CapturedAtUtc = DateTime.UtcNow,
                Container = health.RootElement.Clone(),
                SqlBefore = JsonDocument.Parse(RedactedSessions(sessions)).RootElement.Clone(),
                SqlAfter = JsonDocument.Parse(RedactedSessions(after)).RootElement.Clone(),
                SqlConnectionStableAcrossProbe = stable,
                SqlDatabaseIds = sessions.Select(x => new { x.SessionId, x.DatabaseId }),
                IdentityClassification = first is null ? null : new
                {
                    Login = first.Login == "sa" ? "Sa" : first.Login is "NT AUTHORITY\\SYSTEM" or "NT AUTHORITY\\NETWORK SERVICE" ? "BuiltinServiceNameUnverified" : first.Login.StartsWith("##MS_", StringComparison.Ordinal) ? "MicrosoftPatternUnverified" : "Other",
                    Program = first.Program is "SQLServerCEIP" or "SQLTelemetry" ? "TelemetryNameUnverified" : "OtherUnverified",
                    LoginNameSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first.Login))),
                    ProgramNameSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first.Program)))
                },
                UserDatabaseCount = databases.Count,
                UnknownDatabaseCount = databases.Count(x => x != Volatile.Read(ref CurrentOwnedDatabase)),
                ProbeRows = rows,
                ExactClientSocketRows = socketMatches,
                Limitation = "Snapshot evidence only; claimed PID/name/login never authorize a session. Missing/inaccessible proc or unstable SQL/socket/process identity remains incomplete. No policy change."
            };
            report["Stage"] = "CaptureCompletedPolicyUnchanged";
        }
        catch (Exception error)
        {
            report["Limitation"] = "Forensic capture incomplete; earlier sanitized stages retained; fail-closed unchanged";
            report["ErrorType"] = error.GetType().Name;
        }
        var serialized = JsonSerializer.Serialize(report);
        if (serialized.Length > 65536)
        {
            report.Remove("Evidence"); report.Remove("HostProcProbe");
            report["Limitation"] = "Aggregate output bound reached; earlier sanitized stages retained. Policy unchanged.";
            serialized = JsonSerializer.Serialize(report);
        }
        return serialized;
    }

    private sealed record IsolationSession(short SessionId, Guid ConnectionId, string Status, string Database, bool HasRequest, int OpenTransactions, int ClientProcessId, string? ClientAddress, int? ClientPort, string Program, string Host, string Login, DateTime ConnectedAt, int? DatabaseId = null);
    private static void ValidateCiIsolation(IReadOnlyList<IsolationSession> sessions, IReadOnlyCollection<Guid> verifiedConnections, string? ownedDatabase, IReadOnlyCollection<string> userDatabases)
    {
        if (userDatabases.Any(name => ownedDatabase is null || !string.Equals(name, ownedDatabase, StringComparison.Ordinal)))
            throw new InvalidOperationException("BLOCKED: CI contains a database not owned by this A4.1a fixture.");
        foreach (var session in sessions)
            if (ownedDatabase is null || !verifiedConnections.Contains(session.ConnectionId)
                || !string.Equals(session.Database, ownedDatabase, StringComparison.Ordinal)
                || session.Status != "sleeping" || session.HasRequest || session.OpenTransactions != 0)
                throw new InvalidOperationException("BLOCKED: unverified or active CI SQL workload. " + RedactedSessions([session]));
    }
    private static void ValidateWindowsSessions(int otherSessions, bool allowOwnSessions)
    {
        if (!allowOwnSessions && otherSessions != 0)
            throw new InvalidOperationException("BLOCKED: Windows local policy requires zero other SQL user sessions.");
    }
    private static void ValidateServerIdentity(bool windows, string machine, string? instance, string expectedMachine)
    {
        if (!string.Equals(machine, expectedMachine, StringComparison.OrdinalIgnoreCase)
            || (windows ? !string.Equals(instance, "MSSQLSERVER01", StringComparison.OrdinalIgnoreCase) : instance is not null))
            throw new InvalidOperationException("BLOCKED: SQL instance does not match the independently verified target.");
    }

    private static readonly SemaphoreSlim DatabaseSlot = new(1, 1);
    private static string? CurrentOwnedDatabase;
    private sealed record ConnectionProof(WeakReference<SqlConnection> Handle, Guid ClientId, string Database);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ConnectionProof> OwnedConnections = new();

    private static Guid[] LiveOwnedConnectionIds()
        => OwnedConnections.Where(pair => pair.Value.Database == Volatile.Read(ref CurrentOwnedDatabase)
            && pair.Value.Handle.TryGetTarget(out var handle) && handle.State == ConnectionState.Open
            && handle.ClientConnectionId == pair.Value.ClientId).Select(pair => pair.Key).ToArray();

    private static async Task<List<IsolationSession>> ReadSessionsAsync(SqlConnection observer, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT s.session_id,c.connection_id,s.status,COALESCE(DB_NAME(s.database_id),''),CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM sys.dm_exec_requests r WHERE r.session_id=s.session_id) THEN 1 ELSE 0 END),s.open_transaction_count,s.host_process_id,c.client_net_address,c.client_tcp_port,s.program_name,s.host_name,s.login_name,c.connect_time,s.database_id FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_connections c ON c.session_id=s.session_id WHERE s.is_user_process=1 AND s.session_id<>@@SPID;";
        await using var command = new SqlCommand(sql, observer);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<IsolationSession>();
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(new(reader.GetInt16(0), reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetInt32(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? "" : reader.GetString(9), reader.IsDBNull(10) ? "" : reader.GetString(10), reader.GetString(11), reader.IsDBNull(12) ? DateTime.MinValue : reader.GetDateTime(12), reader.IsDBNull(13) ? null : reader.GetInt16(13)));
        return sessions;
    }

    private static async Task WaitForVerifiedPoolRetirementAsync(SqlConnection observer, Guid knownOwnedConnection)
    {
        // Only a connection identified on our own handle before its deliberate close; never poll away unknown workloads.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await ReadSessionsAsync(observer, deadline.Token)).Any(x => x.ConnectionId == knownOwnedConnection))
        { await Task.Yield(); deadline.Token.ThrowIfCancellationRequested(); }
    }
    private static string RedactedSessions(IReadOnlyList<IsolationSession> sessions)
        => JsonSerializer.Serialize(sessions.Select(s => new
        {
            s.SessionId,
            s.ConnectionId,
            s.Status,
            Database = s.Database == Volatile.Read(ref CurrentOwnedDatabase) ? "CurrentOwned" : s.Database == "master" ? "master" : "Unowned",
            s.HasRequest,
            s.OpenTransactions,
            s.ClientProcessId,
            ProcessMatchesTestHost = s.ClientProcessId == Environment.ProcessId,
            s.ClientAddress,
            s.ClientPort,
            s.ConnectedAt,
            ProgramKind = s.Program is "Core Microsoft SqlClient Data Provider" or "Microsoft SQL Server Data Provider" or "Microsoft.Data.SqlClient" ? "SqlClient" : s.Program.Equals("SQLCMD", StringComparison.OrdinalIgnoreCase) ? "sqlcmd" : "Other",
            HostMatchesTestHost = s.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase),
            LoginIsSa = s.Login == "sa"
        }));

    private static async Task AssertCiSessionsAsync(SqlConnection observer)
    {
        var before = await ReadSessionsAsync(observer);
        // Current-process client pool operation only; never SQL KILL or a server-wide pool reset.
        SqlConnection.ClearAllPools();
        var after = await ReadSessionsAsync(observer);
        var verified = LiveOwnedConnectionIds();
        Console.WriteLine("TM70_A41A_CI_ISOLATION before=" + RedactedSessions(before) + " after=" + RedactedSessions(after) + " verifiedLiveHandles=" + verified.Length);
        var databases = new List<string>();
        await using (var command = new SqlCommand("SELECT name FROM sys.databases WHERE database_id>4;", observer))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) databases.Add(reader.GetString(0));
        try { ValidateCiIsolation(after, verified, Volatile.Read(ref CurrentOwnedDatabase), databases); }
        catch (InvalidOperationException error)
        {
            var evidence = await CaptureForensicOnceAsync(observer, after, databases);
            throw new InvalidOperationException(error.Message + "\nTM70_A41A_FORENSIC " + evidence, error);
        }
    }

    private sealed class OwnedConnection(SqlConnection connection, Guid serverId) : IAsyncDisposable
    {
        public SqlConnection Connection { get; } = connection;
        public async ValueTask DisposeAsync()
        {
            OwnedConnections.TryRemove(serverId, out _);
            await Connection.DisposeAsync();
        }
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
        if (!await DatabaseSlot.WaitAsync(0)) throw new InvalidOperationException("BLOCKED: another Track A owned database still holds the fixture lease.");
        var creationAttempted = false;
        try
        {
            await AssertStorageAsync(configured);
            var existing = await DatabaseNamesAsync(configured);
            creationAttempted = true;
            var db = await SqlServerTestDatabase.CreateEmptyAsync();
            var owned = new OwnedDatabase(db, configured, output);
            Volatile.Write(ref CurrentOwnedDatabase, db.DatabaseName);
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
        catch { if (!creationAttempted && Volatile.Read(ref CurrentOwnedDatabase) is null && DatabaseSlot.CurrentCount == 0) DatabaseSlot.Release(); throw; }
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
        ValidateServerIdentity(localWindows, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), machine);
        reader.GetString(2).Should().Be(principal);
        reader.GetInt32(3).Should().Be(0);
        reader.GetInt64(4).Should().BeGreaterThan(reader.GetInt64(5), "the model allocation must fit before creation; no fabricated reserve threshold");
        var otherSessions = reader.GetInt32(6);
        await reader.DisposeAsync();
        if (localWindows) ValidateWindowsSessions(otherSessions, allowOwnSessions);
        else await AssertCiSessionsAsync(c);
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
        public async Task<OwnedConnection> OpenVerifiedConnectionAsync()
        {
            if (Volatile.Read(ref CurrentOwnedDatabase) != Database.DatabaseName)
                throw new InvalidOperationException("BLOCKED: database is not the current owned fixture.");
            var connection = new SqlConnection(Database.ConnectionString);
            try
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("SELECT c.connection_id,DB_NAME() FROM sys.dm_exec_connections c WHERE c.session_id=@@SPID;", connection);
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync() || reader.GetString(1) != Database.DatabaseName)
                    throw new InvalidOperationException("BLOCKED: live connection database binding differs from its owned fixture.");
                var serverId = reader.GetGuid(0);
                OwnedConnections[serverId] = new(new(connection), connection.ClientConnectionId, Database.DatabaseName);
                return new OwnedConnection(connection, serverId);
            }
            catch { await connection.DisposeAsync(); throw; }
        }
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
            foreach (var entry in OwnedConnections.Where(x => x.Value.Database == Database.DatabaseName)) OwnedConnections.TryRemove(entry.Key, out _);
            Volatile.Write(ref CurrentOwnedDatabase, null);
            DatabaseSlot.Release();
        }
    }
}