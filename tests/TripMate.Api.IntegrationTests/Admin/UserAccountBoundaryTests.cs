using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Providers.CommissionStatements;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Provider = TripMate.Domain.Entities.ServiceProvider;

namespace TripMate.Api.IntegrationTests.Admin;

[Collection(nameof(TripMateApiFactory))]
public sealed class UserAccountBoundaryTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SqlServer_UnlockPathsRequireMatchingLockEvidenceAndAuditScopedDenials()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        await using var db = database.CreateDbContext();
        User NewAccount(UserRole role, string name) => new()
        {
            Role = role,
            Status = AccountStatus.Active,
            FullName = name,
            Email = $"{Guid.NewGuid():N}@tm228.test"
        };

        var staff = NewAccount(UserRole.Staff, "Staff");
        var legacy = NewAccount(UserRole.ServiceProvider, "No audit");
        var wrongTime = NewAccount(UserRole.TourOperator, "Wrong audit time");
        var unsuccessful = NewAccount(UserRole.TourOperator, "Failed audit");
        var humanActor = NewAccount(UserRole.TourOperator, "Human audit actor");
        var regular = NewAccount(UserRole.TourOperator, "Operator unlock without key");
        var keyed = NewAccount(UserRole.TourOperator, "Operator unlock with key");
        var denied = NewAccount(UserRole.Administrator, "Out of scope");
        var unknown = new[] { legacy, wrongTime, unsuccessful, humanActor };
        db.Users.AddRange(unknown.Concat([staff, regular, keyed, denied]));
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow.AddHours(-1).AddTicks(7);
        foreach (var user in unknown.Concat([regular, keyed]))
            user.Lock("Unrecovered pending debt (BR-189)", now);

        db.AuditLogs.AddRange(
            OperatorAudit(wrongTime, now.AddSeconds(-1), null, AuditOutcome.Success),
            OperatorAudit(unsuccessful, now, null, AuditOutcome.Failure),
            OperatorAudit(humanActor, now, staff.Id, AuditOutcome.Success),
            OperatorAudit(regular, now, null, AuditOutcome.Success),
            OperatorAudit(keyed, now, null, AuditOutcome.Success));
        await db.SaveChangesAsync();

        using var client = factory.CreateAuthenticatedClient(staff.Id, UserRole.Staff);
        foreach (var user in unknown)
        {
            using var standard = await Unlock(user.Id, false);
            using var withKey = await Unlock(user.Id, true);
            standard.StatusCode.Should().Be(HttpStatusCode.Conflict, user.FullName);
            withKey.StatusCode.Should().Be(HttpStatusCode.Conflict, user.FullName);
            using var standardProblem = JsonDocument.Parse(await standard.Content.ReadAsStringAsync());
            using var keyedProblem = JsonDocument.Parse(await withKey.Content.ReadAsStringAsync());
            standardProblem.RootElement.GetProperty("errorCode").GetString().Should().Be("user.lock_recovery_state_missing");
            keyedProblem.RootElement.GetProperty("errorCode").GetString().Should().Be("user.lock_recovery_state_missing");
        }
        using var regularResponse = await Unlock(regular.Id, false);
        using var keyedResponse = await Unlock(keyed.Id, true);
        regularResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        keyedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var outside = await Unlock(denied.Id, true);
        using var missing = await Unlock(long.MaxValue, true);
        outside.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(outside.StatusCode);
        using var outsideProblem = JsonDocument.Parse(await outside.Content.ReadAsStringAsync());
        using var missingProblem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        outsideProblem.RootElement.GetProperty("errorCode").GetString().Should().Be("user.not_found");
        outsideProblem.RootElement.GetProperty("title").GetString().Should()
            .Be(missingProblem.RootElement.GetProperty("title").GetString());
        outsideProblem.RootElement.GetProperty("errorCode").GetString().Should()
            .Be(missingProblem.RootElement.GetProperty("errorCode").GetString());
        var deniedAudit = await db.AuditLogs.SingleAsync(log => log.ActionType == AuditActionTypes.UserUnlock
            && log.Result == AuditOutcome.Failure && log.AffectedEntityId == denied.Id);
        deniedAudit.ActorUserId.Should().Be(staff.Id);

        var unknownIds = unknown.Select(user => user.Id).ToArray();
        (await db.Users.AsNoTracking().Where(user => unknownIds.Contains(user.Id)).ToListAsync())
            .Should().OnlyContain(user => user.Status == AccountStatus.Locked && user.StatusBeforeLock == AccountStatus.Active);
        (await db.AuditLogs.CountAsync(log => log.Result == AuditOutcome.Success
            && (log.ActionType == AuditActionTypes.UserUnlock || log.ActionType == AuditActionTypes.UserAccountUnlocked))).Should().Be(2);
        var operation = await db.UserUnlockOperations.SingleAsync();
        operation.TargetUserId.Should().Be(keyed.Id);
        var restoredIds = new[] { regular.Id, keyed.Id };
        (await db.Users.AsNoTracking().Where(user => restoredIds.Contains(user.Id)).ToListAsync())
            .Should().OnlyContain(user => user.Status == AccountStatus.Active && user.StatusBeforeLock == null);

        async Task<HttpResponseMessage> Unlock(long userId, bool hasKey)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/users/{userId}/unlock")
            {
                Content = JsonContent.Create(new { reason = "Reviewed lock evidence" })
            };
            if (hasKey) request.Headers.Add("Idempotency-Key", $"tm228-evidence-{userId}");
            return await client.SendAsync(request);
        }

        static AuditLog OperatorAudit(User target, DateTimeOffset at, long? actor, AuditOutcome result) =>
            AuditLog.CreateRecordedOutcome(actor, AuditActionTypes.TourOperatorAutoLocked, AuditEntityTypes.User,
                target.Id, at, result);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SqlServer_UnlockPathsEnforceRoleScopeAndProviderSettlement()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        await using var db = database.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.ToOffset(ServiceBooking.VietnamOffset).DateTime);
        User NewAccount(UserRole role, string label) => new()
        {
            Role = role,
            Status = AccountStatus.Active,
            FullName = label,
            Email = $"{label}@tm228.test"
        };

        var staff = NewAccount(UserRole.Staff, "staff");
        var admin = NewAccount(UserRole.Administrator, "admin");
        var targets = new[]
        {
            NewAccount(UserRole.Traveler, "traveler"), NewAccount(UserRole.TourOperator, "operator"),
            NewAccount(UserRole.ServiceProvider, "provider"), NewAccount(UserRole.Staff, "staff-target"),
            NewAccount(UserRole.Administrator, "admin-target")
        };
        var provider = Provider.Create("Settlement provider", CommercialService.CategoryHotel, Provider.StatusActive);
        provider.AssignAccount(targets[2]);
        db.Users.AddRange(targets.Append(staff).Append(admin));
        db.ServiceProviders.Add(provider);
        await db.SaveChangesAsync();

        foreach (var target in targets.Where(target => target.Role != UserRole.ServiceProvider))
            target.Lock("Review", now, staff.Id);

        var overdue = ServiceCommissionStatement.Create("TM228-1", provider.Id, "2026-01", 1, 100, 10,
            today.AddDays(-30), today.AddDays(-15));
        var anotherOverdue = ServiceCommissionStatement.Create("TM228-2", provider.Id, "2026-02", 1, 100, 10,
            today.AddDays(-20), today.AddDays(-1));
        var dueToday = ServiceCommissionStatement.Create("TM228-3", provider.Id, "2026-03", 1, 100, 10,
            today.AddDays(-10), today);
        db.ServiceCommissionStatements.AddRange(overdue, anotherOverdue, dueToday);
        const string rawRefreshToken = "tm228-existing-provider-token";
        using var authScope = factory.Services.CreateScope();
        var jwt = authScope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        db.RefreshTokens.Add(new RefreshToken
        {
            User = targets[2],
            TokenHash = jwt.HashRefreshToken(rawRefreshToken),
            CreatedAtUtc = now.AddHours(-1),
            ExpiresAtUtc = now.AddDays(1)
        });
        await db.SaveChangesAsync();

        using var anonymous = factory.CreateClient();
        async Task<HttpStatusCode> Refresh()
        {
            using var response = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rawRefreshToken });
            return response.StatusCode;
        }

        (await Refresh()).Should().Be(HttpStatusCode.OK);
        var job = new CommissionStatementService(db);
        var summary = await job.ProcessOverdueStatementsAsync(now);
        summary.AccountsLockedCount.Should().Be(1);
        (await Refresh()).Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        using var staffClient = factory.CreateAuthenticatedClient(staff.Id, UserRole.Staff);
        using var adminClient = factory.CreateAuthenticatedClient(admin.Id, UserRole.Administrator);
        async Task<HttpStatusCode> Unlock(HttpClient client, long userId, string? key = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/users/{userId}/unlock")
            {
                Content = JsonContent.Create(new { reason = "Verified settlement or review" })
            };
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        foreach (var key in new[] { null, "tm228-denied" })
        {
            foreach (var target in targets.Take(3))
                (await Unlock(adminClient, target.Id, key)).Should().Be(HttpStatusCode.NotFound, $"{target.Role} is outside Administrator scope");
            foreach (var target in targets.Skip(3))
                (await Unlock(staffClient, target.Id, key)).Should().Be(HttpStatusCode.NotFound, $"{target.Role} is outside Staff scope");
            (await Unlock(staffClient, long.MaxValue, key)).Should().Be(HttpStatusCode.NotFound);
            (await Unlock(staffClient, targets[2].Id, key)).Should().Be(HttpStatusCode.Conflict);
        }

        overdue.ConfirmReceipt(staff.Id, now, "receipt-1");
        await db.SaveChangesAsync();
        (await Unlock(staffClient, targets[2].Id)).Should().Be(HttpStatusCode.Conflict);
        anotherOverdue.ConfirmReceipt(staff.Id, now, "receipt-2");
        await db.SaveChangesAsync();

        (await Unlock(staffClient, targets[2].Id, "tm228-provider")).Should().Be(HttpStatusCode.OK);
        (await Refresh()).Should().Be(HttpStatusCode.Unauthorized);
        (await Unlock(staffClient, targets[2].Id, "tm228-provider")).Should().Be(HttpStatusCode.OK);
        (await Unlock(staffClient, targets[0].Id, "tm228-traveler")).Should().Be(HttpStatusCode.OK);
        (await Unlock(staffClient, targets[1].Id)).Should().Be(HttpStatusCode.OK);
        (await Unlock(adminClient, targets[3].Id, "tm228-staff")).Should().Be(HttpStatusCode.OK);

        var targetIds = targets.Select(target => target.Id).ToArray();
        var current = await db.Users.AsNoTracking().Where(user => targetIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id);
        foreach (var target in targets)
            current[target.Id].Role.Should().Be(target.Role);
        current[targets[2].Id].Status.Should().Be(AccountStatus.Active);
        current[targets[4].Id].Status.Should().Be(AccountStatus.Locked);
        var token = await db.RefreshTokens.AsNoTracking().SingleAsync(item => item.UserId == targets[2].Id);
        token.RevokedAtUtc.Should().NotBeNull();
        (await db.AuditLogs.CountAsync(log => log.AffectedEntityId == targets[2].Id
            && log.ActionType == AuditActionTypes.UserUnlock && log.Result == AuditOutcome.Success)).Should().Be(1);

        var changedTarget = await db.Users.SingleAsync(user => user.Id == targets[0].Id);
        await db.Entry(changedTarget).ReloadAsync();
        changedTarget.Role = UserRole.Staff;
        await db.SaveChangesAsync();
        (await Unlock(staffClient, changedTarget.Id, "tm228-traveler")).Should().Be(HttpStatusCode.NotFound);

        using var relock = await staffClient.PostAsJsonAsync($"/api/v1/admin/users/{targets[2].Id}/lock", new { reason = "Policy review" });
        relock.StatusCode.Should().Be(HttpStatusCode.OK);
        var concurrentUnlocks = await Task.WhenAll(Unlock(staffClient, targets[2].Id),
            Unlock(staffClient, targets[2].Id, "tm228-provider-concurrent"));
        concurrentUnlocks.Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await db.AuditLogs.CountAsync(log => log.AffectedEntityId == targets[2].Id && log.Result == AuditOutcome.Success
            && (log.ActionType == AuditActionTypes.UserUnlock || log.ActionType == AuditActionTypes.UserAccountUnlocked))).Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SqlServer_StaffCanManuallyLockAndRestoreProviderButCannotReleaseUnverifiedLegacyLock()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        await using var db = database.CreateDbContext();
        var staff = new User { Role = UserRole.Staff, Status = AccountStatus.Active, FullName = "Staff", Email = "staff@tm228.test" };
        var provider = new User { Role = UserRole.ServiceProvider, Status = AccountStatus.Active, FullName = "Provider", Email = "provider@tm228.test" };
        var legacy = new User { Role = UserRole.ServiceProvider, Status = AccountStatus.Locked, FullName = "Legacy", Email = "legacy@tm228.test" };
        db.Users.AddRange(staff, provider, legacy);
        await db.SaveChangesAsync();
        using var client = factory.CreateAuthenticatedClient(staff.Id, UserRole.Staff);
        using var directory = await client.GetAsync("/api/v1/admin/users?role=ServiceProvider");
        directory.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await directory.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(2);

        using var locked = await client.PostAsJsonAsync($"/api/v1/admin/users/{provider.Id}/lock", new { reason = "Policy" });
        using var restored = await client.PostAsJsonAsync($"/api/v1/admin/users/{provider.Id}/unlock", new { reason = "Resolved" });
        using var blocked = await client.PostAsJsonAsync($"/api/v1/admin/users/{legacy.Id}/unlock", new { reason = "Unverified" });
        locked.StatusCode.Should().Be(HttpStatusCode.OK);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var lockPath = $"/api/v1/admin/users/{provider.Id}/lock";
        var results = await Task.WhenAll(
            client.PostAsJsonAsync(lockPath, new { reason = "Concurrent policy A" }),
            client.PostAsJsonAsync(lockPath, new { reason = "Concurrent policy B" }));
        try
        {
            results.Select(result => result.StatusCode).Should().BeEquivalentTo(
                [HttpStatusCode.OK, HttpStatusCode.Conflict]);
        }
        finally
        {
            foreach (var result in results) result.Dispose();
        }
    }

    [Theory]
    [InlineData("/api/v1/admin/tour-operator-applications/9999", false)]
    [InlineData("/api/v1/admin/tours/9999/approve", true)]
    [InlineData("/api/v1/admin/trips/active/9999", false)]
    public async Task Operations_StaffCanReachHandlerAndAdministratorIsDenied(string path, bool post)
    {
        await using var factory = await SeedAsync();
        using var staff = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var admin = factory.CreateAuthenticatedClient(20, UserRole.Administrator);
        using var staffResponse = post ? await staff.PostAsync(path, null) : await staff.GetAsync(path);
        using var adminResponse = post ? await admin.PostAsync(path, null) : await admin.GetAsync(path);
        staffResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Lock_InvalidatesSignedAccessTokensEvenAfterUnlock()
    {
        await using var factory = await SeedAsync(ApiTestAuthenticationMode.JwtBearer);
        async Task<string> Token(long userId)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var user = await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
            return scope.ServiceProvider.GetRequiredService<IJwtTokenService>().GenerateAccessToken(user).Token;
        }
        using var staff = factory.CreateJwtClient(await Token(10));
        using var administrator = factory.CreateJwtClient(await Token(20));
        (await staff.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await administrator.PostAsJsonAsync("/api/v1/admin/users/10/lock", new { reason = "Policy" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await administrator.PostAsJsonAsync("/api/v1/admin/users/10/unlock", new { reason = "Resolved" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await staff.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var fresh = factory.CreateJwtClient(await Token(10));
        (await fresh.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(UserRole.Staff, 10, "Traveler", "TourOperator")]
    [InlineData(UserRole.Administrator, 20, "Staff", "Staff")]
    public async Task List_FiltersByAuthoritativeRoleBeforeCountAndPage(UserRole role, long actorId, string first, string second)
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(actorId, role);
        using var response = await client.GetAsync("/api/v1/admin/users");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("role").GetString())
            .Should().OnlyContain(value => value == first || value == second);
        json.GetProperty("totalCount").GetInt32().Should().Be(role == UserRole.Staff ? 2 : 1);
        json.ToString().Should().NotContain("passwordHash").And.NotContain("TokenHash");
    }

    [Theory]
    [InlineData(UserRole.Staff, 10, 20)]
    [InlineData(UserRole.Administrator, 20, 30)]
    [InlineData(UserRole.Administrator, 20, 40)]
    public async Task Detail_OutOfScopeAndMissingAreIndistinguishable(UserRole role, long actor, long target)
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(actor, role);
        using var forbidden = await client.GetAsync($"/api/v1/admin/users/{target}");
        using var missing = await client.GetAsync("/api/v1/admin/users/9999");
        forbidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(forbidden.StatusCode);
        var left = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync()).RootElement;
        var right = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        left.GetProperty("errorCode").GetString().Should().Be(right.GetProperty("errorCode").GetString());
        var denials = await factory.WithDbContextAsync(db => db.AuditLogs
            .Where(row => row.ActorUserId == actor && row.ActionType == AuditActionTypes.AdministrationAccessDenied)
            .ToListAsync());
        denials.Should().HaveCount(2).And.OnlyContain(row =>
            row.Result == AuditOutcome.Failure && row.AffectedEntityId == null);
    }

    [Theory]
    [InlineData("lock", "")]
    [InlineData("unlock", " ")]
    public async Task Mutation_RequiresReason(string action, string reason)
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var response = await client.PostAsJsonAsync($"/api/v1/admin/users/30/{action}", new { reason });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Lock_RevokesRefreshTokensAndUnlockRestoresPendingStatusWithAudit()
    {
        await using var factory = await SeedAsync();
        await factory.WithDbContextAsync(async db =>
        {
            var target = await db.Users.SingleAsync(user => user.Id == 30);
            target.Status = AccountStatus.PendingEmailVerification;
            db.RefreshTokens.Add(new RefreshToken { User = target, TokenHash = "private-token-hash", ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            return true;
        });
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var locked = await client.PostAsJsonAsync("/api/v1/admin/users/30/lock", new { reason = "Policy violation" });
        locked.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == 30)).Status.Should().Be(AccountStatus.Locked);
            (await db.RefreshTokens.SingleAsync()).RevokedAtUtc.Should().NotBeNull();
            return true;
        });
        using var unlocked = await client.PostAsJsonAsync("/api/v1/admin/users/30/unlock", new { reason = "Resolved" });
        unlocked.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == 30)).Status.Should().Be(AccountStatus.PendingEmailVerification);
            (await db.AuditLogs.CountAsync(row => row.AffectedEntityId == 30 && row.Result == AuditOutcome.Success)).Should().Be(2);
            return true;
        });
    }

    [Fact]
    public async Task Mutation_CannotChangeRole()
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var response = await client.PostAsJsonAsync("/api/v1/admin/users/30/lock", new { reason = "Policy", role = "Administrator" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == 30)).Role.Should().Be(UserRole.Traveler);
            return true;
        });
    }

    [Theory]
    [InlineData(UserRole.Staff, 10, 20)]
    [InlineData(UserRole.Administrator, 20, 30)]
    public async Task Lock_CannotCrossRoleBoundary(UserRole role, long actor, long target)
    {
        await using var factory = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient(actor, role);
        using var response = await client.PostAsJsonAsync($"/api/v1/admin/users/{target}/lock", new { reason = "Policy" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == target)).Status.Should().Be(AccountStatus.Active);
            return true;
        });
    }

    private static async Task<TripMateApiFactory> SeedAsync(ApiTestAuthenticationMode mode = ApiTestAuthenticationMode.HeaderStub)
    {
        var factory = new TripMateApiFactory(mode);
        await factory.WithDbContextAsync(async db =>
        {
            foreach (var (id, role) in new[] { (10L, UserRole.Staff), (20L, UserRole.Administrator), (30L, UserRole.Traveler), (40L, UserRole.TourOperator) })
                db.Users.Add(new User { Id = id, Role = role, Status = AccountStatus.Active, FullName = role.ToString(), Email = $"{id}@test.invalid", PasswordHash = "credential-must-not-leak" });
            await db.SaveChangesAsync();
            return true;
        });
        return factory;
    }
}