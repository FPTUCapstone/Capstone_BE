using System.Data.Common;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.SemanticPoc;
using TripMate.Infrastructure.Services.SemanticPoc;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class SemanticPocSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Authority_BatchesCandidateIdsAndEnforcesCurrentPublicSqlState()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        var commands = new QueryCounter();
        await using var context = database.CreateDbContext(commands);
        var clock = new CountingClock();
        var authority = new SqlSemanticTourAuthority(context, clock);

        var result = await authority.ResolveAsync(Enumerable.Range(101, 8).Select(x => (long)x).ToArray(), CancellationToken.None);

        result.Select(x => x.TourId).Should().BeEquivalentTo([101L, 108L]);
        result.Should().OnlyContain(x => x.IsPublic);
        var first = result.Single(x => x.TourId == 101);
        first.Title.Should().Be("Phố cổ Hội An");
        first.OperatorName.Should().Be("Visible Travel");
        first.Destinations.Should().BeEquivalentTo(["Đà Nẵng", "Hội An"]);
        first.Text.Should().Be("Title: 19:Phố cổ Hội An\nDestinations: 8:Hội An11:Đà Nẵng\nOperator: 14:Visible Travel");
        first.Text.Should().NotContain("Private itinerary");
        commands.Count.Should().Be(1, "candidate SQL authorization and public-field projection must be batched, not N+1");
        clock.Reads.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Search_RejectsStaleAndUnpublishedVectorsAfterSqlChanges()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var authority = new SqlSemanticTourAuthority(context, new CountingClock());
        var original = await authority.ResolveAsync([101L, 108L], CancellationToken.None);
        var candidates = original.Select(x => new SemanticCandidate(x.TourId, 0.9f, x.Fingerprint, SemanticModel.Version)).ToArray();
        var search = new SemanticTourSearch(new FixedEmbedding(), new FixedVectors(candidates), authority);

        var initial = await search.SearchAsync("di sản Hội An", 1, 20, CancellationToken.None);
        initial.IsSuccess.Should().BeTrue();
        initial.Value.TotalCount.Should().Be(2);

        await database.ExecuteNonQueryAsync("UPDATE commerce.Tours SET title=N'Updated public text' WHERE tour_id=101; UPDATE commerce.Tours SET status='Inactive' WHERE tour_id=108;");
        var changed = await search.SearchAsync("di sản Hội An", 1, 20, CancellationToken.None);

        changed.IsSuccess.Should().BeTrue();
        changed.Value.TotalCount.Should().Be(0);
        changed.Value.Items.Should().BeEmpty();
        changed.Value.SqlUniverseCoverageUnknown.Should().BeTrue();
        changed.Value.CountScope.Should().Be("eligible-indexed-fixture-corpus");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Authority_EmptyOrOversizedCandidateSetDoesNotQuerySql()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var commands = new QueryCounter();
        await using var context = database.CreateDbContext(commands);
        var authority = new SqlSemanticTourAuthority(context, new CountingClock());

        (await authority.ResolveAsync([], CancellationToken.None)).Should().BeEmpty();
        Func<Task> oversized = async () => await authority.ResolveAsync(Enumerable.Range(1, SemanticModel.MaxCorpus + 1).Select(x => (long)x).ToArray(), CancellationToken.None);
        await oversized.Should().ThrowAsync<ArgumentOutOfRangeException>();
        commands.Count.Should().Be(0);
    }

    [SemanticPocServicesFact]
    [Trait("Category", "SemanticPoc")]
    public async Task RealPinnedModelAndQdrant_ResolveSqlAndRejectStaleOrUnpublishedTours()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var authority = new SqlSemanticTourAuthority(context, new CountingClock());
        using var embeddingClient = new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("TRIPMATE_EMBEDDING_POC_URL")!.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        using var vectorClient = new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("TRIPMATE_QDRANT_POC_URL")!.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var embedding = new HttpEmbeddingService(embeddingClient);
        var collection = "tm70_poc_e2e_" + Guid.NewGuid().ToString("N");
        using var created = await vectorClient.PutAsJsonAsync("collections/" + collection,
            new { vectors = new { size = SemanticModel.Dimensions, distance = "Cosine" } });
        created.EnsureSuccessStatusCode();
        try
        {
            var documents = (await authority.ResolveAsync([101L, 108L], CancellationToken.None)).ToList();
            // A genuinely embedded hidden fixture tests SQL authority, not vector metadata trust.
            documents.Add(new SemanticTourDocument(102, "Unpublished Hội An", ["Hội An"], "Visible Travel", false));
            var points = new List<object>();
            foreach (var document in documents)
            {
                var vector = await embedding.EmbedAsync(document.Text, true, CancellationToken.None);
                SemanticModel.IsValidVector(vector).Should().BeTrue();
                points.Add(new
                {
                    id = document.TourId,
                    vector,
                    payload = new { fingerprint = document.Fingerprint, model_version = SemanticModel.Version }
                });
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var upserted = await vectorClient.PutAsJsonAsync("collections/" + collection + "/points?wait=true", new { points });
                upserted.EnsureSuccessStatusCode();
            }
            using var collectionInfo = await vectorClient.GetAsync("collections/" + collection);
            collectionInfo.EnsureSuccessStatusCode();
            using var metadata = System.Text.Json.JsonDocument.Parse(await collectionInfo.Content.ReadAsStringAsync());
            metadata.RootElement.GetProperty("result").GetProperty("points_count").GetInt32().Should().Be(3);

            var search = new SemanticTourSearch(embedding, new QdrantTourVectorSearch(vectorClient, collection), authority);
            var initial = await search.SearchAsync("di sản Hội An", 1, 20, CancellationToken.None);
            initial.IsSuccess.Should().BeTrue();
            initial.Value.Items.Select(x => x.TourId).Should().BeEquivalentTo([101L, 108L]);
            initial.Value.TotalCount.Should().Be(2);
            initial.Value.SqlUniverseCoverageUnknown.Should().BeTrue();

            await database.ExecuteNonQueryAsync("UPDATE commerce.Tours SET title=N'Updated content' WHERE tour_id=101; UPDATE commerce.Tours SET status='Inactive' WHERE tour_id=108;");
            var changed = await search.SearchAsync("di sản Hội An", 1, 20, CancellationToken.None);
            changed.IsSuccess.Should().BeTrue();
            changed.Value.TotalCount.Should().Be(0);
        }
        finally
        {
            using var removed = await vectorClient.DeleteAsync("collections/" + collection);
            removed.EnsureSuccessStatusCode();
        }
    }
    private static Task SeedAsync(SqlServerTestDatabase database) => database.ExecuteNonQueryAsync("""
        SET IDENTITY_INSERT dbo.Users ON;
        INSERT INTO dbo.Users (user_id, role, email, full_name, status) VALUES
        (101,'TourOperator',N'semantic-active@example.test',N'Visible operator','Active'),
        (102,'TourOperator',N'semantic-locked@example.test',N'Locked operator','Locked'),
        (103,'TourOperator',N'semantic-pending@example.test',N'Pending operator','Active');
        SET IDENTITY_INSERT dbo.Users OFF;
        INSERT INTO dbo.OperatorProfiles (user_id,company_name,tax_code,business_license_no,approval_status) VALUES
        (101,N'Visible Travel',N'SEM-TAX-101',N'SEM-LIC-101','Approved'),
        (102,N'Locked Travel',N'SEM-TAX-102',N'SEM-LIC-102','Approved'),
        (103,N'Pending Travel',N'SEM-TAX-103',N'SEM-LIC-103','PendingApproval');
        SET IDENTITY_INSERT commerce.Tours ON;
        INSERT INTO commerce.Tours (tour_id,operator_user_id,title,description,base_price,duration_days,status,published_at) VALUES
        (101,101,N'Phố cổ Hội An',N'Private itinerary',500000,1,'Approved','2026-10-14T00:00:00'),
        (102,101,N'Unpublished',NULL,500000,1,'Approved',NULL),
        (103,101,N'Draft',NULL,500000,1,'Draft','2026-10-14T00:00:00'),
        (104,101,N'Future',NULL,500000,1,'Approved','2026-10-16T00:00:00'),
        (105,102,N'Locked owner',NULL,500000,1,'Approved','2026-10-14T00:00:00'),
        (106,103,N'Unapproved owner',NULL,500000,1,'Approved','2026-10-14T00:00:00'),
        (107,101,N'Paused',NULL,500000,1,'Inactive','2026-10-14T00:00:00'),
        (108,101,N'Published exactly now',NULL,500000,1,'Approved','2026-10-15T00:00:00');
        SET IDENTITY_INSERT commerce.Tours OFF;
        INSERT INTO catalog.Destinations(name) VALUES (N'Đà Nẵng'),(N'Hội An');
        INSERT INTO commerce.TourDestinations(tour_id,destination_id,sequence_no)
        SELECT 101,destination_id,CASE WHEN name=N'Đà Nẵng' THEN 1 ELSE 2 END
        FROM catalog.Destinations WHERE name IN(N'Đà Nẵng',N'Hội An');
        """);

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CountingClock : IDateTimeProvider
    {
        public int Reads { get; private set; }
        public DateTimeOffset UtcNow { get { Reads++; return Now; } }
    }

    private sealed class FixedEmbedding : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, bool passage, CancellationToken cancellationToken)
        {
            var vector = new float[SemanticModel.Dimensions];
            vector[0] = 1;
            return Task.FromResult(vector);
        }
    }

    private sealed class FixedVectors(IReadOnlyList<SemanticCandidate> candidates) : ITourVectorSearch
    {
        public Task<IReadOnlyList<SemanticCandidate>> SearchAllAsync(float[] vector, CancellationToken cancellationToken) => Task.FromResult(candidates);
    }
}
internal sealed class SemanticPocServicesFactAttribute : FactAttribute
{
    public SemanticPocServicesFactAttribute()
    {
        var required = new[] { SqlServerTestDatabase.ConnectionStringEnvironmentVariable,
            "TRIPMATE_EMBEDDING_POC_URL", "TRIPMATE_QDRANT_POC_URL" };
        if (required.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
        {
            Skip = "Set isolated SQL, embedding and Qdrant PoC endpoints to execute real-model integration.";
        }
    }
}