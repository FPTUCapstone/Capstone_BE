using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    [Fact]
    public async Task PayoutUnlock_UsesAccountModerationRulesAndPreservesPendingStatus()
    {
        await using var factory = await SeedAsync();
        await factory.WithDbContextAsync(async db =>
        {
            var target = await db.Users.SingleAsync(user => user.Id == 40L);
            target.Status = AccountStatus.PendingApproval;
            target.Lock("Application review", DateTimeOffset.UtcNow, 10L);
            var debt = TourOperatorDebt.Create(
                target.Id, 500_000m, DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddDays(-20));
            debt.Recover(125_000m);
            db.TourOperatorDebts.Add(debt);
            await db.SaveChangesAsync();
            return true;
        });

        using var staff = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var administrator = factory.CreateAuthenticatedClient(20, UserRole.Administrator);
        const string payoutPath = "/api/v1/admin/payout-settlements/tour-operators/40/unlock";
        const string accountPath = "/api/v1/admin/users/40/unlock";
        var payload = new { reason = "Verified repayment with Finance" };

        using var noReason = await staff.PostAsJsonAsync(payoutPath, new { reason = "  " });
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var denied = await administrator.PostAsJsonAsync(payoutPath, payload);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var wrongTarget = await staff.PostAsJsonAsync(
            "/api/v1/admin/payout-settlements/tour-operators/30/unlock", payload);
        wrongTarget.StatusCode.Should().Be(HttpStatusCode.NotFound);

        foreach (var path in new[] { payoutPath, accountPath })
        {
            using var blocked = await staff.PostAsJsonAsync(path, payload);
            blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
            using var problem = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
            problem.RootElement.GetProperty("messageCode").GetString().Should().Be("MSG199");
            problem.RootElement.GetProperty("outstandingAmountVnd").GetDecimal().Should().Be(375_000m);
        }

        await factory.WithDbContextAsync(async db =>
        {
            var debt = await db.TourOperatorDebts.SingleAsync();
            debt.Recover(debt.Amount - debt.RecoveredAmount);
            await db.SaveChangesAsync();
            return true;
        });

        using var unlocked = await staff.PostAsJsonAsync(payoutPath, payload);
        unlocked.StatusCode.Should().Be(HttpStatusCode.OK);
        using var data = JsonDocument.Parse(await unlocked.Content.ReadAsStringAsync());
        data.RootElement.GetProperty("tourOperatorUserId").GetInt64().Should().Be(40L);
        data.RootElement.GetProperty("status").GetString().Should().Be(nameof(AccountStatus.PendingApproval));
        using var duplicate = await staff.PostAsJsonAsync(payoutPath, payload);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await factory.WithDbContextAsync(async db =>
        {
            var records = await db.AuditLogs.Where(log => log.AffectedEntityId == 40L
                && log.ActionType == AuditActionTypes.UserUnlock && log.Result == AuditOutcome.Success).ToListAsync();
            records.Should().ContainSingle().Which.Reason.Should().Be(payload.reason);
            (await db.Notifications.CountAsync(notification => notification.UserId == 40L)).Should().Be(1);
            return true;
        });
    }

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
        var provider = Provider.Create("Settlement provider", CommercialService.CategoryRestaurant, Provider.StatusActive);
        provider.AssignAccount(targets[2]);
        db.Users.AddRange(targets.Append(staff).Append(admin));
        db.ServiceProviders.Add(provider);
        await db.SaveChangesAsync();

        var category = PoiCategory.Create("TM-228 recovery restaurant", null);
        var poi = PointOfInterest.Create(category, "TM-228 settlement venue", 16.07m, 108.22m, staff.Id, now);
        db.ProviderPoiLinks.Add(ProviderPoiLink.Create(provider, poi));
        await db.SaveChangesAsync();
        var bookableService = CommercialService.Create(provider, CommercialService.CategoryRestaurant, "Dinner reservation",
            100_000m, CommercialService.PriceUnitPerPerson, CommercialService.AvailabilityAvailable, "VND", true,
            null, null, null, now, poiId: poi.Id);
        db.CommercialServices.Add(bookableService);

        foreach (var target in targets.Where(target => target.Role != UserRole.ServiceProvider))
            target.Lock("Review", now, staff.Id);

        var overdue = ServiceCommissionStatement.Create("TM228-1", provider.Id, "2026-01", 1, 100, 10,
            today.AddDays(-30), today.AddDays(-15));
        var anotherOverdue = ServiceCommissionStatement.Create("TM228-2", provider.Id, "2026-02", 1, 100, 10,
            today.AddDays(-20), today.AddDays(-1));
        var dueToday = ServiceCommissionStatement.Create("TM228-3", provider.Id, "2026-03", 1, 100, 10,
            today.AddDays(-10), today);
        var oldZero = ServiceCommissionStatement.Create("TM228-ZERO", provider.Id, "2025-12", 1, 9, 0,
            today.AddDays(-40), today.AddDays(-33));
        oldZero.MarkOverdue();
        anotherOverdue.ReportPayment("REPORTED-WRONG", now.AddDays(-2));
        db.ServiceCommissionStatements.AddRange(overdue, anotherOverdue, dueToday, oldZero);
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
        async Task<int> BookableOptions()
        {
            using var response = await anonymous.GetAsync($"/api/v1/commercial-services/poi/{poi.Id}/options");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return data.RootElement.GetArrayLength();
        }

        async Task<HttpStatusCode> Refresh()
        {
            using var response = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rawRefreshToken });
            return response.StatusCode;
        }

        (await Refresh()).Should().Be(HttpStatusCode.OK);
        (await BookableOptions()).Should().Be(1);
        var job = new CommissionStatementService(db);
        var summary = await job.ProcessOverdueStatementsAsync(now);
        summary.AccountsLockedCount.Should().Be(1);
        (await Refresh()).Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await BookableOptions()).Should().Be(0);
        using var blockedAvailability = await anonymous.GetAsync($"/api/v1/commercial-services/{bookableService.Id}/availability");
        blockedAvailability.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var staffClient = factory.CreateAuthenticatedClient(staff.Id, UserRole.Staff);
        using var adminClient = factory.CreateAuthenticatedClient(admin.Id, UserRole.Administrator);
        async Task<HttpStatusCode> Unlock(HttpClient client, long userId, string? key = null, decimal? outstanding = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/users/{userId}/unlock")
            {
                Content = JsonContent.Create(new { reason = "Verified settlement or review" })
            };
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            if (outstanding is { } amount)
            {
                response.StatusCode.Should().Be(HttpStatusCode.Conflict);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                problem.RootElement.GetProperty("messageCode").GetString().Should().Be("MSG199");
                problem.RootElement.GetProperty("outstandingAmountVnd").GetDecimal().Should().Be(amount);
            }
            return response.StatusCode;
        }

        foreach (var key in new[] { null, "tm228-denied" })
        {
            foreach (var target in targets.Take(3))
                (await Unlock(adminClient, target.Id, key)).Should().Be(HttpStatusCode.NotFound, $"{target.Role} is outside Administrator scope");
            foreach (var target in targets.Skip(3))
                (await Unlock(staffClient, target.Id, key)).Should().Be(HttpStatusCode.NotFound, $"{target.Role} is outside Staff scope");
            (await Unlock(staffClient, long.MaxValue, key)).Should().Be(HttpStatusCode.NotFound);
            (await Unlock(staffClient, targets[2].Id, key, outstanding: 20m)).Should().Be(HttpStatusCode.Conflict);
        }

        const string reconciliation = "/api/v1/admin/commercial-settlements/reconciliation";
        using var deniedReconciliation = await adminClient.GetAsync(reconciliation);
        deniedReconciliation.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var deniedReceipt = await adminClient.PostAsJsonAsync(
            $"{reconciliation}/{overdue.Id}/reconcile", new { bankTransferReference = "BANK-202601" });
        deniedReceipt.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var missingReference = await staffClient.PostAsJsonAsync(
            $"{reconciliation}/{overdue.Id}/reconcile", new { notes = "Checked bank statement" });
        missingReference.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var issuedAttempt = await staffClient.PostAsJsonAsync(
            $"{reconciliation}/{dueToday.Id}/reconcile",
            new { bankTransferReference = "UNREPORTED", receivedAmountVnd = 10m });
        issuedAttempt.StatusCode.Should().Be(HttpStatusCode.Conflict);

        async Task ConfirmReceipt(long statementId, string reference, string? expectedReported = null)
        {
            using var receipt = await staffClient.PostAsJsonAsync(
                $"{reconciliation}/{statementId}/reconcile",
                new { bankTransferReference = reference, receivedAmountVnd = 10m });
            receipt.StatusCode.Should().Be(HttpStatusCode.OK);
            using var data = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
            data.RootElement.GetProperty("bankTransferReference").GetString().Should().Be(reference);
            data.RootElement.GetProperty("verifiedBankTransferReference").GetString().Should().Be(reference);
            data.RootElement.GetProperty("reportedBankTransferReference").GetString().Should().Be(expectedReported);
            if (expectedReported is null)
                data.RootElement.GetProperty("reportedAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
        }

        await ConfirmReceipt(overdue.Id, "BANK-202601");
        (await Unlock(staffClient, targets[2].Id, outstanding: 10m)).Should().Be(HttpStatusCode.Conflict);
        using var repeatedTransfer = await staffClient.PostAsJsonAsync(
            $"{reconciliation}/{anotherOverdue.Id}/reconcile",
            new { bankTransferReference = " bank-202601 ", receivedAmountVnd = 10m });
        repeatedTransfer.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var repeatedProblem = JsonDocument.Parse(await repeatedTransfer.Content.ReadAsStringAsync());
        repeatedProblem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be("CommissionStatement.BankReferenceAlreadyUsed");
        await ConfirmReceipt(anotherOverdue.Id, "BANK-202602", "REPORTED-WRONG");

        (await Unlock(staffClient, targets[2].Id, "tm228-provider")).Should().Be(HttpStatusCode.OK);
        (await Refresh()).Should().Be(HttpStatusCode.Unauthorized);
        (await BookableOptions()).Should().Be(1);
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
    public async Task SqlServer_ConcurrentReceiptsCannotUseTheSameBankCredit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var migration = Path.Combine(SqlServerTestDatabase.DefaultMigrationsDirectory(),
            "20261128_tm228_verified_commission_receipt.sql");
        await database.ExecuteNonQueryAsync(await File.ReadAllTextAsync(migration));
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        await using var db = database.CreateDbContext();
        var staff = new User
        {
            Role = UserRole.Staff, Status = AccountStatus.Active, FullName = "Receipt staff",
            Email = "receipt-staff@tm228.test",
        };
        var provider = Provider.Create("Concurrent receipt provider", CommercialService.CategoryRestaurant, Provider.StatusActive);
        db.Users.Add(staff);
        db.ServiceProviders.Add(provider);
        await db.SaveChangesAsync();
        var issueDate = new DateOnly(2026, 1, 1);
        var first = ServiceCommissionStatement.Create("TM228-R1", provider.Id, "2025-11", 1, 100, 10,
            issueDate, issueDate.AddDays(7));
        var second = ServiceCommissionStatement.Create("TM228-R2", provider.Id, "2025-12", 1, 100, 10,
            issueDate, issueDate.AddDays(7));
        first.MarkOverdue();
        second.MarkOverdue();
        db.ServiceCommissionStatements.AddRange(first, second);
        await db.SaveChangesAsync();

        using var client = factory.CreateAuthenticatedClient(staff.Id, UserRole.Staff);
        async Task<HttpStatusCode> Reconcile(long id)
        {
            using var response = await client.PostAsJsonAsync($"/api/v1/admin/commercial-settlements/reconciliation/{id}/reconcile",
                new { bankTransferReference = "SAME-CREDIT-001", receivedAmountVnd = 10 });
            return response.StatusCode;
        }

        var outcomes = await Task.WhenAll(Reconcile(first.Id), Reconcile(second.Id));
        outcomes.Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await db.ServiceCommissionStatements.CountAsync(row => row.Status == ServiceCommissionStatement.StatusPaid))
            .Should().Be(1);
        (await db.AuditLogs.CountAsync(row => row.ActionType == AuditActionTypes.CommissionReceiptConfirmed))
            .Should().Be(1);
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

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SqlServer_TranslatesBookingOwnershipAndIncludesTheLastTickOfVietnamDate()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        await using var db = database.CreateDbContext();
        var now = new DateTimeOffset(2026, 11, 15, 17, 0, 0, TimeSpan.Zero);
        static User Account(string name, UserRole role, DateTimeOffset createdAt) => new()
        {
            FullName = name,
            Email = $"{name}@tm93.test",
            Role = role,
            Status = AccountStatus.Active,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt
        };
        var staff = Account("Staff", UserRole.Staff, now);
        var traveler = Account("Traveler", UserRole.Traveler, now);
        var tourOperator = Account("Operator", UserRole.TourOperator, now.AddDays(1).AddTicks(-1));
        var nextDay = Account("NextDay", UserRole.Traveler, now.AddDays(1));
        db.Users.AddRange(staff, traveler, tourOperator, nextDay);
        await db.SaveChangesAsync();
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = tourOperator.Id,
            CompanyName = "TM93 operator",
            TaxCode = "0012345678",
            BusinessLicenseNo = "TM93-BIZ",
            ApprovalStatus = OperatorApprovalStatus.Approved
        });
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), tourOperator.Id);
        Set(tour, nameof(Tour.Title), "TM93 SQL tour");
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.BasePrice), 500_000m);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourSchedule.Create(now.AddDays(7), now.AddDays(7).AddHours(4), "Da Nang", 10, now,
            registrationDeadlineAtUtc: now.AddDays(6), adultPrice: 500_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        schedule.ReserveCapacity(1);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        db.Bookings.Add(Booking.Create("BK-TM93-SQL-01", traveler.Id, schedule.Id, null, 500_000m, 0m, 500_000m,
            [BookingParticipant.Create("TM93 traveler", ParticipantType.Adult, null, null, null, null, null)],
            now, now.AddMinutes(15)));
        await db.SaveChangesAsync();

        using var staffClient = factory.CreateAuthenticatedClient(staff.Id, UserRole.Staff);
        using var range = await staffClient.GetAsync("/api/v1/admin/users?registeredFrom=2026-11-16T00:00:00%2B07:00&registeredTo=2026-11-16T23:59:59.9999999%2B07:00");
        range.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await range.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("fullName").GetString())
            .Should().Equal("Operator", "Traveler");
        (await ReadBookingCount(staffClient, traveler.Id)).GetInt32().Should().Be(1);
        (await ReadBookingCount(staffClient, tourOperator.Id)).GetInt32().Should().Be(1);
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
        object? payload = path.EndsWith("/approve", StringComparison.Ordinal)
            ? new
            {
                checklist = new
                {
                    contentCompleteness = true,
                    imageAppropriateness = true,
                    pricePlausibility = true,
                    policyCompliance = true,
                    itineraryFeasibility = true
                }
            }
            : null;
        using var staffResponse = post
            ? payload is not null ? await staff.PostAsJsonAsync(path, payload) : await staff.PostAsync(path, null)
            : await staff.GetAsync(path);
        using var adminResponse = post
            ? payload is not null ? await admin.PostAsJsonAsync(path, payload) : await admin.PostAsync(path, null)
            : await admin.GetAsync(path);
        staffResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        if (path.EndsWith("/approve", StringComparison.Ordinal))
        {
            var forbidden = await adminResponse.Content.ReadFromJsonAsync<JsonElement>();
            forbidden.GetProperty("errorCode").GetString().Should().Be("operator_tour.admin_access_required");
        }
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
        forbidden.Headers.CacheControl!.NoStore.Should().BeTrue();
        missing.Headers.CacheControl!.NoStore.Should().BeTrue();
        var left = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync()).RootElement;
        var right = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        left.GetProperty("errorCode").GetString().Should().Be("account_management.not_found");
        left.EnumerateObject().Where(property => property.Name != "traceId")
            .Select(property => (property.Name, property.Value.ToString())).Should().BeEquivalentTo(
                right.EnumerateObject().Where(property => property.Name != "traceId")
                    .Select(property => (property.Name, property.Value.ToString())));
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
    public async Task List_UsesVietnamCalendarDayAndRejectsInvertedRanges()
    {
        await using var factory = await SeedAsync();
        await factory.WithDbContextAsync(async db =>
        {
            var start = DateTimeOffset.Parse("2026-11-16T00:00:00+07:00", CultureInfo.InvariantCulture);
            (await db.Users.SingleAsync(user => user.Id == 30)).CreatedAtUtc = start;
            (await db.Users.SingleAsync(user => user.Id == 40)).CreatedAtUtc = start.AddDays(1).AddTicks(-1);
            db.Users.Add(new User
            {
                Id = 50,
                FullName = "Next day",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = start.AddDays(1)
            });
            await db.SaveChangesAsync();
            return true;
        });
        using var staff = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var range = await staff.GetAsync("/api/v1/admin/users?registeredFrom=2026-11-16T00:00:00%2B07:00&registeredTo=2026-11-16T23:59:59.9999999%2B07:00");
        range.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = await range.Content.ReadFromJsonAsync<JsonElement>();
        data.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("userId").GetString())
            .Should().Equal("40", "30");
        using var invalid = await staff.GetAsync("/api/v1/admin/users?registeredFrom=2026-11-17T00:00:00%2B07:00&registeredTo=2026-11-16T23:59:59.9999999%2B07:00");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errorCode").GetString().Should().Be("account_management.invalid_date_range");
    }

    [Fact]
    public async Task Detail_CountsBookingRowsAcrossStatusesAndUsesNullableCountsByRole()
    {
        await using var factory = await SeedAsync();
        await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Users.AddRange(
                new User { Id = 50, FullName = "Provider", Role = UserRole.ServiceProvider, Status = AccountStatus.Active },
                new User { Id = 60, FullName = "Other traveler", Role = UserRole.Traveler, Status = AccountStatus.Active },
                new User { Id = 70, FullName = "Empty traveler", Role = UserRole.Traveler, Status = AccountStatus.Active },
                new User { Id = 80, FullName = "Other operator", Role = UserRole.TourOperator, Status = AccountStatus.Active });

            async Task<TourSchedule> AddSchedule(long operatorId)
            {
                var profile = new OperatorProfile { UserId = operatorId, CompanyName = $"Account metric operator {operatorId}" };
                db.OperatorProfiles.Add(profile);
                var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
                Set(tour, nameof(Tour.OperatorUserId), operatorId);
                Set(tour, nameof(Tour.OperatorProfile), profile);
                Set(tour, nameof(Tour.Title), $"Account metric tour {operatorId}");
                Set(tour, nameof(Tour.DurationDays), 1);
                Set(tour, nameof(Tour.BasePrice), 500_000m);
                db.Tours.Add(tour);
                await db.SaveChangesAsync();
                var schedule = TourSchedule.Create(now.AddDays(4), now.AddDays(4).AddHours(8), "Da Nang", 20, now);
                Set(schedule, nameof(TourSchedule.TourId), tour.Id);
                Set(schedule, nameof(TourSchedule.Tour), tour);
                db.TourSchedules.Add(schedule);
                await db.SaveChangesAsync();
                return schedule;
            }

            var schedule = await AddSchedule(40);
            var otherSchedule = await AddSchedule(80);
            var statuses = new[] { BookingStatus.PendingPayment, BookingStatus.Confirmed, BookingStatus.Cancelled,
                BookingStatus.Completed, BookingStatus.Expired, BookingStatus.Interrupted };
            var bookings = statuses.Select(status =>
            {
                var payment = status == BookingStatus.Cancelled ? PaymentStatus.Refunded : PaymentStatus.Paid;
                var booking = Booking.Create(30, schedule.Id, 1, 500_000m, status, payment);
                Set(booking, nameof(Booking.BookingCode), $"BK-TM93-{(int)status}");
                return booking;
            }).ToArray();
            var sameOperator = Booking.Create(60, schedule.Id, 1, 500_000m, BookingStatus.Confirmed, PaymentStatus.Paid);
            Set(sameOperator, nameof(Booking.BookingCode), "BK-TM93-OTHER-TRAVELER");
            var unrelated = Booking.Create(60, otherSchedule.Id, 1, 500_000m, BookingStatus.Confirmed, PaymentStatus.Paid);
            Set(unrelated, nameof(Booking.BookingCode), "BK-TM93-OTHER-OPERATOR");
            db.Bookings.AddRange(bookings);
            db.Bookings.AddRange(sameOperator, unrelated);
            await db.SaveChangesAsync();
            var refunded = bookings.Single(booking => booking.Status == BookingStatus.Cancelled);
            db.Refunds.AddRange(
                Refund.CreatePending(refunded.Id, 101, 201, 100_000m, "First refund", RefundInitiator.System, now),
                Refund.CreatePending(refunded.Id, 102, 202, 100_000m, "Second refund", RefundInitiator.System, now));
            await db.SaveChangesAsync();
            return true;
        });
        using var staff = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var administrator = factory.CreateAuthenticatedClient(20, UserRole.Administrator);
        (await ReadBookingCount(staff, 30)).GetInt32().Should().Be(6);
        (await ReadBookingCount(staff, 40)).GetInt32().Should().Be(7);
        (await ReadBookingCount(staff, 80)).GetInt32().Should().Be(1);
        (await ReadBookingCount(staff, 70)).GetInt32().Should().Be(0);
        (await ReadBookingCount(staff, 50)).ValueKind.Should().Be(JsonValueKind.Null);
        (await ReadBookingCount(administrator, 10)).ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Detail_ProjectsOnlySuccessfulAccountHistoryWithStableOrderAndAttribution()
    {
        await using var factory = await SeedAsync();
        await factory.WithDbContextAsync(async db =>
        {
            var time = DateTimeOffset.Parse("2026-11-16T10:00:00Z", CultureInfo.InvariantCulture);
            db.AuditLogs.AddRange(
                AuditLog.CreateRecordedOutcome(10, AuditActionTypes.UserAccountLocked, AuditEntityTypes.User, 40,
                    time, AuditOutcome.Success, reason: "Policy"),
                AuditLog.CreateRecordedOutcome(10, AuditActionTypes.UserAccountUnlocked, AuditEntityTypes.User, 40,
                    time, AuditOutcome.Success),
                AuditLog.CreateOperatorApplicationApproved(10, 40, "{}", "{}", time),
                AuditLog.CreateOperatorApplicationRejected(10, 40, """{"Role":"Staff"}""",
                    """{"RejectionReason":"License expired","PrivateValue":"do-not-project"}""", time),
                AuditLog.CreateOperatorApplicationResubmitted(40, "{}", "{}", time),
                AuditLog.CreateRecordedOutcome(10, AuditActionTypes.TourOperatorUnlocked, AuditEntityTypes.User, 40,
                    time, AuditOutcome.Success),
                AuditLog.CreateRecordedOutcome(null, AuditActionTypes.TourOperatorAutoLocked, AuditEntityTypes.User, 40,
                    time, AuditOutcome.Success),
                AuditLog.CreateRecordedOutcome(10, AuditActionTypes.UserAccountLocked, AuditEntityTypes.User, 40,
                    time, AuditOutcome.Failure),
                AuditLog.CreateRecordedOutcome(10, AuditActionTypes.UserAccountLocked, AuditEntityTypes.OperatorProfile, 40,
                    time, AuditOutcome.Success),
                AuditLog.CreateRecordedOutcome(10, AuditActionTypes.UserAccountLocked, AuditEntityTypes.User, 30,
                    time, AuditOutcome.Success),
                AuditLog.CreateRecordedOutcome(10, "POI_DEACTIVATED", AuditEntityTypes.User, 40,
                    time, AuditOutcome.Success));
            var traveler = await db.Users.SingleAsync(user => user.Id == 30);
            db.RefreshTokens.Add(new RefreshToken
            {
                User = traveler,
                TokenHash = "tm93-private",
                ExpiresAtUtc = time.AddDays(1)
            });
            await db.SaveChangesAsync();
            return true;
        });
        using var staff = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var directory = await staff.GetAsync("/api/v1/admin/users");
        directory.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detail = await staff.GetAsync("/api/v1/admin/users/40");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await detail.Content.ReadFromJsonAsync<JsonElement>();
        var history = payload.GetProperty("moderationHistory").EnumerateArray().ToArray();
        history.Select(row => row.GetProperty("action").GetString()).Should().Equal(
            AuditActionTypes.TourOperatorAutoLocked, AuditActionTypes.TourOperatorUnlocked,
            AuditActionTypes.OperatorApplicationResubmit, AuditActionTypes.OperatorApplicationReject,
            AuditActionTypes.OperatorApplicationApprove, AuditActionTypes.UserAccountUnlocked, AuditActionTypes.UserAccountLocked);
        history[0].GetProperty("actorAttribution").GetString().Should().Be("System");
        history[^1].GetProperty("actorAttribution").GetString().Should().Be("Account 10");
        history[^1].GetProperty("reason").GetString().Should().Be("Policy");
        history[3].GetProperty("reason").GetString().Should().Be("License expired");
        history[0].GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        history.Select(row => row.GetProperty("atUtc").GetDateTime()).Should()
            .OnlyContain(value => value == new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc));
        payload.ToString().Should().NotContain("beforeData").And.NotContain("afterData").And.NotContain("tm93-private")
            .And.NotContain("PrivateValue").And.NotContain("do-not-project");
        await factory.WithDbContextAsync(async db =>
        {
            (await db.AuditLogs.CountAsync()).Should().Be(11);
            (await db.Users.SingleAsync(user => user.Id == 40)).Status.Should().Be(AccountStatus.Active);
            (await db.RefreshTokens.SingleAsync()).RevokedAtUtc.Should().BeNull();
            return true;
        });
    }

    [Fact]
    public async Task Detail_IncludesIdempotentUnlockAndProviderAutoLockHistory()
    {
        await using var factory = await SeedAsync();
        await factory.WithDbContextAsync(async db =>
        {
            var time = DateTimeOffset.Parse("2026-11-16T10:00:00Z", CultureInfo.InvariantCulture);
            db.Users.Add(new User { Id = 50, FullName = "Provider", Role = UserRole.ServiceProvider, Status = AccountStatus.Active });
            await db.SaveChangesAsync();
            db.AuditLogs.AddRange(
                // Old commission jobs stored the reason inside the audit snapshot.
                new AuditLog(null, AuditActionTypes.ServiceProviderAutoLocked, AuditEntityTypes.User, 50, time,
                    afterData: "Status=Locked; Reason=Overdue commission statement SP-001 for period 2026-10"),
                AuditLog.CreateRecordedOutcome(10, AuditActionTypes.UserUnlock, AuditEntityTypes.User, 50,
                    time, AuditOutcome.Success, reason: "Settlement verified"),
                AuditLog.CreateRecordedOutcome(10, AuditActionTypes.UserUnlock, AuditEntityTypes.User, 50,
                    time, AuditOutcome.Failure),
                AuditLog.CreateRecordedOutcome(10, AuditActionTypes.UserUnlock, AuditEntityTypes.User, 30,
                    time, AuditOutcome.Success, reason: "Verified owner"),
                new AuditLog(null, AuditActionTypes.ServiceProviderAutoLocked, AuditEntityTypes.User, 30, time));
            await db.SaveChangesAsync();
            return true;
        });
        using var staff = factory.CreateAuthenticatedClient(10, UserRole.Staff);

        using var provider = await staff.GetAsync("/api/v1/admin/users/50");
        using var traveler = await staff.GetAsync("/api/v1/admin/users/30");

        provider.StatusCode.Should().Be(HttpStatusCode.OK);
        var providerHistory = (await provider.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("moderationHistory").EnumerateArray().ToArray();
        providerHistory.Select(row => row.GetProperty("action").GetString()).Should().Equal(
            AuditActionTypes.UserUnlock, AuditActionTypes.ServiceProviderAutoLocked);
        providerHistory[0].GetProperty("actorAttribution").GetString().Should().Be("Account 10");
        providerHistory[0].GetProperty("reason").GetString().Should().Be("Settlement verified");
        providerHistory[1].GetProperty("actorAttribution").GetString().Should().Be("System");
        providerHistory[1].GetProperty("reason").GetString()
            .Should().Be("Overdue commission statement SP-001 for period 2026-10");

        var travelerHistory = (await traveler.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("moderationHistory").EnumerateArray().ToArray();
        travelerHistory.Select(row => row.GetProperty("action").GetString()).Should().Equal(AuditActionTypes.UserUnlock);
    }

    [Fact]
    public async Task Directory_AuditsAnonymousAndForbiddenExactlyOnce()
    {
        await using var factory = await SeedAsync();
        using var anonymous = factory.CreateClient();
        using var traveler = factory.CreateAuthenticatedClient(30, UserRole.Traveler);
        using var challenged = await anonymous.GetAsync("/api/v1/admin/users");
        using var forbidden = await traveler.GetAsync("/api/v1/admin/users");
        challenged.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await forbidden.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errorCode").GetString().Should().Be("account_management.forbidden");
        var denials = await factory.WithDbContextAsync(db => db.AuditLogs
            .Where(row => row.ActionType == AuditActionTypes.AdministrationAccessDenied)
            .OrderBy(row => row.Id).ToListAsync());
        denials.Select(row => row.ActorUserId).Should().Equal(null, 30L);
        denials.Should().OnlyContain(row => row.AffectedEntityId == null && row.Result == AuditOutcome.Failure);
    }

    [Fact]
    public async Task ReadDenials_PreserveStatusWhenAuditPersistenceFails()
    {
        var interceptor = new RejectAuditWrites();
        await using var factory = await SeedAsync(saveChangesInterceptor: interceptor);
        using var anonymous = factory.CreateClient();
        using var traveler = factory.CreateAuthenticatedClient(30, UserRole.Traveler);
        using var staff = factory.CreateAuthenticatedClient(10, UserRole.Staff);
        using var challenged = await anonymous.GetAsync("/api/v1/admin/users");
        using var forbidden = await traveler.GetAsync("/api/v1/admin/users");
        using var concealed = await staff.GetAsync("/api/v1/admin/users/20");
        using var missing = await staff.GetAsync("/api/v1/admin/users/9999");
        challenged.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        concealed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        interceptor.Attempts.Should().Be(4);
        (await factory.WithDbContextAsync(db => db.AuditLogs.CountAsync())).Should().Be(0);
    }

    private sealed class RejectAuditWrites : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditLog>().Any(entry => entry.State == EntityState.Added))
            {
                Attempts++;
                throw new InvalidOperationException("Injected audit persistence failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static async Task<JsonElement> ReadBookingCount(HttpClient client, long userId)
    {
        using var response = await client.GetAsync($"/api/v1/admin/users/{userId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("bookingCount");
    }

    private static void Set<T>(object instance, string name, T value) =>
        instance.GetType().GetProperty(name)!.SetValue(instance, value);

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
            var queued = await db.Notifications.SingleAsync(row => row.UserId == 30 && row.Channel == NotificationChannel.Email);
            queued.Type.Should().Be("AccountLocked");
            return true;
        });
        using var unlocked = await client.PostAsJsonAsync("/api/v1/admin/users/30/unlock", new { reason = "Resolved" });
        unlocked.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == 30)).Status.Should().Be(AccountStatus.PendingEmailVerification);
            (await db.AuditLogs.CountAsync(row => row.AffectedEntityId == 30 && row.Result == AuditOutcome.Success)).Should().Be(2);
            (await db.Notifications.CountAsync(row => row.UserId == 30 && row.Channel == NotificationChannel.Email
                && row.Type == "AccountUnlocked")).Should().Be(1);
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

    private static async Task<TripMateApiFactory> SeedAsync(ApiTestAuthenticationMode mode = ApiTestAuthenticationMode.HeaderStub,
        SaveChangesInterceptor? saveChangesInterceptor = null)
    {
        var factory = new TripMateApiFactory(mode, saveChangesInterceptor: saveChangesInterceptor);
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
