using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.DirectPayment;

/// <summary>
/// SQL Server integration tests for the operator payment channel endpoints.
/// These require a real SQL Server connection because they verify:
///   - Actual EF Core persistence with SQL Server constraints
///   - Real ASP.NET Core Data Protection (credentials stored as ciphertext, not plaintext)
///   - Idempotent upsert behaviour (second submit updates same row, no duplicates)
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class OperatorPaymentChannelEndpointSqlServerTests
{
    private const string GetChannelRoute = "/api/v1/operator/payment-channel";
    private const string SubmitChannelRoute = "/api/v1/operator/payment-channel";
    private const string VerifyChannelRoute = "/api/v1/operator/payment-channel/verify";

    // ---- I1: GET when no channel exists → 200 NotConnected -----------------

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task GetChannel_WhenNoChannelExists_Returns200WithNotConnectedStatus()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorId = await SeedApprovedOperatorAsync(database);

        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(operatorId, UserRole.TourOperator);

        var response = await client.GetAsync(GetChannelRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("NotConnected");
        body.GetProperty("channelId").ValueKind.Should().Be(JsonValueKind.Null,
            "channelId must be null when no channel has been created");
    }

    // ---- I2: PUT submit credentials → 200 PendingVerification, persisted ----

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SubmitCredentials_ValidRequest_Returns200AndPersistsChannelAsPendingVerification()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorId = await SeedApprovedOperatorAsync(database);

        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(operatorId, UserRole.TourOperator);

        var response = await client.PutAsJsonAsync(SubmitChannelRoute, new
        {
            clientId = "test-client-id",
            apiKey = "test-api-key",
            checksumKey = "test-checksum-key",
            beneficiaryAccountNumber = "1234567890",
            beneficiaryAccountName = "Nguyen Van A",
            beneficiaryBankCode = "VCB",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("PendingVerification");

        // Verify the channel row was actually inserted
        int channelCount = await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM payment.OperatorPaymentChannels WHERE tour_operator_user_id = {operatorId};");
        channelCount.Should().Be(1, "exactly one channel row must exist after first submit");
    }

    // ---- I3: Credentials are write-only — response never contains raw values ---

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SubmitCredentials_RawCredentials_NeverAppearsInResponseBody()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorId = await SeedApprovedOperatorAsync(database);

        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(operatorId, UserRole.TourOperator);

        const string rawClientId = "super-secret-client-id";
        const string rawApiKey = "super-secret-api-key";
        const string rawChecksumKey = "super-secret-checksum-key";

        var response = await client.PutAsJsonAsync(SubmitChannelRoute, new
        {
            clientId = rawClientId,
            apiKey = rawApiKey,
            checksumKey = rawChecksumKey,
            beneficiaryAccountNumber = "1234567890",
            beneficiaryAccountName = "Nguyen Van A",
            beneficiaryBankCode = "VCB",
        });

        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().NotContain(rawClientId, "raw clientId must never appear in a response");
        responseBody.Should().NotContain(rawApiKey, "raw apiKey must never appear in a response");
        responseBody.Should().NotContain(rawChecksumKey, "raw checksumKey must never appear in a response");

        // Also verify the DB stores ciphertext, not plaintext
        string? storedClientRef = await database.ExecuteScalarAsync<string?>(
            $"SELECT client_id_ref FROM payment.OperatorPaymentChannels WHERE tour_operator_user_id = {operatorId};");
        storedClientRef.Should().NotBeNull();
        storedClientRef.Should().NotBe(rawClientId,
            "credential stored in DB must be ciphertext, not the raw value");
    }

    // ---- I4: Second submit for same operator updates (no duplicate row) --------

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SubmitCredentials_SecondSubmitForSameOperator_UpdatesExistingRowNotDuplicate()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var operatorId = await SeedApprovedOperatorAsync(database);

        using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(operatorId, UserRole.TourOperator);

        var firstPayload = new
        {
            clientId = "first-client-id",
            apiKey = "first-api-key",
            checksumKey = "first-checksum",
            beneficiaryAccountNumber = "1111111111",
            beneficiaryAccountName = "First Account",
            beneficiaryBankCode = "VCB",
        };
        var secondPayload = new
        {
            clientId = "second-client-id",
            apiKey = "second-api-key",
            checksumKey = "second-checksum",
            beneficiaryAccountNumber = "2222222222",
            beneficiaryAccountName = "Second Account",
            beneficiaryBankCode = "TCB",
        };

        var first = await client.PutAsJsonAsync(SubmitChannelRoute, firstPayload);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var second = await client.PutAsJsonAsync(SubmitChannelRoute, secondPayload);
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());

        int channelCount = await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM payment.OperatorPaymentChannels WHERE tour_operator_user_id = {operatorId};");
        channelCount.Should().Be(1, "second submit must update the existing row, not insert a duplicate");

        string? storedBankCode = await database.ExecuteScalarAsync<string?>(
            $"SELECT beneficiary_bank_code FROM payment.OperatorPaymentChannels WHERE tour_operator_user_id = {operatorId};");
        storedBankCode.Should().Be("TCB", "beneficiary info must reflect the most recent submit");
    }

    // ---- Seed helpers ---------------------------------------------------------

    private static async Task<long> SeedApprovedOperatorAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Email = $"operator-channel-test-{Guid.NewGuid():N}@example.com",
            FullName = "Operator Channel Test",
            PasswordHash = "hashed",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = now.AddDays(-10),
            UpdatedAtUtc = now.AddDays(-10),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            CompanyName = "Test Channel Company",
            TaxCode = $"TAX-{suffix}",
            BusinessLicenseNo = $"LIC-{suffix}",
            ContactPhone = "0901112223",
            ContactAddress = "Ho Chi Minh City",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CommissionRate = 10m,
            CreatedAtUtc = now.AddDays(-10),
            UpdatedAtUtc = now.AddDays(-10),
        });
        await db.SaveChangesAsync();
        return user.Id;
    }
}