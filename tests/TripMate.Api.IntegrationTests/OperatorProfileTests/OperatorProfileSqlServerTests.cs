using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Media;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.OperatorProfileTests;

/// <summary>UC-34 against a real SQL Server schema (fresh v7 plus all migrations).</summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class OperatorProfileSqlServerTests
{
    private const string Route = "/api/v1/operator/profile";
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];

    private sealed class LogoStorageStub : IOperatorLogoStorage
    {
        private int allocations;

        public List<string> Deletes { get; } = [];

        public string AllocatePublicId(long operatorUserId) =>
            $"tripmate/operator-logos/{operatorUserId}/sql{Interlocked.Increment(ref allocations)}";

        public Task<OperatorLogoUploadResult> UploadAsync(OperatorLogoUpload upload, CancellationToken cancellationToken) =>
            Task.FromResult(OperatorLogoUploadResult.Succeeded(new Uri($"https://cdn.example.test/{upload.PublicId}.png")));

        public Task<bool> DeleteAsync(string publicId, CancellationToken cancellationToken)
        {
            lock (Deletes) Deletes.Add(publicId);
            return Task.FromResult(true);
        }
    }

    private static TripMateApiFactory Factory(SqlServerTestDatabase database, LogoStorageStub storage) => new(
        sqlServerConnectionString: database.ConnectionString,
        configureTestServices: services =>
        {
            services.RemoveAll<IOperatorLogoStorage>();
            services.AddSingleton<IOperatorLogoStorage>(storage);
        });

    private static async Task<long> SeedOperatorAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var created = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var user = new User
        {
            Email = "owner@suntours.vn",
            FullName = "Owner",
            PasswordHash = "hashed",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var profile = new OperatorProfile
        {
            UserId = user.Id,
            CompanyName = "Sun Tours",
            TaxCode = "0312345678",
            BusinessLicenseNo = "79-1234/2026/TCDL-GPLHQT",
            CommissionRate = 12.5m,
            ApprovalStatus = OperatorApprovalStatus.Approved,
            ReviewedBy = user.Id,
            ReviewedAtUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
        };
        profile.UpdateBeneficiaryBankAccount("Vietcombank", "Holder Name", "****9876", "cipher-secret-xyz",
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        db.OperatorProfiles.Add(profile);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static MultipartFormDataContent Form(
        string name = "Moon Tours",
        string? description = "Night tours",
        string? address = "2 River St",
        string? phone = "0911111111",
        string? email = "hello@moon.vn",
        string? website = "https://moon.vn",
        byte[]? logo = null,
        string? removeLogo = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(name), "businessName" } };
        if (description is not null) form.Add(new StringContent(description), "businessDescription");
        if (address is not null) form.Add(new StringContent(address), "businessAddress");
        if (phone is not null) form.Add(new StringContent(phone), "contactPhone");
        if (email is not null) form.Add(new StringContent(email), "contactEmail");
        if (website is not null) form.Add(new StringContent(website), "website");
        if (removeLogo is not null) form.Add(new StringContent(removeLogo), "removeLogo");
        if (logo is not null)
        {
            var content = new ByteArrayContent(logo);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(content, "logo", "logo.png");
        }

        return form;
    }

    // ExecuteScalarAsync turns DBNull into "", so SQL NULL is returned through an explicit marker.
    private const string SqlNullMarker = "<<SQL NULL>>";

    private static async Task<string?> ReadAsync(SqlServerTestDatabase database, string column, long userId)
    {
        string value = await database.ExecuteScalarAsync<string>(
            $"SELECT ISNULL(CAST({column} AS NVARCHAR(MAX)), N'{SqlNullMarker}') " +
            $"FROM dbo.OperatorProfiles WHERE user_id = {userId};");
        return value == SqlNullMarker ? null : value;
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Put_PersistsEveryEditableColumn_AndTheyAreReadBackByGet()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        using var factory = Factory(database, new LogoStorageStub());
        long userId = await SeedOperatorAsync(database);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(name: "Công ty Du lịch Mặt Trăng", description: "Tour đêm Đà Nẵng — hải sản & cà phê",
            address: "2 Đường Sông Hàn, Hải Châu", email: " Hello@Moon.vn ");

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(database, "company_name", userId)).Should().Be("Công ty Du lịch Mặt Trăng");
        (await ReadAsync(database, "description", userId)).Should().Be("Tour đêm Đà Nẵng — hải sản & cà phê");
        (await ReadAsync(database, "contact_address", userId)).Should().Be("2 Đường Sông Hàn, Hải Châu");
        (await ReadAsync(database, "contact_phone", userId)).Should().Be("0911111111");
        (await ReadAsync(database, "contact_email", userId)).Should().Be("Hello@Moon.vn");
        (await ReadAsync(database, "website_url", userId)).Should().Be("https://moon.vn");
        var got = await client.GetFromJsonAsync<JsonElement>(Route);
        got.GetProperty("businessName").GetString().Should().Be("Công ty Du lịch Mặt Trăng");
        got.GetProperty("businessDescription").GetString().Should().Be("Tour đêm Đà Nẵng — hải sản & cà phê");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Put_LeavesEveryReadOnlyColumnByteIdentical()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        using var factory = Factory(database, new LogoStorageStub());
        long userId = await SeedOperatorAsync(database);
        string[] readOnly =
        [
            "tax_code", "business_license_no", "approval_status", "commission_rate", "rejection_reason",
            "reviewed_by", "reviewed_at", "created_at", "bank_name", "account_holder_name",
            "account_number_masked", "account_number_protected", "bank_account_updated_at",
        ];
        var before = new List<string?>();
        foreach (string column in readOnly) before.Add(await ReadAsync(database, column, userId));
        string? emailBefore = await database.ExecuteScalarAsync<string?>(
            $"SELECT email + '|' + full_name + '|' + status FROM dbo.Users WHERE user_id = {userId};");
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form();
        form.Add(new StringContent("0999999999"), "taxCode");
        form.Add(new StringContent("hacker@evil.test"), "accountEmail");

        (await client.PutAsync(Route, form)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = new List<string?>();
        foreach (string column in readOnly) after.Add(await ReadAsync(database, column, userId));
        after.Should().Equal(before);
        (await database.ExecuteScalarAsync<string?>(
            $"SELECT email + '|' + full_name + '|' + status FROM dbo.Users WHERE user_id = {userId};"))
            .Should().Be(emailBefore);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Put_ValuesAtTheMaximumLengths_FitTheirColumnsWithoutTruncation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        using var factory = Factory(database, new LogoStorageStub());
        long userId = await SeedOperatorAsync(database);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        string name = new('n', 200);
        string description = new('d', 2000);
        string address = new('a', 300);
        string email = new string('e', 195) + "@x.vn";
        string website = "https://x.vn/" + new string('w', 287);
        using var form = Form(name: name, description: description, address: address, email: email, website: website);

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(database, "company_name", userId)).Should().Be(name);
        (await ReadAsync(database, "description", userId)).Should().Be(description);
        (await ReadAsync(database, "contact_address", userId)).Should().Be(address);
        (await ReadAsync(database, "contact_email", userId)).Should().Be(email);
        (await ReadAsync(database, "website_url", userId)).Should().Be(website);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Put_BlankOptionalFields_AreStoredAsSqlNull()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        using var factory = Factory(database, new LogoStorageStub());
        long userId = await SeedOperatorAsync(database);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using (var fill = Form())
        {
            (await client.PutAsync(Route, fill)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var clear = Form(description: " ", address: "", phone: null, email: null, website: null);
        (await client.PutAsync(Route, clear)).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (string column in new[] { "description", "contact_address", "contact_phone", "contact_email", "website_url" })
        {
            (await ReadAsync(database, column, userId)).Should().BeNull(column);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Put_LogoReplaceAndRemove_PersistLogoUrlAndPublicId()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var storage = new LogoStorageStub();
        using var factory = Factory(database, storage);
        long userId = await SeedOperatorAsync(database);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);

        using (var first = Form(logo: Png))
        {
            (await client.PutAsync(Route, first)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await ReadAsync(database, "logo_public_id", userId)).Should().Be($"tripmate/operator-logos/{userId}/sql1");
        (await ReadAsync(database, "logo_url", userId)).Should().Be($"https://cdn.example.test/tripmate/operator-logos/{userId}/sql1.png");

        using (var second = Form(logo: Png))
        {
            (await client.PutAsync(Route, second)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await ReadAsync(database, "logo_public_id", userId)).Should().Be($"tripmate/operator-logos/{userId}/sql2");
        storage.Deletes.Should().Equal($"tripmate/operator-logos/{userId}/sql1");

        using (var remove = Form(removeLogo: "true"))
        {
            (await client.PutAsync(Route, remove)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await ReadAsync(database, "logo_public_id", userId)).Should().BeNull();
        (await ReadAsync(database, "logo_url", userId)).Should().BeNull();
        storage.Deletes.Should().Equal($"tripmate/operator-logos/{userId}/sql1", $"tripmate/operator-logos/{userId}/sql2");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSaves_LeaveOneCoherentRow_LastWriteWins()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        using var factory = Factory(database, new LogoStorageStub());
        long userId = await SeedOperatorAsync(database);
        using var clientA = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var clientB = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var formA = Form(name: "Writer A", description: "from A", website: "https://a.example.test");
        using var formB = Form(name: "Writer B", description: "from B", website: "https://b.example.test");

        var responses = await Task.WhenAll(clientA.PutAsync(Route, formA), clientB.PutAsync(Route, formB));

        responses.Select(response => response.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        string? name = await ReadAsync(database, "company_name", userId);
        string? description = await ReadAsync(database, "description", userId);
        string? website = await ReadAsync(database, "website_url", userId);
        (name, description, website).Should().BeOneOf(
            ("Writer A", "from A", "https://a.example.test"),
            ("Writer B", "from B", "https://b.example.test"));
    }
}