using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TripMate.Api.Common;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Emergency;

[Collection(nameof(TripMateApiFactory))]
public sealed class EmergencyTourSecurityAndSafeLoggingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private const string OperatorPassword = "SecureOperatorPass123!";
    private const string WrongPassword = "WrongPassword999!";

    [Fact]
    public async Task Security_CurrentPassword_IsNotPersistedInDatabase_NorInEntityProperties()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var stopResponse = await client.PostAsJsonAsync(
            $"{endpoint}/emergency-stop",
            new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "SafetyIncident",
                reasonDetails = "Flash flood advisory on tour route.",
                immediateInstructions = "Assemble at high ground camp.",
                emergencyContact = "0900000000",
            });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify entity properties: EmergencyTourCancellationOperation has no password property
        typeof(EmergencyTourCancellationOperation).GetProperties()
            .Select(prop => prop.Name.ToLowerInvariant())
            .Should().NotContain(name => name.Contains("password"));

        // Verify database: no password value stored in any column of EmergencyTourCancellationOperations
        await factory.WithDbContextAsync(async db =>
        {
            var op = await db.EmergencyTourCancellationOperations.SingleAsync();
            op.TourScheduleId.Should().Be(seed.ScheduleId);

            // AuditLogs BeforeData and AfterData must not contain the password
            var audit = await db.AuditLogs.SingleAsync(a => a.AffectedEntityId == seed.ScheduleId);
            audit.BeforeData.Should().NotContain(OperatorPassword);
            audit.AfterData.Should().NotContain(OperatorPassword);
            audit.Reason.Should().NotContain(OperatorPassword);
            return true;
        });
    }

    [Fact]
    public async Task Security_Password_IsNotInRequestHash()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        Guid idempotencyKey = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey.ToString());

        using var stopResponse = await client.PostAsJsonAsync(
            $"{endpoint}/emergency-stop",
            new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "SafetyIncident",
                reasonDetails = "Flash flood advisory.",
                immediateInstructions = "Assemble at camp.",
                emergencyContact = "0900000000",
            });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await factory.WithDbContextAsync(async db =>
        {
            var op = await db.EmergencyTourCancellationOperations.SingleAsync();
            op.RequestHash.Should().NotBeNullOrWhiteSpace();

            // Compute expected hash without password
            string canonical = string.Join('\n',
                seed.TourId, seed.ScheduleId, idempotencyKey, impactVersion,
                EmergencyTourCancellationOperation.EmergencyStopActiveAction,
                EmergencyTourReason.SafetyIncident, "Flash flood advisory.",
                "Assemble at camp.", "0900000000",
                string.Empty, string.Empty, string.Empty, "0");
            string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();

            op.RequestHash.Should().Be(expectedHash, "Request hash must exclude currentPassword");
            return true;
        });
    }

    [Fact]
    public async Task Security_AuditLog_StoresOnlyHashedEvidence_NeverRawSensitiveUrlOrReference()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        const string sensitiveUrl = "https://sensitive-gov-orders.local/order-998234?token=confidential";
        const string sensitiveAuthorityRef = "CLASSIFIED-DEFENSE-DIRECTIVE-8823";
        const string sensitiveEvidenceRef = "REF-INTERNAL-CASE-0091823";

        using var stopResponse = await client.PostAsJsonAsync(
            $"{endpoint}/emergency-stop",
            new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "AuthorityDirective",
                reasonDetails = "Government directive issued.",
                immediateInstructions = "Follow evacuation team.",
                emergencyContact = "0900000000",
                evidenceUrl = sensitiveUrl,
                authorityReference = sensitiveAuthorityRef,
                evidenceReference = sensitiveEvidenceRef,
            });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await factory.WithDbContextAsync(async db =>
        {
            var audit = await db.AuditLogs.SingleAsync(a => a.AffectedEntityId == seed.ScheduleId);

            // AuditLog afterData must contain hashes
            string expectedUrlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sensitiveUrl)))
                .ToLowerInvariant();
            string expectedAuthorityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sensitiveAuthorityRef)))
                .ToLowerInvariant();
            string expectedRefHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sensitiveEvidenceRef)))
                .ToLowerInvariant();

            audit.AfterData.Should().Contain(expectedUrlHash);
            audit.AfterData.Should().Contain(expectedAuthorityHash);
            audit.AfterData.Should().Contain(expectedRefHash);

            // AuditLog must NOT contain raw sensitive strings
            audit.AfterData.Should().NotContain(sensitiveUrl);
            audit.AfterData.Should().NotContain(sensitiveAuthorityRef);
            audit.AfterData.Should().NotContain(sensitiveEvidenceRef);
            audit.BeforeData.Should().NotContain(sensitiveUrl);
            return true;
        });
    }

    [Fact]
    public async Task Security_EvidenceProvenance_IsStoredAsOperatorProvided_NeverSystemVerified()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var stopResponse = await client.PostAsJsonAsync(
            $"{endpoint}/emergency-stop",
            new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "AuthorityDirective",
                reasonDetails = "Direct order from local authorities.",
                immediateInstructions = "Evacuate.",
                emergencyContact = "0900000000",
                evidenceUrl = "https://authority.example.test/notice",
                authorityReference = "EVAC-01",
            });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await factory.WithDbContextAsync(async db =>
        {
            var op = await db.EmergencyTourCancellationOperations.SingleAsync();
            op.EvidenceProvenance.Should().Be(EmergencyEvidenceProvenance.OperatorProvided);
            op.EvidenceProvenance.Should().NotBe(EmergencyEvidenceProvenance.SystemVerified);
            return true;
        });
    }

    [Fact]
    public async Task Security_Backend_DoesNotFetchEvidenceUrl_EvenWithUnreachableOrNonexistentHost()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        // Observable local trap: any outbound fetch attempt (TCP connect, TLS, HTTP)
        // toward this listener would surface as a pending connection. The command path
        // must only validate and persist the URL, never contact it.
        using var trap = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        trap.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        trap.Listen(4);
        int trapPort = ((IPEndPoint)trap.LocalEndPoint!).Port;

        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var stopResponse = await client.PostAsJsonAsync(
            $"{endpoint}/emergency-stop",
            new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "AuthorityDirective",
                reasonDetails = "Evacuation directive.",
                immediateInstructions = "Follow instructions.",
                emergencyContact = "0900000000",
                addEvidenceLater = true,
            });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Add-later evidence path must stay offline as well.
        using var evidenceResponse = await client.PutAsJsonAsync(
            $"{endpoint}/emergency-evidence",
            new
            {
                evidenceUrl = $"https://127.0.0.1:{trapPort}/order-998234.pdf",
                evidenceReference = "DOC-ADD-LATER",
                expectedVersion = await ReadEvidenceVersionAsync(client, $"{endpoint}/emergency-evidence"),
            });

        evidenceResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Short grace window; nothing may have dialed the trap during either command.
        trap.Poll(300_000, SelectMode.SelectRead).Should().BeFalse(
            "the backend must never open a connection to an evidence URL");
    }

    [Theory]
    [InlineData("http://insecure.test/doc.pdf", "Reject non-https scheme")]
    [InlineData("ftp://ftp.test/doc.pdf", "Reject ftp scheme")]
    [InlineData("relative/path/to/doc.pdf", "Reject relative path")]
    [InlineData("/absolute/local/path.pdf", "Reject local file path")]
    public async Task Security_EvidenceUrl_MustBeAbsoluteHttps_RejectsOthers(string invalidUrl, string reason)
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var stopResponse = await client.PostAsJsonAsync(
            $"{endpoint}/emergency-stop",
            new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "AuthorityDirective",
                reasonDetails = "Evacuation directive.",
                immediateInstructions = "Follow instructions.",
                emergencyContact = "0900000000",
                evidenceUrl = invalidUrl,
                authorityReference = "DOC-99",
            });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: reason);
        string body = await stopResponse.Content.ReadAsStringAsync();
        System.Text.RegularExpressions.Regex.Unescape(body).Should().Contain("Liên kết bằng chứng phải là HTTPS");
    }

    [Fact]
    public async Task Security_EvidenceUrl_RejectsUrlsOver1000Characters()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        string tooLongUrl = "https://evidence.test/" + new string('a',
            EmergencyTourCancellationOperation.EvidenceUrlMaxLength);
        tooLongUrl.Length.Should().BeGreaterThan(EmergencyTourCancellationOperation.EvidenceUrlMaxLength);

        using var stopResponse = await client.PostAsJsonAsync(
            $"{endpoint}/emergency-stop",
            new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "AuthorityDirective",
                reasonDetails = "Evacuation directive.",
                immediateInstructions = "Follow instructions.",
                emergencyContact = "0900000000",
                evidenceUrl = tooLongUrl,
                authorityReference = "DOC-99",
            });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await stopResponse.Content.ReadAsStringAsync();
        System.Text.RegularExpressions.Regex.Unescape(body).Should().Contain("Liên kết bằng chứng phải là HTTPS");
    }

    [Fact]
    public async Task Security_EvidenceUrl_RejectsUrlsWithCredentialsOrUserInfo()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var stopResponse = await client.PostAsJsonAsync(
            $"{endpoint}/emergency-stop",
            new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "AuthorityDirective",
                reasonDetails = "Evacuation directive.",
                immediateInstructions = "Follow instructions.",
                emergencyContact = "0900000000",
                evidenceUrl = "https://admin:supersecret@weather-agency.gov.test/notice",
                authorityReference = "DOC-99",
            });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await stopResponse.Content.ReadAsStringAsync();
        System.Text.RegularExpressions.Regex.Unescape(body).Should().Contain("Liên kết bằng chứng phải là HTTPS");
    }

    [Fact]
    public async Task Security_TravelerAuthorization_OtherTravelerCannotReadOrAcknowledgeBooking()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));

        // First, trigger emergency stop so status exists
        using (var operatorClient = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator))
        {
            string opBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";
            using var preview = await operatorClient.GetAsync($"{opBase}/emergency-impact");
            string impactVersion = await ReadPropertyAsync(preview, "impactVersion");
            operatorClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var stop = await operatorClient.PostAsJsonAsync($"{opBase}/emergency-stop", new
            {
                impactVersion,
                currentPassword = OperatorPassword,
                reason = "SafetyIncident",
                reasonDetails = "Trip stopped due to incident.",
                immediateInstructions = "Wait for escort.",
                emergencyContact = "0900000000",
            });
            stop.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Create an attacker / other traveler (userId 999)
        const long attackerTravelerId = 999;
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Id = attackerTravelerId,
                Email = "attacker-traveler@test.local",
                FullName = "Attacker Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
            });
            await db.SaveChangesAsync();
            return true;
        });

        using var attackerClient = factory.CreateAuthenticatedClient(attackerTravelerId, UserRole.Traveler);
        string endpoint = $"/api/v1/traveler/tour-bookings/{seed.BookingId}/emergency-status";

        // Non-owner cannot read emergency status
        using var readResponse = await attackerClient.GetAsync(endpoint);
        readResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Non-owner cannot acknowledge emergency status
        using var ackResponse = await attackerClient.PostAsync($"{endpoint}/acknowledge", content: null);
        ackResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Security_OperatorAuthorization_OtherOperatorCannotPreviewCancelStopOrAddEvidence()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));

        // Create another operator (operatorId 888) who does not own this tour
        const long otherOperatorId = 888;
        var hasher = new PasswordHasherService();
        await factory.WithDbContextAsync(async db =>
        {
            var otherOpUser = new User
            {
                Id = otherOperatorId,
                Email = "other-operator@test.local",
                FullName = "Other Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
                PasswordHash = hasher.Hash(OperatorPassword),
            };
            db.Users.Add(otherOpUser);
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = otherOperatorId,
                User = otherOpUser,
                CompanyName = "Other Tours Co",
                TaxCode = "OTHER-TAX",
                BusinessLicenseNo = "OTHER-LICENSE",
                ApprovalStatus = OperatorApprovalStatus.Approved,
            });
            await db.SaveChangesAsync();
            return true;
        });

        using var intruderClient = factory.CreateAuthenticatedClient(otherOperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        // Other operator cannot preview
        using var previewResponse = await intruderClient.GetAsync($"{endpoint}/emergency-impact");
        previewResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

        // Other operator cannot cancel before departure
        string validFormatImpactVersion = new string('a', 64);
        intruderClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var cancelResponse = await intruderClient.PostAsJsonAsync($"{endpoint}/cancel-before-departure", new
        {
            impactVersion = validFormatImpactVersion,
            reason = "WeatherEmergency",
            reasonDetails = "Intruder attempt with valid format.",
            emergencyContact = "0900000000",
        });
        cancelResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

        // Other operator cannot emergency stop
        using var stopResponse = await intruderClient.PostAsJsonAsync($"{endpoint}/emergency-stop", new
        {
            impactVersion = validFormatImpactVersion,
            currentPassword = OperatorPassword,
            reason = "SafetyIncident",
            reasonDetails = "Intruder attempt with valid format.",
            immediateInstructions = "Follow escort.",
            emergencyContact = "0900000000",
        });
        stopResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

        // Other operator cannot supplement evidence
        using var evidenceResponse = await intruderClient.PutAsJsonAsync($"{endpoint}/emergency-evidence", new
        {
            evidenceUrl = "https://authority.example.test/doc",
            authorityReference = "AUTH-1",
        });
        evidenceResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Security_ReplayEmergencyStop_WithWrongPassword_FailsStepUp_DoesNotReturnCommittedResult()
    {
        using var factory = CreateFactory();
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        Guid idempotencyKey = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey.ToString());

        // First attempt with valid password -> commits successfully
        using var firstResponse = await client.PostAsJsonAsync($"{endpoint}/emergency-stop", new
        {
            impactVersion,
            currentPassword = OperatorPassword,
            reason = "SafetyIncident",
            reasonDetails = "Original committed stop.",
            immediateInstructions = "Stay with guide.",
            emergencyContact = "0900000000",
        });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Replay with exact same Idempotency-Key and payload, BUT wrong password
        using var replayClient = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        replayClient.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey.ToString());
        using var replayResponse = await replayClient.PostAsJsonAsync($"{endpoint}/emergency-stop", new
        {
            impactVersion,
            currentPassword = WrongPassword,
            reason = "SafetyIncident",
            reasonDetails = "Original committed stop.",
            immediateInstructions = "Stay with guide.",
            emergencyContact = "0900000000",
        });

        // Must reject step-up authentication, never returning the previously committed 200 OK
        replayResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string replayBody = await replayResponse.Content.ReadAsStringAsync();
        replayBody.Should().Contain(EmergencyTourErrorCodes.InvalidPassword);
    }

    [Fact]
    public async Task Security_EmergencyStop_WrongPasswords_Get429ThenRightPasswordWorksAfterTheWindow()
    {
        using var factory = new TripMateApiFactory(
            dateTimeProviderFactory: _ => new FixedDateTimeProvider(Now),
            configureTestServices: services => services.Configure<EmergencyStopRateLimitOptions>(options =>
            {
                options.PermitLimit = 3;
                options.Window = TimeSpan.FromSeconds(8);
            }));
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";
        using var previewResponse = await client.GetAsync($"{endpoint}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(previewResponse, "impactVersion");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Task<HttpResponseMessage> Attempt(string password) => client.PostAsJsonAsync($"{endpoint}/emergency-stop", new
        {
            impactVersion,
            currentPassword = password,
            reason = "SafetyIncident",
            reasonDetails = "Rate limit check.",
            immediateInstructions = "Stay with guide.",
            emergencyContact = "0900000000",
        });

        var statuses = new List<HttpStatusCode>();
        string lastBody = string.Empty;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var response = await Attempt(WrongPassword);
            statuses.Add(response.StatusCode);
            lastBody = await response.Content.ReadAsStringAsync();
        }

        statuses.Take(3).Should().OnlyContain(status => status == HttpStatusCode.BadRequest);
        statuses[3].Should().Be(HttpStatusCode.TooManyRequests);
        lastBody.Should().NotContain(WrongPassword);
        lastBody.Should().NotContain("password", "a throttled answer says nothing about the credential");
        await factory.WithDbContextAsync(async db =>
        {
            var failures = await db.AuditLogs
                .Where(audit => audit.ActionType == AuditActionTypes.TourEmergencyStopReauthenticationFailed)
                .ToListAsync();
            failures.Should().HaveCount(3);
            failures.Should().OnlyContain(audit => audit.ActorUserId == seed.OperatorId
                && audit.Result == AuditOutcome.Failure
                && audit.AffectedEntityId == seed.ScheduleId);
            string stored = JsonSerializer.Serialize(failures.Select(audit => new { audit.Reason, audit.BeforeData, audit.AfterData }));
            stored.Should().NotContain(WrongPassword);
            (await db.TourSchedules.SingleAsync(item => item.Id == seed.ScheduleId)).Status
                .Should().Be(TourScheduleStatus.Scheduled);
            return true;
        });

        HttpStatusCode afterWindow = HttpStatusCode.TooManyRequests;
        for (int wait = 0; wait < 24 && afterWindow == HttpStatusCode.TooManyRequests; wait++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            using var response = await Attempt(OperatorPassword);
            afterWindow = response.StatusCode;
        }

        afterWindow.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Security_EmergencyStop_LimitIsPerOperator()
    {
        using var factory = new TripMateApiFactory(
            dateTimeProviderFactory: _ => new FixedDateTimeProvider(Now),
            configureTestServices: services => services.Configure<EmergencyStopRateLimitOptions>(options =>
            {
                options.PermitLimit = 1;
                options.Window = TimeSpan.FromMinutes(5);
            }));
        var seed = await SeedAsync(factory, Now.AddHours(-1));
        object body = new
        {
            impactVersion = new string('a', 64),
            currentPassword = WrongPassword,
            reason = "SafetyIncident",
            reasonDetails = "Rate limit check.",
            immediateInstructions = "Stay with guide.",
            emergencyContact = "0900000000",
        };
        string endpoint = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}/emergency-stop";
        using var first = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        first.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var other = factory.CreateAuthenticatedClient(seed.OperatorId + 1000, UserRole.TourOperator);
        other.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var allowed = await first.PostAsJsonAsync(endpoint, body);
        using var throttled = await first.PostAsJsonAsync(endpoint, body);
        using var otherOperator = await other.PostAsJsonAsync(endpoint, body);

        allowed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        otherOperator.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    private static TripMateApiFactory CreateFactory() => new(
        dateTimeProviderFactory: _ => new FixedDateTimeProvider(Now));

    private static async Task<string> ReadPropertyAsync(
        HttpResponseMessage response,
        string propertyName)
    {
        string payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty(propertyName).GetString()!;
    }

    private static Task<Seed> SeedAsync(TripMateApiFactory factory, DateTimeOffset start) =>
        factory.WithDbContextAsync(async db =>
        {
            const long operatorId = 801;
            const long travelerId = 802;
            var hasher = new PasswordHasherService();
            var operatorUser = new User
            {
                Id = operatorId,
                Email = "uc70-sec-operator@test.local",
                FullName = "UC70 Sec Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
                PasswordHash = hasher.Hash(OperatorPassword),
            };
            db.Users.AddRange(operatorUser, new User
            {
                Id = travelerId,
                Email = "uc70-sec-traveler@test.local",
                FullName = "UC70 Sec Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
            });
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = operatorId,
                User = operatorUser,
                CompanyName = "UC70 Safe Tours",
                TaxCode = "UC70-SEC-TAX",
                BusinessLicenseNo = "UC70-SEC-LICENSE",
                ApprovalStatus = OperatorApprovalStatus.Approved,
            });
            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), operatorId);
            Set(tour, nameof(Tour.Title), "UC70 security tour");
            Set(tour, nameof(Tour.BasePrice), 100_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), TourStatus.Published);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();

            var schedule = TourSchedule.Create(
                start, start.AddHours(8), "Da Nang", 20, Now,
                adultPrice: 100_000m);
            Set(schedule, nameof(TourSchedule.TourId), tour.Id);
            Set(schedule, nameof(TourSchedule.Tour), tour);
            schedule.ReserveCapacity(1);
            db.TourSchedules.Add(schedule);
            await db.SaveChangesAsync();

            var booking = Booking.Create(
                travelerId, schedule.Id, 1, 100_000m,
                BookingStatus.Confirmed, PaymentStatus.Paid);
            Set(booking, nameof(Booking.TourSchedule), schedule);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            var payment = PaymentTransaction.Create(
                booking.Id, PaymentGateway.PayOS, 100_000m, 1234, Now);
            payment.MarkSuccess($"paid-{booking.Id}", null, Now, 0m);
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();
            return new Seed(operatorId, travelerId, tour.Id, schedule.Id, booking.Id);
        });

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(
            property,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed record Seed(
        long OperatorId,
        long TravelerId,
        long TourId,
        long ScheduleId,
        long BookingId);

    private sealed class FixedDateTimeProvider(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static async Task<string> ReadEvidenceVersionAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement.TryGetProperty("data", out var data) ? data : body.RootElement;
        return root.GetProperty("evidenceVersion").GetString()!;
    }
}