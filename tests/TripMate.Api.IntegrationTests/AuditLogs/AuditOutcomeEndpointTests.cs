using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.AuditLogs;

[Collection(nameof(TripMateApiFactory))]
public class AuditOutcomeEndpointTests
{
    [Fact]
    public async Task ApprovalStateConflict_RecordsOneFailureAndPreservesResponse()
    {
        await using var factory = new TripMateApiFactory();
        var adminId = await SeedAdmin(factory);
        var targetId = await factory.WithDbContextAsync(async db =>
        {
            var user = new User { Email = "operator@example.com", FullName = "Reviewed operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        });
        using var client = factory.CreateAuthenticatedClient(adminId, UserRole.Administrator);

        var response = await client.PostAsync($"/api/v1/admin/tour-operator-applications/{targetId}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var audit = await factory.WithDbContextAsync(db => db.AuditLogs.SingleAsync());
        audit.Result.Should().Be(AuditOutcome.Failure);
        audit.ActorUserId.Should().Be(adminId);
        audit.Reason.Should().BeNull();
        audit.BeforeData.Should().BeNull();
        using var metadata = JsonDocument.Parse(audit.AfterData!);
        metadata.RootElement.GetProperty("auditMetadata").GetProperty("errorCode").GetString()
            .Should().Be("admin.tour_operator_application_not_pending");

        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/admin/audit-logs/{audit.Id}"));
        detail.RootElement.GetProperty("result").GetString().Should().Be("Failure");
        detail.RootElement.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/admin/audit-logs"));
        list.RootElement.GetProperty("items")[0].GetProperty("result").GetString().Should().Be("Failure");
    }

    [Fact]
    public async Task Recorder_UsesFreshContextAndDoesNotSaveDirtyBusinessEntities()
    {
        await using var factory = new TripMateApiFactory();
        var adminId = await SeedAdmin(factory);
        using var scope = factory.Services.CreateScope();
        var businessDb = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var admin = await businessDb.Users.SingleAsync(x => x.Id == adminId);
        admin.FullName = "Uncommitted name";
        businessDb.Users.Add(new User { Email = "uncommitted@example.com", FullName = "Uncommitted entity" });
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditFailureRecorder>();

        await recorder.RecordAsync(new(adminId, "ApproveOperatorApplication", "OperatorProfile", 999, "audit.database_write_failed"));

        var stored = await factory.WithDbContextAsync(db => db.Users.AsNoTracking().ToListAsync());
        stored.Should().ContainSingle().Which.FullName.Should().Be("Administrator");
        var logs = await factory.WithDbContextAsync(db => db.AuditLogs.ToListAsync());
        logs.Should().ContainSingle().Which.Result.Should().Be(AuditOutcome.Failure);
    }

    [Fact]
    public async Task LegacyAndReasonContract_DoesNotGuessSuccessOrExtractJsonReason()
    {
        await using var factory = new TripMateApiFactory();
        var adminId = await SeedAdmin(factory);
        var ids = await factory.WithDbContextAsync(async db =>
        {
            var legacy = new AuditLog(null, "Legacy", "User", null, DateTimeOffset.UtcNow, afterData: "{\"reason\":\"Unverified old note\"}");
            var rejected = AuditLog.CreateRecordedOutcome(adminId, "RejectOperatorApplication", "OperatorProfile", 42,
                DateTimeOffset.UtcNow, AuditOutcome.Success, "Giấy phép không hợp lệ");
            var sensitive = AuditLog.CreateRecordedOutcome(adminId, "Update", "User", 42,
                DateTimeOffset.UtcNow, AuditOutcome.Success, "password=fixture-secret");
            db.AuditLogs.AddRange(legacy, rejected, sensitive);
            await db.SaveChangesAsync();
            return (legacy.Id, Rejected: rejected.Id, Sensitive: sensitive.Id);
        });
        using var client = factory.CreateAuthenticatedClient(adminId, UserRole.Administrator);
        using var legacyJson = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/admin/audit-logs/{ids.Id}"));
        legacyJson.RootElement.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
        legacyJson.RootElement.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        using var rejectJson = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/admin/audit-logs/{ids.Rejected}"));
        rejectJson.RootElement.GetProperty("result").GetString().Should().Be("Success");
        rejectJson.RootElement.GetProperty("reason").GetString().Should().Be("Giấy phép không hợp lệ");
        using var secretJson = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/admin/audit-logs/{ids.Sensitive}"));
        secretJson.RootElement.GetProperty("reason").GetString().Should().Be("[REDACTED]");
    }

    [Fact]
    public async Task ValidationAuthorizationAndReadRequests_DoNotCreateBusinessAudit()
    {
        await using var factory = new TripMateApiFactory();
        var adminId = await SeedAdmin(factory);
        using var admin = factory.CreateAuthenticatedClient(adminId, UserRole.Administrator);
        using var traveler = factory.CreateAuthenticatedClient(adminId, UserRole.Traveler);
        (await admin.PostAsJsonAsync("/api/v1/admin/pois", new { name = "", categoryId = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await traveler.PostAsync("/api/v1/admin/tour-operator-applications/999/approve", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetAsync("/api/v1/admin/audit-logs/999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await factory.WithDbContextAsync(db => db.AuditLogs.CountAsync())).Should().Be(0);
    }

    private static Task<long> SeedAdmin(TripMateApiFactory factory) => factory.WithDbContextAsync(async db =>
    {
        var user = new User { FullName = "Administrator", Email = "admin@example.com", Role = UserRole.Administrator, Status = AccountStatus.Active };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    });
}