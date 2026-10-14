using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

internal static class TripReviewSchemaInventory
{
    public static async Task<IReadOnlyList<string>> ReadAsync(
        SqlServerTestDatabase database, bool mediaOnly = false, bool recoveryOnly = false)
    {
        const string commandText = """
            WITH TargetTables AS (
                SELECT OBJECT_ID(N'social.TripReviews') AS object_id
                UNION ALL SELECT OBJECT_ID(N'social.Reviews')
                UNION ALL SELECT OBJECT_ID(N'commerce.TourDestinations')
            ), Inventory AS (
                SELECT CONCAT(N'TABLE|', SCHEMA_NAME(o.schema_id), N'.', o.name) AS item
                FROM sys.objects AS o
                JOIN TargetTables AS target ON target.object_id = o.object_id
                WHERE o.type = N'U'

                UNION ALL

                SELECT CONCAT(
                    N'COLUMN|', SCHEMA_NAME(o.schema_id), N'.', o.name, N'|',
                    c.name, N'|', t.name, N'|', c.max_length,
                    N'|', c.precision, N'|', c.scale, N'|', c.is_nullable,
                    N'|', COALESCE(c.collation_name, N'<NULL>'), N'|', c.is_identity,
                    N'|', c.is_computed, N'|', COALESCE(dc.definition, N'<NULL>'))
                FROM sys.columns AS c
                JOIN sys.objects AS o ON o.object_id = c.object_id
                JOIN TargetTables AS target ON target.object_id = c.object_id
                JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints AS dc
                    ON dc.parent_object_id = c.object_id
                    AND dc.parent_column_id = c.column_id

                UNION ALL

                SELECT CONCAT(
                    N'INDEX|', SCHEMA_NAME(o.schema_id), N'.', o.name, N'|',
                    i.type, N'|', i.is_unique, N'|', i.is_primary_key,
                    N'|', i.is_unique_constraint, N'|', i.is_disabled, N'|', i.ignore_dup_key,
                    N'|', i.has_filter, N'|', COALESCE(i.filter_definition, N'<NULL>'),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(ic.object_id, ic.column_id)
                            + CASE WHEN ic.is_descending_key = 1 THEN N':DESC' ELSE N':ASC' END
                        FROM sys.index_columns AS ic
                        WHERE ic.object_id = i.object_id
                            AND ic.index_id = i.index_id
                            AND ic.key_ordinal > 0
                        ORDER BY ic.key_ordinal
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(ic.object_id, ic.column_id)
                        FROM sys.index_columns AS ic
                        WHERE ic.object_id = i.object_id
                            AND ic.index_id = i.index_id
                            AND ic.is_included_column = 1
                        ORDER BY ic.index_column_id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''))
                FROM sys.indexes AS i
                JOIN sys.objects AS o ON o.object_id = i.object_id
                JOIN TargetTables AS target ON target.object_id = i.object_id
                WHERE i.index_id > 0 AND i.is_hypothetical = 0

                UNION ALL

                SELECT CONCAT(
                    N'FOREIGN_KEY|', SCHEMA_NAME(parent_object.schema_id), N'.', parent_object.name,
                    N'|', SCHEMA_NAME(referenced_object.schema_id), N'.', referenced_object.name,
                    N'|', fk.delete_referential_action, N'|', fk.update_referential_action,
                    N'|', fk.is_disabled, N'|', fk.is_not_trusted, N'|', STUFF((
                        SELECT N',' + COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
                            + N'->' + COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id)
                        FROM sys.foreign_key_columns AS fkc
                        WHERE fkc.constraint_object_id = fk.object_id
                        ORDER BY fkc.constraint_column_id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''))
                FROM sys.foreign_keys AS fk
                JOIN sys.objects AS parent_object ON parent_object.object_id = fk.parent_object_id
                JOIN sys.objects AS referenced_object ON referenced_object.object_id = fk.referenced_object_id
                JOIN TargetTables AS target ON target.object_id = fk.parent_object_id

                UNION ALL

                SELECT CONCAT(
                    N'CHECK|', SCHEMA_NAME(o.schema_id), N'.', o.name,
                    N'|', cc.is_disabled, N'|', cc.is_not_trusted, N'|',
                    cc.definition)
                FROM sys.check_constraints AS cc
                JOIN sys.objects AS o ON o.object_id = cc.parent_object_id
                JOIN TargetTables AS target ON target.object_id = cc.parent_object_id
            )
            SELECT item
            FROM Inventory
            ORDER BY item;
            """;

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var sql = mediaOnly ? commandText.Replace("OBJECT_ID(N'social.TripReviews')", "OBJECT_ID(N'social.TripReviewMediaOperations')")
            .Replace("OBJECT_ID(N'social.Reviews')", "OBJECT_ID(N'social.TripReviewMedia')")
            .Replace("UNION ALL SELECT OBJECT_ID(N'commerce.TourDestinations')", string.Empty) : commandText;
        if (recoveryOnly) sql = commandText.Replace("OBJECT_ID(N'social.TripReviews')", "OBJECT_ID(N'social.TripReviewMediaRecovery')")
            .Replace("UNION ALL SELECT OBJECT_ID(N'social.Reviews')", string.Empty)
            .Replace("UNION ALL SELECT OBJECT_ID(N'commerce.TourDestinations')", string.Empty);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var inventory = new List<string>();
        while (await reader.ReadAsync())
        {
            inventory.Add(reader.GetString(0));
        }

        return inventory;
    }

}