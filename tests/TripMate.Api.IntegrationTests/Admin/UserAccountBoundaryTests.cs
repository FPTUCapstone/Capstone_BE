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
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

[Collection(nameof(TripMateApiFactory))]
public sealed class UserAccountBoundaryTests
{
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
        User Account(string name, UserRole role, DateTimeOffset createdAt) => new()
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
        db.OperatorProfiles.Add(new OperatorProfile { UserId = tourOperator.Id, CompanyName = "TM93 operator",
            TaxCode = "0012345678", BusinessLicenseNo = "TM93-BIZ", ApprovalStatus = OperatorApprovalStatus.Approved });
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
            db.Users.Add(new User { Id = 50, FullName = "Next day", Role = UserRole.Traveler,
                Status = AccountStatus.Active, CreatedAtUtc = start.AddDays(1) });
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
            db.RefreshTokens.Add(new RefreshToken { User = traveler, TokenHash = "tm93-private",
                ExpiresAtUtc = time.AddDays(1) });
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
