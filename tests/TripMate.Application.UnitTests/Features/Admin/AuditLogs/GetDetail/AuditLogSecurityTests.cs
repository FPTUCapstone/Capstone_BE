using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Admin.AuditLogs.GetDetail;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.AuditLogs.GetDetail;

public class AuditLogSecurityTests
{
    [Theory]
    [InlineData("pwd=MySecret123")]
    [InlineData("IBAN: DE89370400440532013000")]
    [InlineData("pin: 1234")]
    [InlineData("Card: 4111 1111 1111 1111")]
    [InlineData("Support note: pwd=fixture-only")]
    [InlineData("Support supplied pwd=fixture-only")]
    [InlineData("Note: IBAN: DE89370400440532013000")]
    [InlineData("clientSecret=fixture-only")]
    [InlineData("Reviewed; access_token: fixture-only")]
    [InlineData("Details: \"password\": \"fixture-only\"")]
    public async Task Detail_MasksCredentialLabelsInReason(string reason)
    {
        using var db = TestDbContext.Create();
        var log = AuditLog.CreateRecordedOutcome(null, "Update", "User", 2, DateTimeOffset.UtcNow,
            TripMate.Domain.Enums.AuditOutcome.Success, reason);
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();
        var handler = new GetAuditLogDetailQueryHandler(db,
            new FakeCurrentUserService { UserId = 1, Role = "Administrator" });
        var result = await handler.Handle(new(log.Id), CancellationToken.None);
        result.Value.Reason.Should().Be("[REDACTED]");
    }

    [Theory]
    [InlineData("Pin location corrected.")]
    [InlineData("Card layout updated after review.")]
    [InlineData("Giấy phép kinh doanh không hợp lệ.")]
    public async Task Detail_PreservesOrdinaryBusinessReason(string reason)
    {
        using var db = TestDbContext.Create();
        var log = AuditLog.CreateRecordedOutcome(null, "Update", "User", 2, DateTimeOffset.UtcNow,
            TripMate.Domain.Enums.AuditOutcome.Success, reason);
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();
        var handler = new GetAuditLogDetailQueryHandler(db,
            new FakeCurrentUserService { UserId = 1, Role = "Administrator" });
        var result = await handler.Handle(new(log.Id), CancellationToken.None);
        result.Value.Reason.Should().Be(reason);
    }

    [Fact]
    public void Entity_DoesNotExposeAnEmptyConstructor()
    {
        typeof(AuditLog).GetConstructor(Type.EmptyTypes).Should().BeNull();
    }

    [Theory]
    [InlineData("", "User", null, null)]
    [InlineData("Update", " ", null, null)]
    [InlineData("Update", "User", 0L, null)]
    [InlineData("Update", "User", null, -1L)]
    public void Entity_RejectsInvalidRecordedIdentifiersAndNames(
        string action, string entity, long? actorId, long? entityId)
    {
        Action create = () => new AuditLog(actorId, action, entity, entityId, DateTimeOffset.UtcNow);
        create.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("password")]
    [InlineData("PASSWORD_HASH")]
    [InlineData("access_token")]
    [InlineData("refreshToken")]
    [InlineData("api-key")]
    [InlineData("clientSecret")]
    [InlineData("Authorization")]
    [InlineData("paymentCredentials")]
    [InlineData("cardNumber")]
    [InlineData("cvv")]
    [InlineData("bankAccountNumber")]
    [InlineData("cardCvv")]
    [InlineData("cardSecurityCode")]
    public async Task Detail_MasksCredentialsAtEveryDepth_WithoutChangingStorage(string field)
    {
        var payload = $$"""{"status":"Approved","reason":"Checked","nested":[{"{{field}}":"sensitive-test-value"}]}""";
        using var db = TestDbContext.Create();
        var log = new AuditLog(null, "Update", "User", 2, DateTimeOffset.UtcNow,
            beforeData: payload, afterData: payload);
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();
        var handler = new GetAuditLogDetailQueryHandler(db,
            new FakeCurrentUserService { UserId = 1, Role = "Administrator" });

        var result = await handler.Handle(new(log.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BeforeData.Should().NotContain("sensitive-test-value");
        result.Value.AfterData.Should().NotContain("sensitive-test-value");
        using var json = JsonDocument.Parse(result.Value.AfterData!);
        json.RootElement.GetProperty("nested")[0].GetProperty(field).GetString().Should().Be("[REDACTED]");
        json.RootElement.GetProperty("status").GetString().Should().Be("Approved");
        json.RootElement.GetProperty("reason").GetString().Should().Be("Checked");
        db.ChangeTracker.Clear();
        var stored = await db.AuditLogs.FindAsync(log.Id);
        stored!.BeforeData.Should().Be(payload);
        stored.AfterData.Should().Be(payload);
    }

    [Theory]
    [InlineData("password=sensitive-test-value")]
    [InlineData("{\"password\":\"sensitive-test-value\"")]
    [InlineData("\"sensitive-test-value\"")]
    [InlineData("[{\"field\":\"password\",\"previousValue\":\"sensitive-test-value\",\"newValue\":\"other\"}]")]
    [InlineData("{\"embedded\":\"{\\\"token\\\":\\\"sensitive-test-value\\\"}\"}")]
    [InlineData("[{\"name\":\"password\",\"value\":\"sensitive-test-value\"}]")]
    public async Task Detail_DoesNotLeakUnsupportedOrIndirectCredentialPayloads(string payload)
    {
        using var db = TestDbContext.Create();
        var log = new AuditLog(null, "Update", "User", 2, DateTimeOffset.UtcNow,
            beforeData: payload, afterData: payload);
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();
        var handler = new GetAuditLogDetailQueryHandler(db,
            new FakeCurrentUserService { UserId = 1, Role = "Administrator" });

        var result = await handler.Handle(new(log.Id), CancellationToken.None);

        result.Value.BeforeData.Should().NotContain("sensitive-test-value");
        result.Value.AfterData.Should().NotContain("sensitive-test-value");
        result.Value.AfterData.Should().Contain("[REDACTED]");
    }

    [Theory]
    [InlineData(51, 4, 9)]
    [InlineData(6, 81, 9)]
    [InlineData(6, 4, 46)]
    public void Entity_RejectsMetadataExceedingSchemaLengths(int actionLength, int entityLength, int ipLength)
    {
        Action create = () => new AuditLog(null, new string('a', actionLength), new string('e', entityLength),
            null, DateTimeOffset.UtcNow, ipAddress: new string('i', ipLength));
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Detail_WithNullOrExcessivelyNestedPayload_WithholdsUnsafeData()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"data\":", 40)) + "\"secret-fixture\"" + new string('}', 40);
        using var db = TestDbContext.Create();
        var log = new AuditLog(null, "Update", "User", 2, DateTimeOffset.UtcNow, afterData: deep);
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();
        var handler = new GetAuditLogDetailQueryHandler(db,
            new FakeCurrentUserService { UserId = 1, Role = "Administrator" });

        var result = await handler.Handle(new(log.Id), CancellationToken.None);

        result.Value.BeforeData.Should().BeNull();
        result.Value.AfterData.Should().NotContain("secret-fixture").And.Contain("[REDACTED]");
    }
}