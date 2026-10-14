using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class TripReviewMediaMigrationTests
{
    internal const string Migration = "20260929_add_trip_review_media.sql";
    internal const string Seed = """
        INSERT dbo.Users(role,email,full_name) VALUES('Traveler','media1@test.invalid',N'Media One'),('Traveler','media2@test.invalid',N'Media Two');
        INSERT planning.Itineraries(traveler_user_id,source_type,title) VALUES(1,'Manual',N'Test');
        INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
        VALUES('MEDIA1',1,1,0,0,'Completed'),('MEDIA2',1,1,0,0,'Completed'),('MEDIA3',2,1,0,0,'Completed');
        INSERT social.TripReviews(booking_id,traveler_user_id,itinerary_id,overall_rating,title,content,publish_display_name,public_display_name,publication_status,created_at,edit_deadline,updated_at)
        VALUES(2,1,1,5,N'Preserved',N'Preserved',0,N'M.','Published','2026-09-01','2026-09-08','2026-09-01');
        INSERT social.Reviews(traveler_user_id,target_type,target_id,rating,comment) VALUES(1,'Tour',999,3,N'Legacy preserved');
        """;
    internal static string Insert(Guid id, Guid batch, int slot = 0, string? publicId = null) => $"""
        INSERT social.TripReviewMediaOperations(operation_id,batch_id,booking_id,traveler_user_id,sort_order,public_id,state,content_type,extension,input_byte_length,width,height,created_at,updated_at)
        VALUES('{id}','{batch}',1,1,{slot},'{publicId ?? id.ToString("N")}','Reserved','image/png','.png',5000000,6000,4000,'2026-09-01','2026-09-01');
        """;
    internal static Task Migrate(SqlServerTestDatabase db) => db.ExecuteScriptAsync(Path.Combine(AppContext.BaseDirectory, "Database", "migrations", Migration));
    internal static Task MigrateTyped(SqlServerTestDatabase db) => db.ExecuteScriptAsync(Path.Combine(
        AppContext.BaseDirectory, "Database", "migrations", "20261007_extend_trip_review_typed_parents.sql"));
    internal static async Task<SqlServerTestDatabase> Baseline()
    {
        var db = await SqlServerTestDatabase.CreateEmptyAsync();
        try
        {
            await db.ExecuteScriptAsync(Path.Combine(AppContext.BaseDirectory, "Database", "Fixtures", "tm79_pre_media_schema.sql"));
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static Task<string> State(SqlServerTestDatabase db) => db.ExecuteScalarAsync<string>("SELECT (SELECT * FROM social.TripReviews FOR JSON PATH) + (SELECT * FROM social.Reviews FOR JSON PATH)");

    [SqlServerFact]
    public async Task FreshUpgradeRerun_FullInventoryAndExistingDataArePreserved()
    {
        await using var fresh = await SqlServerTestDatabase.CreateAsync();
        await using var upgrade = await Baseline();
        var before = await State(upgrade);
        await Migrate(upgrade);
        await MigrateTyped(upgrade);
        (await TripReviewSchemaInventory.ReadAsync(fresh, true)).Should().Equal(await TripReviewSchemaInventory.ReadAsync(upgrade, true));
        (await upgrade.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('social.TripReviewMediaOperations')")).Should().Be(21);
        (await upgrade.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('social.TripReviewMedia')")).Should().Be(5);
        var inventory = await TripReviewSchemaInventory.ReadAsync(upgrade, true);
        await upgrade.ExecuteNonQueryAsync(Insert(Guid.NewGuid(), Guid.NewGuid()));
        var version = await upgrade.ExecuteScalarAsync<string>("SELECT CONVERT(varchar(18),version,1) FROM social.TripReviewMediaOperations");
        await MigrateTyped(upgrade);
        (await TripReviewSchemaInventory.ReadAsync(upgrade, true)).Should().Equal(inventory);
        (await State(upgrade)).Should().Be(before);
        (await upgrade.ExecuteScalarAsync<string>("SELECT CONVERT(varchar(18),version,1) FROM social.TripReviewMediaOperations")).Should().Be(version);
    }

    [SqlServerTheory]
    [InlineData("ALTER TABLE social.TripReviewMediaOperations ADD surprise int NULL;")]
    [InlineData("ALTER TABLE social.TripReviewMediaOperations ALTER COLUMN delivery_url nvarchar(1024) NULL;")]
    [InlineData("ALTER TABLE social.TripReviewMediaOperations NOCHECK CONSTRAINT ALL;")]
    [InlineData("DROP INDEX IX_TripReviewMediaOperations_Recovery ON social.TripReviewMediaOperations;")]
    [InlineData("ALTER TABLE social.TripReviewMedia ADD surprise int NULL;")]
    [InlineData("DECLARE @checks nvarchar(max); SELECT @checks=STRING_AGG(CONVERT(nvarchar(max),'ALTER TABLE social.TripReviewMediaOperations ADD CONSTRAINT '+QUOTENAME(name)+' CHECK '+definition+';'),'') FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('social.TripReviewMediaOperations') AND name IN ('CK_TripReviewMediaOperations_Url','CK_TripReviewMediaOperations_State'); ALTER TABLE social.TripReviewMediaOperations DROP CONSTRAINT CK_TripReviewMediaOperations_Url,CK_TripReviewMediaOperations_State; ALTER TABLE social.TripReviewMediaOperations ALTER COLUMN delivery_url nvarchar(2048) COLLATE Latin1_General_100_BIN2 NULL; EXEC(@checks);")]
    [InlineData("ALTER TABLE social.TripReviewMediaOperations ALTER COLUMN height int NULL;")]
    [InlineData("ALTER TABLE social.TripReviewMedia DROP CONSTRAINT FK_TripReviewMedia_Operation;")]
    [InlineData("DROP INDEX UX_TripReviewMedia_Operation ON social.TripReviewMedia;")]
    [InlineData("ALTER TABLE social.TripReviewMedia DROP CONSTRAINT CK_TripReviewMedia_Slot; ALTER TABLE social.TripReviewMedia ADD CONSTRAINT CK_TripReviewMedia_Slot CHECK(sort_order BETWEEN 0 AND 5);")]
    public async Task WrongExistingShape_IsRejectedWithoutRepair(string mutation)
    {
        await using var db = await Baseline();
        await Migrate(db);
        await db.ExecuteNonQueryAsync(mutation);
        var before = await TripReviewSchemaInventory.ReadAsync(db, true);
        var data = await State(db);
        Func<Task> action = () => Migrate(db);
        await action.Should().ThrowAsync<SqlException>();
        (await TripReviewSchemaInventory.ReadAsync(db, true)).Should().Equal(before);
        (await State(db)).Should().Be(data);
    }

    [SqlServerFact]
    public async Task HybridWrongShape_RollsBackNewObjects()
    {
        await using var db = await Baseline();
        await db.ExecuteNonQueryAsync("CREATE TABLE social.TripReviewMedia(media_id int); INSERT social.TripReviewMedia VALUES(42);");
        Func<Task> action = () => Migrate(db);
        await action.Should().ThrowAsync<SqlException>();
        (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID('social.TripReviewMediaOperations')")).Should().Be(0);
        (await db.ExecuteScalarAsync<int>("SELECT media_id FROM social.TripReviewMedia")).Should().Be(42);
    }

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task SlotsUniquenessAndRestrictiveDeletes(int count)
    {
        await using var db = await Baseline(); await Migrate(db);
        (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.TripReviewMedia")).Should().Be(0);
        var batch = Guid.NewGuid(); var first = Guid.NewGuid();
        await db.ExecuteNonQueryAsync(Insert(first, batch));
        for (int i = 1; i < count; i++) await db.ExecuteNonQueryAsync(Insert(Guid.NewGuid(), batch, i));
        Func<Task> duplicate = () => db.ExecuteNonQueryAsync(Insert(Guid.NewGuid(), batch));
        await duplicate.Should().ThrowAsync<SqlException>();
        Func<Task> publicId = () => db.ExecuteNonQueryAsync(Insert(Guid.NewGuid(), Guid.NewGuid(), 0, first.ToString("N")));
        await publicId.Should().ThrowAsync<SqlException>();
        await db.ExecuteNonQueryAsync($"INSERT social.TripReviewMedia(trip_review_id,operation_id,sort_order,created_at) VALUES(1,'{first}',0,'2026-09-01');");
        foreach (var sql in new[] { "DELETE social.TripReviewMediaOperations;", "DELETE social.TripReviews;", "DELETE commerce.Bookings WHERE booking_id=1;", "DELETE dbo.Users WHERE user_id=1;" })
        { Func<Task> deletion = () => db.ExecuteNonQueryAsync(sql); await deletion.Should().ThrowAsync<SqlException>(); }
    }

    [SqlServerTheory]
    [InlineData("sort_order=5")]
    [InlineData("state='Bad'")]
    [InlineData("input_byte_length=5000001")]
    [InlineData("input_byte_length=0")]
    [InlineData("width=6001")]
    [InlineData("height=0")]
    [InlineData("extension='.jpg'")]
    [InlineData("public_id=''")]
    [InlineData("state='Uploaded'")]
    [InlineData("state='Adopted'")]
    [InlineData("state='Cleaned'")]
    [InlineData("delivery_url=N'https://example.invalid/a'")]
    public async Task InvalidMetadataOrLifecycleIsRejected(string mutation)
    {
        await using var db = await Baseline(); await Migrate(db);
        await db.ExecuteNonQueryAsync(Insert(Guid.NewGuid(), Guid.NewGuid()));
        Func<Task> change = () => db.ExecuteNonQueryAsync("UPDATE social.TripReviewMediaOperations SET " + mutation);
        await change.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    public async Task ValidStateTuples_AllowLargerStoredBytes()
    {
        await using var db = await Baseline(); await Migrate(db);
        await db.ExecuteNonQueryAsync(Insert(Guid.NewGuid(), Guid.NewGuid()));
        await db.ExecuteNonQueryAsync("UPDATE social.TripReviewMediaOperations SET state='CleanupPending'; UPDATE social.TripReviewMediaOperations SET state='Cleaned',cleaned_at='2026-09-02';");
        await db.ExecuteNonQueryAsync("DELETE social.TripReviewMediaOperations;");
        await db.ExecuteNonQueryAsync(Insert(Guid.NewGuid(), Guid.NewGuid()));
        await db.ExecuteNonQueryAsync("UPDATE social.TripReviewMediaOperations SET state='Uploaded',delivery_url='https://example.invalid/a',stored_byte_length=6000000,uploaded_at='2026-09-02'; UPDATE social.TripReviewMediaOperations SET state='Adopted',adopted_at='2026-09-03';");
    }
}