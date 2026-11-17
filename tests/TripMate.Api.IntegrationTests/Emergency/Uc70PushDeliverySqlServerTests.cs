using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services;
using TripMate.Infrastructure.TripAlerts;

namespace TripMate.Api.IntegrationTests.Emergency;

public sealed class Uc70PushDeliverySqlServerTests
{
    private const string OperatorPassword = "Uc70-push-fixture-value";
    private const string TourTitle = "Secret Hue Heritage Tour";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] ForbiddenText =
        [TourTitle, "Hue", "Typhoon", "0900000000", "refund", "Refund", "UC70 Traveler"];

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CancelBeforeDeparture_QueuesOneGenericPushPerBookingForItsOwner()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        await CommitCancelAsync(database, seed);

        var claims = await ClaimAllAsync(database);

        claims.Should().HaveCount(3);
        claims.Should().OnlyContain(claim => claim.Title == Notification.TripUpdateTitle);
        claims.Should().OnlyContain(claim => claim.Body == Notification.TripUpdateBody);
        claims.Select(claim => claim.Data["bookingId"]).Should().BeEquivalentTo(
            [$"{seed.FirstBookingId}", $"{seed.SecondBookingId}", $"{seed.OtherOwnerBookingId}"]);
        foreach (var claim in claims)
        {
            claim.Data.Keys.Should().BeEquivalentTo(
                ["type", "bookingId", "scheduleId", "operationId", "action", "committedAtUtc"]);
            claim.Data["type"].Should().Be("uc70_booking_emergency");
            claim.Data["action"].Should().Be("CancelBeforeDeparture");
            claim.Data["scheduleId"].Should().Be($"{seed.ScheduleId}");
            AssertPrivate(claim);
        }

        var otherOwnerClaim = claims.Single(
            claim => claim.Data["bookingId"] == $"{seed.OtherOwnerBookingId}");
        claims.Where(claim => claim != otherOwnerClaim)
            .SelectMany(claim => claim.ProtectedDeviceTokens)
            .Should().OnlyContain(token => token == seed.TravelerToken);
        otherOwnerClaim.ProtectedDeviceTokens.Should().ContainSingle(seed.OtherTravelerToken);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ReplayingTheCommand_DoesNotQueueAnotherPush()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        var commandId = Guid.NewGuid();

        await CommitCancelAsync(database, seed, commandId);
        var before = await WorkCountAsync(database);
        await CommitCancelAsync(database, seed, commandId, expectedVersion: "replay");

        (await WorkCountAsync(database)).Should().Be(before);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task EmergencyStop_QueuesTheStopAction()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddHours(-1));
        await using (var db = database.CreateDbContext())
        {
            var version = await ImpactVersionAsync(db, seed);
            var result = await Handler(db, seed.OperatorId).Handle(
                new EmergencyStopActiveCommand(
                    seed.TourId, seed.ScheduleId, Guid.NewGuid(), version, OperatorPassword,
                    EmergencyTourReason.SafetyIncident, "Road closed", "Stay at the shelter.",
                    "0900000000"),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        var claims = await ClaimAllAsync(database);

        claims.Should().HaveCount(3);
        claims.Should().OnlyContain(claim => claim.Data["action"] == "EmergencyStop");
        claims.ForEach(AssertPrivate);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RejectedCommand_RollsBackWithNoPush()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        await using (var db = database.CreateDbContext())
        {
            var result = await Handler(db, seed.OperatorId).Handle(
                new CancelBeforeDepartureCommand(
                    seed.TourId, seed.ScheduleId, Guid.NewGuid(), new string('f', 64),
                    EmergencyTourReason.WeatherEmergency, "Stale", "0900000000"),
                CancellationToken.None);
            result.IsSuccess.Should().BeFalse();
        }

        (await WorkCountAsync(database)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ReviewNotices_CarryMinimalIdsAndStopOnceAcknowledged()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        long reviewId = await AddReviewAsync(database, seed);

        await using (var queueContext = database.CreateDbContext())
        {
            var queue = Queue(queueContext);
            var claim = await queue.ClaimNextAsync(CancellationToken.None);

            claim.Should().NotBeNull();
            claim!.Title.Should().Be(Notification.SafetyAlertTitle);
            claim.Body.Should().Be(Notification.SafetyAlertBody);
            claim.Data.Keys.Should().BeEquivalentTo(
                ["type", "reviewId", "tourId", "scheduleId", "changeType", "issuedAtUtc"]);
            claim.Data["type"].Should().Be("uc70_weather_safety_review");
            claim.Data["reviewId"].Should().Be($"{reviewId}");
            claim.Data["changeType"].Should().Be("Created");
            claim.ProtectedDeviceTokens.Should().ContainSingle(seed.OperatorToken);
            AssertPrivate(claim);
            await queue.CompleteAsync(claim, new TripAlertPushResult(false, []), CancellationToken.None);
        }

        await using (var ackContext = database.CreateDbContext())
        {
            var review = await ackContext.WeatherSafetyReviews.SingleAsync();
            review.Acknowledge(seed.OperatorId, Now, null).Should().BeTrue();
            await ackContext.SaveChangesAsync();
        }

        await using var afterAck = database.CreateDbContext();
        var later = new Clock { UtcNow = Now.AddHours(2) };
        var laterQueue = new AlertDeliveryQueue(afterAck, later, Options.Create(new TripAlertOptions()));
        (await laterQueue.ClaimNextAsync(CancellationToken.None)).Should().BeNull();
        var work = await afterAck.AlertDeliveryWork.AsNoTracking().SingleAsync();
        work.NextAttemptAtUtc.Should().BeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task EscalationSendsOneNoticeAndRepeatedSweepsAddNoMore()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        await using (var db = database.CreateDbContext())
        {
            db.WeatherSafetyReviews.Add(WeatherSafetyReview.CreateHold(
                seed.ScheduleId, "Fixture", "evt-escalate", Now.AddHours(-2), Now.AddHours(-1)));
            await db.SaveChangesAsync();
        }

        await using var provider = new ServiceCollection()
            .AddScoped<IApplicationDbContext>(_ => database.CreateDbContext())
            .AddSingleton<IDateTimeProvider>(new Clock { UtcNow = Now })
            .AddSingleton<IWeatherSafetyReviewNotifier>(new NullNotifier())
            .BuildServiceProvider();
        var service = new WeatherSafetyReviewEscalationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WeatherSafetyReviewEscalationBackgroundService>.Instance);
        (await service.ProcessEscalationsAsync(CancellationToken.None)).Should().Be(1);
        (await service.ProcessEscalationsAsync(CancellationToken.None)).Should().Be(0);

        var claims = await ClaimAllAsync(database);
        claims.Should().ContainSingle();
        claims[0].Data["changeType"].Should().Be("Escalated");
        AssertPrivate(claims[0]);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MissedSla_AfterTheDepartureWasCancelled_SendsNoEscalation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        await using (var db = database.CreateDbContext())
        {
            db.WeatherSafetyReviews.Add(WeatherSafetyReview.CreateHold(
                seed.ScheduleId, "Fixture", "evt-decided", Now.AddHours(-2), Now.AddHours(-1)));
            var schedule = await db.TourSchedules.SingleAsync(item => item.Id == seed.ScheduleId);
            schedule.CancelBeforeDeparture(Now).Should().BeTrue();
            await db.SaveChangesAsync();
        }

        await using var provider = new ServiceCollection()
            .AddScoped<IApplicationDbContext>(_ => database.CreateDbContext())
            .AddSingleton<IDateTimeProvider>(new Clock { UtcNow = Now })
            .AddSingleton<IWeatherSafetyReviewNotifier>(new NullNotifier())
            .BuildServiceProvider();
        var service = new WeatherSafetyReviewEscalationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WeatherSafetyReviewEscalationBackgroundService>.Instance);

        (await service.ProcessEscalationsAsync(CancellationToken.None)).Should().Be(0);
        (await ClaimAllAsync(database)).Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UndeliverableNotice_IsStoppedAndDoesNotBlockTheQueue()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        await using (var db = database.CreateDbContext())
        {
            // A booking notice with no decision behind it can never build its payload.
            EmergencyPushNotices.AddBookingNotice(
                db, seed.TravelerId, seed.FirstBookingId,
                Notification.EmergencyDepartureCancelledType, Now.AddMinutes(-5));
            await db.SaveChangesAsync();
        }

        long reviewId = await AddReviewAsync(database, seed);

        var claims = await ClaimAllAsync(database);

        claims.Should().ContainSingle();
        claims[0].Data["reviewId"].Should().Be($"{reviewId}");
        await using var verify = database.CreateDbContext();
        (await verify.AlertDeliveryWork.AsNoTracking().ToListAsync())
            .Should().OnlyContain(work => work.ClaimToken == null);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task EscalationCannotOverwriteAnAcknowledgementItRaced()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        await AddReviewAsync(database, seed);

        await using var escalating = database.CreateDbContext();
        var stale = await escalating.WeatherSafetyReviews.SingleAsync();
        await using (var acknowledging = database.CreateDbContext())
        {
            var review = await acknowledging.WeatherSafetyReviews.SingleAsync();
            review.Acknowledge(seed.OperatorId, Now, null).Should().BeTrue();
            await acknowledging.SaveChangesAsync();
        }

        stale.Escalate(Now, "Desk").Should().BeTrue();
        var save = () => escalating.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using var verify = database.CreateDbContext();
        (await verify.WeatherSafetyReviews.AsNoTracking().SingleAsync())
            .Status.Should().Be(WeatherSafetyReview.StatusAcknowledged);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task NoToken_StopsWithoutCountingAnAttempt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4), withTokens: false);
        await CommitCancelAsync(database, seed);

        (await ClaimAllAsync(database)).Should().BeEmpty();

        await using var db = database.CreateDbContext();
        var work = await db.AlertDeliveryWork.AsNoTracking().ToListAsync();
        work.Should().OnlyContain(item => item.AttemptCount == 0 && item.NextAttemptAtUtc == null);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RetryableFailure_IsRetriedAfterBackoffWithoutADuplicateNotification()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        long reviewId = await AddReviewAsync(database, seed);

        await using var db = database.CreateDbContext();
        var clock = new Clock { UtcNow = Now };
        var queue = new AlertDeliveryQueue(db, clock, Options.Create(new TripAlertOptions()));
        var first = await queue.ClaimNextAsync(CancellationToken.None);
        first!.AttemptCount.Should().Be(1);
        await queue.CompleteAsync(first, new TripAlertPushResult(false, []), CancellationToken.None);

        (await queue.ClaimNextAsync(CancellationToken.None)).Should().BeNull();

        clock.UtcNow = Now.AddMinutes(16);
        var retry = await queue.ClaimNextAsync(CancellationToken.None);
        retry!.NotificationId.Should().Be(first.NotificationId);
        retry.AttemptCount.Should().Be(2);
        retry.Data["reviewId"].Should().Be($"{reviewId}");
        (await db.Notifications.AsNoTracking().CountAsync(notification =>
            notification.Type == Notification.WeatherSafetyReviewCreatedType)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task InvalidToken_IsDeactivatedAndNotUsedAgain()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        await AddReviewAsync(database, seed);

        await using var db = database.CreateDbContext();
        var queue = Queue(db);
        var claim = await queue.ClaimNextAsync(CancellationToken.None);
        await queue.CompleteAsync(
            claim!, new TripAlertPushResult(false, [seed.OperatorToken]), CancellationToken.None);

        (await db.PushDeviceTokens.AsNoTracking()
            .SingleAsync(token => token.UserId == seed.OperatorId))
            .DeactivatedAtUtc.Should().NotBeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AccountSwitch_OldOwnerNoLongerReceivesPush()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(4));
        await using (var db = database.CreateDbContext())
        {
            var other = await db.Users.SingleAsync(user => user.Id == seed.OtherTravelerId);
            var device = await db.PushDeviceTokens.SingleAsync(token => token.UserId == seed.OperatorId);
            device.Reassign(other, "protected-switched", new string('c', 64), Now);
            await db.SaveChangesAsync();
        }

        await AddReviewAsync(database, seed);

        (await ClaimAllAsync(database)).Should().BeEmpty();
    }

    private static void AssertPrivate(ClaimedTripAlert claim)
    {
        var text = $"{claim.Title} {claim.Body} {string.Join(' ', claim.Data.Values)}";
        foreach (var forbidden in ForbiddenText)
        {
            text.Should().NotContain(forbidden);
        }
    }

    private static AlertDeliveryQueue Queue(ApplicationDbContext db) =>
        new(db, new Clock { UtcNow = Now }, Options.Create(new TripAlertOptions()));

    private static async Task<List<ClaimedTripAlert>> ClaimAllAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var queue = Queue(db);
        var claims = new List<ClaimedTripAlert>();
        while (await queue.ClaimNextAsync(CancellationToken.None) is { } claim)
        {
            claims.Add(claim);
            await queue.CompleteAsync(claim, new TripAlertPushResult(true, []), CancellationToken.None);
        }

        return claims;
    }

    private static async Task<int> WorkCountAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateDbContext();
        return await db.AlertDeliveryWork.CountAsync();
    }

    private static async Task<long> AddReviewAsync(SqlServerTestDatabase database, Seed seed)
    {
        await using var db = database.CreateDbContext();
        var review = WeatherSafetyReview.CreateHold(
            seed.ScheduleId, "Fixture", $"evt-{Guid.NewGuid():N}", Now, Now.AddMinutes(30));
        db.WeatherSafetyReviews.Add(review);
        await db.SaveChangesAsync();
        EmergencyPushNotices.AddReviewNotice(
            db, seed.OperatorId, review.Id, Notification.WeatherSafetyReviewCreatedType, Now);
        await db.SaveChangesAsync();
        return review.Id;
    }

    private static async Task<string> ImpactVersionAsync(ApplicationDbContext db, Seed seed)
    {
        var current = new CurrentUser(seed.OperatorId);
        var response = await new GetEmergencyTourImpactQueryHandler(db, current, new Clock { UtcNow = Now })
            .Handle(new GetEmergencyTourImpactQuery(seed.TourId, seed.ScheduleId), CancellationToken.None);
        return response.Value!.ImpactVersion;
    }

    private static async Task CommitCancelAsync(
        SqlServerTestDatabase database,
        Seed seed,
        Guid? commandId = null,
        string? expectedVersion = null)
    {
        await using var db = database.CreateDbContext();
        var id = commandId ?? Guid.NewGuid();
        var version = expectedVersion == "replay" ? Seed.LastVersion! : await ImpactVersionAsync(db, seed);
        Seed.LastVersion = version;
        var result = await Handler(db, seed.OperatorId).Handle(
            new CancelBeforeDepartureCommand(
                seed.TourId, seed.ScheduleId, id, version,
                EmergencyTourReason.WeatherEmergency, "Typhoon warning", "0900000000"),
            CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
    }

    private static EmergencyTourCommandHandler Handler(ApplicationDbContext db, long operatorId) =>
        new(db, new CurrentUser(operatorId), new Clock { UtcNow = Now }, new PasswordHasherService(), new NullNotifier());

    private static async Task<Seed> SeedAsync(
        SqlServerTestDatabase database,
        DateTimeOffset start,
        bool withTokens = true)
    {
        await using var db = database.CreateDbContext();
        var hasher = new PasswordHasherService();
        User NewUser(string name, UserRole role) => new()
        {
            Email = $"uc70-push-{name}-{Guid.NewGuid():N}@test.local",
            FullName = $"UC70 {name}",
            Role = role,
            Status = AccountStatus.Active,
            PasswordHash = hasher.Hash(OperatorPassword),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var operatorUser = NewUser("Operator", UserRole.TourOperator);
        var traveler = NewUser("Traveler", UserRole.Traveler);
        var otherTraveler = NewUser("Other", UserRole.Traveler);
        db.Users.AddRange(operatorUser, traveler, otherTraveler);
        await db.SaveChangesAsync();
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            User = operatorUser,
            CompanyName = "UC70 Tours",
            TaxCode = $"UC70-{operatorUser.Id}",
            BusinessLicenseNo = $"UC70-L-{operatorUser.Id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUser.Id);
        Set(tour, nameof(Tour.Title), TourTitle);
        Set(tour, nameof(Tour.BasePrice), 100_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        var schedule = TourSchedule.Create(
            start, start.AddHours(8), "Hue", 20, Now.AddDays(-5), adultPrice: 100_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        schedule.ReserveCapacity(3);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();

        Booking NewBooking(long ownerId, string code)
        {
            var booking = Booking.Create(
                ownerId, schedule.Id, 1, 100_000m, BookingStatus.Confirmed, PaymentStatus.Unpaid);
            Set(booking, nameof(Booking.BookingCode), code);
            Set(booking, nameof(Booking.TourSchedule), schedule);
            return booking;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var first = NewBooking(traveler.Id, $"U{suffix}A");
        var second = NewBooking(traveler.Id, $"U{suffix}B");
        var third = NewBooking(otherTraveler.Id, $"U{suffix}C");
        db.Bookings.AddRange(first, second, third);

        string operatorToken = "protected-operator", travelerToken = "protected-traveler",
            otherToken = "protected-other";
        if (withTokens)
        {
            db.PushDeviceTokens.AddRange(
                PushDeviceToken.Register(operatorUser, Guid.NewGuid(), operatorToken, new string('a', 64), Now),
                PushDeviceToken.Register(traveler, Guid.NewGuid(), travelerToken, new string('b', 64), Now),
                PushDeviceToken.Register(otherTraveler, Guid.NewGuid(), otherToken, new string('d', 64), Now));
        }

        await db.SaveChangesAsync();
        return new Seed(
            operatorUser.Id, traveler.Id, otherTraveler.Id, tour.Id, schedule.Id,
            first.Id, second.Id, third.Id, operatorToken, travelerToken, otherToken);
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(
            property,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed record Seed(
        long OperatorId,
        long TravelerId,
        long OtherTravelerId,
        long TourId,
        long ScheduleId,
        long FirstBookingId,
        long SecondBookingId,
        long OtherOwnerBookingId,
        string OperatorToken,
        string TravelerToken,
        string OtherTravelerToken)
    {
        public static string? LastVersion { get; set; }
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class CurrentUser(long userId) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Role => nameof(UserRole.TourOperator);
    }

    private sealed class NullNotifier : IWeatherSafetyReviewNotifier
    {
        public Task NotifyChangedAsync(
            long operatorUserId,
            WeatherSafetyReviewChanged notification,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}