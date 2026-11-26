using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Admin;
using TripMate.Api.IntegrationTests.Bookings;
using TripMate.Api.IntegrationTests.Emergency;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Api.IntegrationTests.ServiceProviderRequests;
using TripMate.Api.IntegrationTests.Tickets;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.CommercialServices.Booking;
using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

using Xunit.Abstractions;

namespace TripMate.Api.IntegrationTests.Notifications;

[Collection(nameof(TripMateApiFactory))]
public sealed class NotificationBrowserAcceptanceTests(ITestOutputHelper output)
{
    internal const string RunnerVariable = "TRIPMATE_NOTIFICATION_BROWSER_RUNNER";

    [NotificationBrowserFact]
    public async Task FiveRolesReceiveAndReadBusinessNotificationsInBrowser()
    {
        var localSql = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable(SqlServerTestDatabase.ConnectionStringEnvironmentVariable));
        var databaseHost = localSql.DataSource.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase)
            .Split(',', '\\')[0];
        new[] { "localhost", "127.0.0.1", "." }.Should().Contain(databaseHost.ToLowerInvariant(),
            "the acceptance harness must only use a local disposable SQL Server");

        await using var database = await SqlServerTestDatabase.CreateAsync();
        var approval = await ApproveTourSqlServerTests.SeedPendingTourAsync(database);
        var emergency = await EmergencyRefundHandoffSqlServerTests.SeedAsync(database);
        var service = await ServiceProviderRequestSqlServerTests.SeedAsync(database, includeBooking: false,
            categoryName: CommercialService.CategoryRestaurant);
        var now = DateTimeOffset.UtcNow;
        var ticket = await TicketSqlServerSeed.CreateAsync(database, now.AddDays(-6));
        var ticketBooking = await ticket.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var refundId = await ReportBookingRefundReturnSqlServerTests.SeedAwaitingPartnerReturnRefundAsync(
            database, ticketBooking, ticket.TravelerId, 500_000m, 350_000m);
        var accounts = new (long Id, string Type, string Entry, string? Destination)[]
        {
            (service.TravelerId, ProviderRequestEvents.NotificationType, "/", null),
            (approval.OperatorId, Notification.TourApprovedType, "/partner/tours", $"/partner/tours/{approval.TourId}/edit"),
            (service.ActorId, "MSG205", "/provider/bookings", null),
            (approval.StaffId1, Notification.RefundReturnReportedType, "/admin/staff", "/admin/refund-reconciliation"),
            (emergency.AdministratorId, Notification.EmergencyRefundReviewRequiredType, "/admin",
                $"/admin/refunds/escalated?emergencyScheduleId={emergency.ScheduleId}"),
        };
        var password = Guid.NewGuid().ToString("N") + "Aa1!";
        await using (var db = database.CreateDbContext())
        {
            var userIds = accounts.Select(account => account.Id).ToArray();
            var users = await db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync();
            var hash = new PasswordHasherService().Hash(password);
            foreach (var user in users)
            {
                user.PasswordHash = hash;
                user.EmailVerifiedAtUtc = now;
            }
            db.OperatorContractLegacyExemptions.Add(OperatorContractLegacyExemption.Create(approval.OperatorId, now, "Local browser acceptance"));
            await db.SaveChangesAsync();
        }

        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer,
            database.ConnectionString, environmentName: "Development");
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var local = factory.CreateDefaultClient();
        var origin = local.BaseAddress!.GetLeftPart(UriPartial.Authority);
        using var staff = await AsUserAsync(approval.StaffId1);
        using var provider = await AsUserAsync(service.ActorId);
        using var traveler = await AsUserAsync(service.TravelerId);
        var approvalResponse = await staff.PostAsJsonAsync($"/api/v1/admin/tours/{approval.TourId}/approve",
            new { checklist = ApproveTourSqlServerTests.ValidChecklist });
        await RequireSuccessAsync(approvalResponse);
        await using (var db = database.CreateDbContext())
        {
            var processor = new TourApprovalNoticeDeliveryProcessor(db, new Clock(now.AddMinutes(5)),
                NullLogger<TourApprovalNoticeDeliveryProcessor>.Instance);
            await processor.ProcessDueBatchAsync(default);

            var emergencyClock = new Clock(EmergencyRefundHandoffSqlServerTests.Now);
            var current = new CurrentOperator(emergency.OperatorId);
            var impact = await new GetEmergencyTourImpactQueryHandler(db, current, emergencyClock)
                .Handle(new GetEmergencyTourImpactQuery(emergency.TourId, emergency.ScheduleId), default);
            impact.IsSuccess.Should().BeTrue(impact.ErrorMessage ?? "");
            var action = await EmergencyRefundHandoffSqlServerTests.Handler(db, emergency.OperatorId).Handle(
                new CancelBeforeDepartureCommand(emergency.TourId, emergency.ScheduleId, Guid.NewGuid(),
                    impact.Value.ImpactVersion, EmergencyTourReason.WeatherEmergency, "Local emergency test", "0900000000"), default);
            action.IsSuccess.Should().BeTrue(action.ErrorMessage ?? "");
        }
        var refund = await ReportBookingRefundReturnSqlServerTests.HandleAsync(database, ticket.OperatorId,
            now, new ReportBookingRefundReturnCommand(refundId, "LOCAL-UAT-REFUND"));
        refund.IsSuccess.Should().BeTrue(refund.ErrorMessage ?? "");

        var startsAt = now.AddDays(2).ToString("yyyy-MM-ddTHH:mm:ssZ");
        await RequireSuccessAsync(await provider.PutAsJsonAsync(
            $"/api/v1/provider/commercial-services/{service.ServiceId}/availability/{startsAt}", new { quantity = 2 }));
        traveler.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var submitted = await traveler.PostAsJsonAsync("/api/v1/traveler/service-booking-requests", new
        {
            serviceId = service.ServiceId,
            startsAt,
            quantity = 1,
            contactName = "Browser UAT",
            contactPhone = "0901234567",
            contactEmail = "test@example.test"
        });
        await RequireSuccessAsync(submitted);
        var booking = (await submitted.Content.ReadFromJsonAsync<CommercialServiceSubmission>())!;
        var detailPath = $"/api/v1/provider/service-booking-requests/{booking.Id}";
        var details = (await provider.GetFromJsonAsync<ProviderRequestDetail>(detailPath))!;
        provider.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await RequireSuccessAsync(await provider.PostAsJsonAsync($"{detailPath}/decision",
            new { decision = ServiceBooking.Confirmed, details.RowVersion }));

        await using (var db = database.CreateDbContext())
        {
            var actors = new List<object>();
            var mobileActors = new List<object>();
            var expectedNoticeIds = new List<long>();
            var mobileNoticeIds = new List<long>();
            foreach (var account in accounts)
            {
                var actor = await db.Users.SingleAsync(item => item.Id == account.Id);
                var notice = await db.Notifications.AsNoTracking().SingleAsync(item => item.UserId == account.Id && item.Type == account.Type);
                expectedNoticeIds.Add(notice.Id);
                notice.Status.Should().NotBe(NotificationStatus.Read);
                actors.Add(new
                {
                    role = actor.Role.ToString(),
                    actor.Email,
                    account.Entry,
                    destination = actor.Role == UserRole.ServiceProvider ? $"/provider/bookings/{booking.Id}" : account.Destination,
                    noticeId = notice.Id,
                    notice.Title,
                    noticeType = notice.Type,
                });
                if (actor.Role is UserRole.Traveler or UserRole.TourOperator)
                {
                    mobileNoticeIds.Add(notice.Id);
                    mobileActors.Add(new { role = actor.Role.ToString(), token = IssueTestJwt(actor), noticeId = notice.Id, notice.Title });
                }
            }

            output.WriteLine(await RunBrowserAsync(new { origin, password, actors }));
            var saved = await db.Notifications.AsNoTracking().Where(item => expectedNoticeIds.Contains(item.Id)).ToListAsync();
            saved.Should().OnlyContain(item => item.Status == NotificationStatus.Read && item.ReadAtUtc.HasValue);
            if (Environment.GetEnvironmentVariable("TRIPMATE_NOTIFICATION_MOBILE_ROOT") is { Length: > 0 } mobileRoot)
            {
                // Replay the same delivered notices through the Mobile UI in the disposable database.
                await db.Notifications.Where(item => mobileNoticeIds.Contains(item.Id)).ExecuteUpdateAsync(setters =>
                    setters.SetProperty(item => item.Status, NotificationStatus.Pending)
                        .SetProperty(item => item.ReadAtUtc, (DateTimeOffset?)null));
                output.WriteLine(await RunMobileAsync(mobileRoot, new { origin, actors = mobileActors }));
                (await db.Notifications.AsNoTracking().Where(item => mobileNoticeIds.Contains(item.Id)).ToListAsync())
                    .Should().OnlyContain(item => item.Status == NotificationStatus.Read && item.ReadAtUtc.HasValue);
            }
        }

        string IssueTestJwt(User user)
        {
            using var scope = factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IJwtTokenService>().GenerateAccessToken(user).Token;
        }

        async Task<HttpClient> AsUserAsync(long id)
        {
            await using var context = database.CreateDbContext();
            var user = await context.Users.SingleAsync(item => item.Id == id);
            var jwt = IssueTestJwt(user);
            var client = new HttpClient { BaseAddress = new Uri(origin) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", jwt);
            return client;
        }
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode.Should().BeTrue("status {0}: {1}", response.StatusCode, await response.Content.ReadAsStringAsync());

    private static Task<string> RunBrowserAsync(object input)
    {
        var runner = Environment.GetEnvironmentVariable(RunnerVariable)!;
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(runner)!, "..")),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(runner);
        return RunProcessAsync(start, JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static Task<string> RunMobileAsync(string root, object input)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "flutter")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("flutter");
        }
        foreach (var arg in new[] { "test", "--no-pub", "test/integration/notification_live_widget_test.dart", "--reporter", "expanded" })
            start.ArgumentList.Add(arg);
        start.Environment["TRIPMATE_NOTIFICATION_MOBILE_FIXTURE"] =
            JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return RunProcessAsync(start);
    }

    private static async Task<string> RunProcessAsync(ProcessStartInfo start, string? input = null)
    {
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            process.ExitCode.Should().Be(0, "{0}{1}", stdout, await error);
            return stdout;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private sealed class Clock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class CurrentOperator(long id) : ICurrentUserService
    {
        public long? UserId => id;
        public string? Role => nameof(UserRole.TourOperator);
    }
}

internal sealed class NotificationBrowserFactAttribute : FactAttribute
{
    public NotificationBrowserFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NotificationBrowserAcceptanceTests.RunnerVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerTestDatabase.ConnectionStringEnvironmentVariable)))
            Skip = "Requires the local SQL Server connection and the Web Playwright runner path.";
    }
}
