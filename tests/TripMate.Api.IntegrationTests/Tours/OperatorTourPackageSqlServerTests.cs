using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

/// <summary>
/// UC-35 Core behaviour against SQL Server. EF InMemory ignores unique indexes and
/// delete/insert ordering, so the replace-all update path can only be proven here.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class OperatorTourPackageSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Create_PersistsDestinationsInOrderTermsAndPolicySnapshot_AndDetailReturnsThem()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var created = await PostCreateAsync(client, Guid.NewGuid(), seed,
            destinationIds: [seed.DestinationIds[1], seed.DestinationIds[0]],
            inclusions: "  Hotel and breakfast  ", exclusions: "   ");

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");

        using var detail = await client.GetAsync($"/api/v1/operator/tours/{tourId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("destinations").EnumerateArray()
            .Select(item => item.GetProperty("destinationId").GetInt64())
            .Should().Equal(seed.DestinationIds[1], seed.DestinationIds[0]);
        root.GetProperty("destination").GetString().Should().Be("Đà Nẵng, Hà Nội");
        root.GetProperty("inclusions").GetString().Should().Be("Hotel and breakfast");
        root.GetProperty("exclusions").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("cancellationPolicyTemplateId").GetInt64().Should().Be(seed.PolicyTemplateId);
        root.GetProperty("cancellationPolicyRules").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("rejectionReason").ValueKind.Should().Be(JsonValueKind.Null);

        await using var verification = database.CreateDbContext();
        var links = await verification.TourDestinations.AsNoTracking()
            .Where(link => link.TourId == tourId).OrderBy(link => link.SequenceNo).ToListAsync();
        links.Select(link => (link.DestinationId, link.SequenceNo)).Should().Equal(
            (seed.DestinationIds[1], 1), (seed.DestinationIds[0], 2));
        (await verification.TourCancellationPolicyRules.CountAsync(rule => rule.TourId == tourId))
            .Should().Be(await TemplateRuleCountAsync(database, seed.PolicyTemplateId));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Update_ReplacesChildrenForIdenticalReorderedAndChangedLists_WithoutUniqueIndexErrors()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var created = await PostCreateAsync(client, Guid.NewGuid(), seed,
            destinationIds: [seed.DestinationIds[0], seed.DestinationIds[1]]);
        var tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");

        Dictionary<long, string> names;
        await using (var lookup = database.CreateDbContext())
        {
            names = await lookup.Destinations.AsNoTracking().ToDictionaryAsync(destination => destination.Id, destination => destination.Name);
        }

        long[][] destinationSequences =
        [
            [seed.DestinationIds[0], seed.DestinationIds[1]], // identical
            [seed.DestinationIds[1], seed.DestinationIds[0]], // reordered
            [seed.DestinationIds[2]],                         // changed
            [seed.DestinationIds[0], seed.DestinationIds[2], seed.DestinationIds[1]],
        ];
        foreach (var sequence in destinationSequences)
        {
            using var update = await client.PutAsJsonAsync($"/api/v1/operator/tours/{tourId}",
                BuildBody(seed, sequence, "Updated inclusions", null));

            update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

            await using var verification = database.CreateDbContext();
            var links = await verification.TourDestinations.AsNoTracking()
                .Where(link => link.TourId == tourId).OrderBy(link => link.SequenceNo).ToListAsync();
            links.Select(link => link.DestinationId).Should().Equal(sequence);
            links.Select(link => link.SequenceNo).Should().Equal(Enumerable.Range(1, sequence.Length));
            (await verification.Tours.AsNoTracking().SingleAsync(tour => tour.Id == tourId)).DestinationSummary
                .Should().Be(string.Join(", ", sequence.Select(id => names[id])));
            (await verification.TourItineraryItems.CountAsync(item => item.TourId == tourId)).Should().Be(1);
            (await verification.TourSchedules.CountAsync(item => item.TourId == tourId)).Should().Be(1);
            (await verification.TourCancellationPolicyRules.CountAsync(rule => rule.TourId == tourId))
                .Should().Be(await TemplateRuleCountAsync(database, seed.PolicyTemplateId));
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AssignCancellationPolicy_ReassigningTheSameTemplate_Succeeds()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var created = await PostCreateAsync(client, Guid.NewGuid(), seed,
            destinationIds: [seed.DestinationIds[0]]);
        var tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");

        using var assigned = await client.PutAsync(
            $"/api/v1/operator/tours/{tourId}/cancellation-policy/{seed.PolicyTemplateId}", null);

        assigned.StatusCode.Should().Be(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());
        await using var verification = database.CreateDbContext();
        (await verification.TourCancellationPolicyRules.CountAsync(rule => rule.TourId == tourId))
            .Should().Be(await TemplateRuleCountAsync(database, seed.PolicyTemplateId));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Create_ReplayWithSameKeyAndSameDestinations_ReturnsTheOriginalDraft()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        var key = Guid.NewGuid();

        using var first = await PostCreateAsync(client, key, seed,
            destinationIds: [seed.DestinationIds[0], seed.DestinationIds[1]], inclusions: " Meals ");
        using var replay = await PostCreateAsync(client, key, seed,
            destinationIds: [seed.DestinationIds[0], seed.DestinationIds[1]], inclusions: "Meals");

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.Created, "trimmed text is the same payload");
        ReadLong(await replay.Content.ReadAsStringAsync(), "tourId")
            .Should().Be(ReadLong(await first.Content.ReadAsStringAsync(), "tourId"));
    }

    /// <summary>
    /// The exact JSON the Web (operatorTourService.toBackendPayload) and Mobile (TourPackageDraft.toJson)
    /// clients send, including their explicit nulls. If the contract drifts again this fails before merge.
    /// </summary>
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateUpdateAndDetail_AcceptTheExactPayloadsSentByWebAndMobile()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string start = seed.DepartureStartUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        string end = seed.DepartureStartUtc.AddHours(8).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        string destinations = string.Join(",", seed.DestinationIds);

        string web = $$"""
            {"title":"Web tour","destinationIds":[{{destinations}}],"cancellationPolicyTemplateId":{{seed.PolicyTemplateId}},
             "description":"Web description","basePrice":1000000,"durationDays":1,
             "itineraryItems":[{"dayNo":1,"sequenceNo":1,"poiId":{{seed.PoiId}},"plannedTime":"08:00:00","stayDurationMinutes":60,"notes":null}],
             "schedules":[{"startAtUtc":"{{start}}","endAtUtc":"{{end}}","meetingPoint":null,"minimumCapacity":1,"totalCapacity":20,
                           "registrationDeadlineAtUtc":null,"adultPrice":1000000,"childPrice":null,"infantPrice":null}],
             "timeZoneId":"Asia/Ho_Chi_Minh"}
            """;
        string mobile = $$"""
            {"title":"Mobile tour","description":"Mobile description","basePrice":1000000,"durationDays":1,
             "categoryId":null,"inclusions":null,"exclusions":null,"highlights":null,"travellerRequirements":null,"timeZoneId":null,
             "destinationIds":[{{destinations}}],"cancellationPolicyTemplateId":{{seed.PolicyTemplateId}},
             "itineraryItems":[{"poiId":{{seed.PoiId}},"dayNo":1,"sequenceNo":1,"stayDurationMinutes":60,"plannedTime":null,"notes":null}],
             "schedules":[{"startAtUtc":"{{start}}","endAtUtc":"{{end}}","totalCapacity":20,"minimumCapacity":1,
                           "registrationDeadlineAtUtc":null,"adultPrice":1000000,"childPrice":null,"infantPrice":null,"meetingPoint":null}]}
            """;

        foreach (string body in new[] { web, mobile })
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/operator/tours")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var created = await client.SendAsync(create);
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            long tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");

            using var update = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var updated = await client.PutAsync($"/api/v1/operator/tours/{tourId}", update);
            updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());

            using var detail = await client.GetAsync($"/api/v1/operator/tours/{tourId}");
            detail.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            var root = json.RootElement;
            root.GetProperty("destinations").GetArrayLength().Should().Be(seed.DestinationIds.Length);
            root.GetProperty("timeZoneId").GetString().Should().NotBeNullOrWhiteSpace();
            root.GetProperty("departures")[0].GetProperty("adultPrice").GetDecimal().Should().Be(1000000m);
            root.GetProperty("itineraryItems")[0].GetProperty("poiId").GetInt64().Should().Be(seed.PoiId);
            // Every field the clients read must be present with the documented name and type.
            foreach (string field in new[]
            {
                "id", "title", "destination", "description", "status", "rejectionReason", "inclusions", "exclusions",
                "highlights", "travellerRequirements", "cancellationPolicyTemplateId", "cancellationPolicyRules",
                "itineraryItems", "departures", "mediaCount", "hasPrimaryImage", "canSubmitForReview", "reviewBlockers",
                "warnings", "timeZoneId",
            })
            {
                root.TryGetProperty(field, out _).Should().BeTrue($"clients read '{field}'");
            }
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ListAndDetail_ReportStatusPagingAndReviewReadinessFromSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        long firstTourId = 0;
        for (var index = 0; index < 3; index++)
        {
            using var created = await PostCreateAsync(client, Guid.NewGuid(), seed,
                destinationIds: [seed.DestinationIds[0]]);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            if (index == 0) firstTourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");
        }

        using var page = await client.GetAsync("/api/v1/operator/tours?status=draft&page=2&pageSize=2");
        using var invalidStatus = await client.GetAsync("/api/v1/operator/tours?status=Nonsense");
        using var invalidPage = await client.GetAsync("/api/v1/operator/tours?pageSize=1000");
        using var detail = await client.GetAsync($"/api/v1/operator/tours/{firstTourId}");

        page.StatusCode.Should().Be(HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
        using var list = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        list.RootElement.GetProperty("totalCount").GetInt32().Should().Be(3);
        list.RootElement.GetProperty("page").GetInt32().Should().Be(2);
        list.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        invalidStatus.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invalidPage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        detailJson.RootElement.GetProperty("canSubmitForReview").GetBoolean().Should().BeFalse();
        detailJson.RootElement.GetProperty("hasPrimaryImage").GetBoolean().Should().BeFalse();
        detailJson.RootElement.GetProperty("reviewBlockers").EnumerateArray()
            .Select(blocker => blocker.GetString()).Should().Equal("primary_image_missing");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Create_ConcurrentRequestsWithSameKey_CommitOneDraftAndNeverFail()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            PostCreateAsync(client, key, seed, destinationIds: [seed.DestinationIds[0]])));

        try
        {
            responses.Select(response => response.StatusCode).Should().OnlyContain(
                status => status == HttpStatusCode.Created,
                "a duplicate request must replay the original draft, not fail with a 409/500 or a deadlock");
            var tourIds = new HashSet<long>();
            foreach (var response in responses)
            {
                tourIds.Add(ReadLong(await response.Content.ReadAsStringAsync(), "tourId"));
            }

            tourIds.Should().HaveCount(1);
            await using var verification = database.CreateDbContext();
            (await verification.Tours.CountAsync(tour => tour.OperatorUserId == seed.OperatorId)).Should().Be(1);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Create_ReplayWithSameKeyAndReorderedDestinations_ReturnsPayloadMismatchConflict()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        var key = Guid.NewGuid();

        using var first = await PostCreateAsync(client, key, seed,
            destinationIds: [seed.DestinationIds[0], seed.DestinationIds[1]]);
        using var replay = await PostCreateAsync(client, key, seed,
            destinationIds: [seed.DestinationIds[1], seed.DestinationIds[0]]);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Create_WithFractionalBasePrice_ReturnsBadRequestAndWritesNothing()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var response = await PostCreateAsync(client, Guid.NewGuid(), seed,
            destinationIds: [seed.DestinationIds[0]], basePrice: 1000000.5m);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var verification = database.CreateDbContext();
        (await verification.Tours.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Create_WithTrailingZeroWholeVndPrice_IsAccepted()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var response = await PostCreateAsync(client, Guid.NewGuid(), seed,
            destinationIds: [seed.DestinationIds[0]], basePrice: 500000.00m);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Create_WithoutDestinationOrPolicy_ReturnsBadRequestAndWritesNothing()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var noDestination = await PostCreateAsync(client, Guid.NewGuid(), seed, destinationIds: []);
        using var noPolicy = await PostCreateAsync(client, Guid.NewGuid(), seed,
            destinationIds: [seed.DestinationIds[0]], policyTemplateId: 0);

        noDestination.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noPolicy.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var verification = database.CreateDbContext();
        (await verification.Tours.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Create_WithUnknownDestination_ReturnsNotFoundAndWritesNothing()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var response = await PostCreateAsync(client, Guid.NewGuid(), seed,
            destinationIds: [seed.DestinationIds[0], 987654321]);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var verification = database.CreateDbContext();
        (await verification.Tours.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Update_OfAPendingTour_IsLockedAndLeavesChildrenUntouched()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var created = await PostCreateAsync(client, Guid.NewGuid(), seed,
            destinationIds: [seed.DestinationIds[0]]);
        var tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");
        await database.ExecuteNonQueryAsync(
            $"UPDATE commerce.Tours SET status = 'Pending' WHERE tour_id = {tourId};");

        using var update = await client.PutAsJsonAsync($"/api/v1/operator/tours/{tourId}",
            BuildBody(seed, [seed.DestinationIds[1]], null, null));

        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var verification = database.CreateDbContext();
        (await verification.TourDestinations.Where(link => link.TourId == tourId)
            .Select(link => link.DestinationId).ToListAsync()).Should().Equal(seed.DestinationIds[0]);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Update_WhenTheReplacementSaveFails_RollsBackTheDeletedChildren()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var interceptor = new FailWhenDestinationsAreInserted();
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: interceptor);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var created = await PostCreateAsync(client, Guid.NewGuid(), seed,
            destinationIds: [seed.DestinationIds[0], seed.DestinationIds[1]]);
        var tourId = ReadLong(await created.Content.ReadAsStringAsync(), "tourId");
        interceptor.Armed = true;

        using var update = await client.PutAsJsonAsync($"/api/v1/operator/tours/{tourId}",
            BuildBody(seed, [seed.DestinationIds[2]], "Changed", null));

        update.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        interceptor.Triggered.Should().BeTrue("the replacement save must have been attempted and failed");
        await using var verification = database.CreateDbContext();
        (await verification.TourDestinations.Where(link => link.TourId == tourId)
            .OrderBy(link => link.SequenceNo).Select(link => link.DestinationId).ToListAsync())
            .Should().Equal(seed.DestinationIds[0], seed.DestinationIds[1]);
        (await verification.TourItineraryItems.CountAsync(item => item.TourId == tourId)).Should().Be(1);
        (await verification.TourSchedules.CountAsync(item => item.TourId == tourId)).Should().Be(1);
        (await verification.TourCancellationPolicyRules.CountAsync(rule => rule.TourId == tourId))
            .Should().Be(await TemplateRuleCountAsync(database, seed.PolicyTemplateId));
        (await verification.Tours.AsNoTracking().SingleAsync(tour => tour.Id == tourId))
            .Inclusions.Should().Be("Included services");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Destinations_AreListedAnonymouslyOrderedByName()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/destinations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.EnumerateArray()
            .Select(item => (Id: item.GetProperty("destinationId").GetInt64(), Name: item.GetProperty("name").GetString()!))
            .ToList();
        await using var verification = database.CreateDbContext();
        var expectedOrder = await verification.Destinations.AsNoTracking()
            .OrderBy(destination => destination.Name).Select(destination => destination.Id).ToListAsync();
        items.Select(item => item.Id).Should().Equal(expectedOrder);
        items.Should().HaveCount(seed.DestinationIds.Length);
    }

    private static async Task<HttpResponseMessage> PostCreateAsync(
        HttpClient client,
        Guid key,
        Seed seed,
        long[] destinationIds,
        string? inclusions = "Included services",
        string? exclusions = "Personal expenses",
        decimal basePrice = 1000000m,
        long? policyTemplateId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/operator/tours")
        {
            Content = JsonContent.Create(BuildBody(
                seed, destinationIds, inclusions, exclusions, basePrice,
                policyTemplateId ?? seed.PolicyTemplateId)),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request);
    }

    private static object BuildBody(
        Seed seed,
        long[] destinationIds,
        string? inclusions,
        string? exclusions,
        decimal basePrice = 1000000m,
        long? policyTemplateId = null) => new
        {
            title = "SQL Server integration tour",
            description = "A complete commercial tour draft.",
            basePrice,
            durationDays = 1,
            destinationIds,
            cancellationPolicyTemplateId = policyTemplateId ?? seed.PolicyTemplateId,
            inclusions,
            exclusions,
            itineraryItems = new[]
            {
                new { dayNo = 1, sequenceNo = 1, poiId = seed.PoiId, stayDurationMinutes = 60 },
            },
            schedules = new[]
            {
                new
                {
                    startAtUtc = seed.DepartureStartUtc,
                    endAtUtc = seed.DepartureStartUtc.AddDays(1),
                    totalCapacity = 12,
                    minimumCapacity = 2,
                    adultPrice = 1000000m,
                },
            },
        };

    private static long ReadLong(string payload, string property)
    {
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty(property).GetInt64();
    }

    private static async Task<int> TemplateRuleCountAsync(SqlServerTestDatabase database, long templateId) =>
        await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM commerce.CancellationPolicyTemplateRules " +
            $"WHERE cancellation_policy_template_id = {templateId};");

    private static async Task<Seed> SeedAsync(SqlServerTestDatabase database)
    {
        var operatorId = await database.ExecuteScalarAsync<long>("""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'uc35-sql@test.local', N'UC-35 operator', 'Active');
            DECLARE @id BIGINT = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no, approval_status)
            VALUES (@id, N'UC-35 Tours', N'UC35SQLTAX', N'UC35-SQL-LICENSE', 'Approved');
            SELECT CONVERT(BIGINT, @id);
            """);

        await database.ExecuteNonQueryAsync("""
            INSERT catalog.Destinations(name)
            VALUES (N'Hà Nội'), (N'Đà Nẵng'), (N'Hội An');
            """);
        var destinationIds = new List<long>();
        await using (var context = database.CreateDbContext())
        {
            destinationIds.AddRange(await context.Destinations.AsNoTracking()
                .OrderBy(destination => destination.Id).Select(destination => destination.Id).ToListAsync());
        }

        long poiId;
        await using (var context = database.CreateDbContext())
        {
            var category = PoiCategory.Create("UC-35 attraction", null);
            context.PoiCategories.Add(category);
            var poi = PointOfInterest.Create(category, "UC-35 POI", 21.03m, 105.85m, operatorId, DateTimeOffset.UtcNow);
            context.PointsOfInterest.Add(poi);
            await context.SaveChangesAsync();
            poiId = poi.Id;
        }

        var policyTemplateId = await database.ExecuteScalarAsync<long>("""
            SELECT TOP (1) cancellation_policy_template_id
            FROM commerce.CancellationPolicyTemplates
            ORDER BY cancellation_policy_template_id;
            """);
        // A fixed departure keeps the idempotency fingerprint identical between replays.
        var departureStartUtc = new DateTimeOffset(
            DateTimeOffset.UtcNow.AddDays(30).UtcDateTime.Date, TimeSpan.Zero).AddHours(8);
        return new Seed(operatorId, poiId, destinationIds.ToArray(), policyTemplateId, departureStartUtc);
    }

    private sealed record Seed(
        long OperatorId, long PoiId, long[] DestinationIds, long PolicyTemplateId, DateTimeOffset DepartureStartUtc);

    private sealed class FailWhenDestinationsAreInserted : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public bool Triggered { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<TourDestination>()
                    .Any(entry => entry.State == EntityState.Added))
            {
                Triggered = true;
                throw new InvalidOperationException("Simulated failure while saving replacement children.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}