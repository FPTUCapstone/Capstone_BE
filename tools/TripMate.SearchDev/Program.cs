using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Features.Tours.Search.V2;
using TripMate.Application.Features.Tours.SemanticPoc;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services.SemanticPoc;

// Explicit local development indexer. SQL principal requires SELECT only; no SQL DDL/cleanup exists here.
static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing {name}.");
static Uri Local(string text)
{
    var uri = new Uri(text.TrimEnd('/') + "/");
    if (!uri.IsLoopback || uri.Scheme != "http" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        throw new InvalidOperationException("Only loopback HTTP dev dependencies are allowed.");
    return uri;
}
var connection = new SqlConnectionStringBuilder(Required("TM70_DEV_CONNECTION"));
if (!connection.InitialCatalog.StartsWith("TM70_SemanticDev_", StringComparison.Ordinal)
    || connection.DataSource != "127.0.0.1,14337") throw new InvalidOperationException("Explicit isolated SQL target required.");
if (args.Contains("--inspect", StringComparer.Ordinal))
{
    await using var sql = new SqlConnection(connection.ConnectionString);
    await sql.OpenAsync();
    await using var command = sql.CreateCommand();
    command.CommandText = """
        SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)) AS machine,
            COALESCE(CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)),'DEFAULT') AS instance,
            CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(256)) AS dataPath,
            DB_NAME() AS databaseName, IS_SRVROLEMEMBER('sysadmin') AS isSysadmin,
            HAS_PERMS_BY_NAME('commerce.Tours','OBJECT','SELECT') AS canSelect,
            HAS_PERMS_BY_NAME('commerce.Tours','OBJECT','INSERT') AS canInsert,
            HAS_PERMS_BY_NAME('commerce.Tours','OBJECT','UPDATE') AS canUpdate,
            HAS_PERMS_BY_NAME('commerce.Tours','OBJECT','DELETE') AS canDelete,
            HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER') AS canAlterDatabase,
            HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL') AS canControlDatabase;
        SELECT name, physical_name FROM sys.database_files;
        """;
    await using var reader = await command.ExecuteReaderAsync();
    var sets = new List<object>();
    do
    {
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        sets.Add(rows);
    } while (await reader.NextResultAsync());
    Console.WriteLine(JsonSerializer.Serialize(sets));
    return;
}
var collection = Required("TM70_DEV_COLLECTION");
if (!collection.StartsWith("tm70_poc_dev_", StringComparison.Ordinal) || collection.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
    throw new InvalidOperationException("Invalid isolated dev collection.");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var ct = deadline.Token;
await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connection.ConnectionString).Options);
var snapshot = await new SqlDevTourCatalog(context).ReadAsync(new TourSearchV2Request(), DateTimeOffset.UtcNow, ct);
using var embedClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Local(Required("TM70_DEV_EMBEDDING_URL")), Timeout = TimeSpan.FromSeconds(30) };
using var qdrant = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Local(Required("TM70_DEV_QDRANT_URL")), Timeout = TimeSpan.FromSeconds(15) };
var embedding = new HttpEmbeddingService(embedClient);
var manifestPath = Path.GetFullPath(Required("TM70_DEV_INDEX_MANIFEST"));
IndexManifest? previous = File.Exists(manifestPath) ? JsonSerializer.Deserialize<IndexManifest>(await File.ReadAllTextAsync(manifestPath, ct)) : null;
using var existing = await qdrant.GetAsync($"collections/{collection}", ct);
var owner = previous?.Owner ?? Guid.NewGuid().ToString("N");
if (existing.IsSuccessStatusCode)
{
    if (previous is null || previous.Collection != collection || previous.Database != connection.InitialCatalog
        || previous.Endpoint != qdrant.BaseAddress!.AbsoluteUri) throw new InvalidOperationException("Existing collection has no matching ownership manifest.");
    using var scroll = await qdrant.PostAsJsonAsync($"collections/{collection}/points/scroll", new { limit = 257, with_payload = true, with_vector = false }, ct);
    scroll.EnsureSuccessStatusCode();
    using var content = await JsonDocument.ParseAsync(await scroll.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    var result = content.RootElement.GetProperty("result");
    if (result.GetProperty("next_page_offset").ValueKind != JsonValueKind.Null
        || result.GetProperty("points").EnumerateArray().Any(p => p.GetProperty("payload").GetProperty("dev_owner").GetString() != owner))
        throw new InvalidOperationException("Collection contains foreign or excessive points.");
}
else
{
    if (existing.StatusCode != HttpStatusCode.NotFound || previous is not null)
        throw new InvalidOperationException("Collection state does not match the manifest.");
    using var create = await qdrant.PutAsJsonAsync($"collections/{collection}", new { vectors = new { size = SemanticModel.Dimensions, distance = "Cosine" } }, ct);
    create.EnsureSuccessStatusCode();
    // Record ownership before indexing; retry remains possible after an interrupted first build.
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new IndexManifest(collection, connection.InitialCatalog, qdrant.BaseAddress!.AbsoluteUri, owner, [])), ct);
}
foreach (var doc in snapshot.Corpus)
{
    var vector = await embedding.EmbedAsync(doc.Text, true, ct);
    using var upsert = await qdrant.PutAsJsonAsync($"collections/{collection}/points?wait=true", new
    {
        points = new[] { new { id = doc.TourId, vector, payload = new { fingerprint = doc.Fingerprint, model_version = SemanticModel.Version, dev_owner = owner } } },
    }, ct);
    upsert.EnsureSuccessStatusCode();
}
var currentIds = snapshot.Corpus.Select(d => d.TourId).ToArray();
var removed = (previous?.TourIds ?? []).Except(currentIds).ToArray();
if (removed.Length > 0)
{
    using var deletion = await qdrant.PostAsJsonAsync($"collections/{collection}/points/delete?wait=true", new { points = removed }, ct);
    deletion.EnsureSuccessStatusCode();
}
await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new IndexManifest(collection, connection.InitialCatalog, qdrant.BaseAddress!.AbsoluteUri, owner, currentIds)), ct);
Console.WriteLine(JsonSerializer.Serialize(new { collection, database = connection.InitialCatalog, indexedCount = currentIds.Length, removedCount = removed.Length, model = SemanticModel.Version, dimensions = SemanticModel.Dimensions }));
internal sealed record IndexManifest(string Collection, string Database, string Endpoint, string Owner, long[] TourIds);
