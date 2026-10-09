using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public sealed class RegisterOperatorSqlServerTests
{
    private const string Route = "/api/v1/auth/register/operator";
    private const string Password = "Password123!";

    private sealed class FirebaseStub : IFirebaseAuthService
    {
        public Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(
            string idToken, CancellationToken cancellationToken = default)
        {
            var verified = idToken.StartsWith("verified:", StringComparison.Ordinal);
            var email = verified ? idToken["verified:".Length..] : idToken;
            return Task.FromResult(new FirebaseTokenValidationResult(
                "operator-uid", email, verified, SignInProvider: "password"));
        }
    }

    private sealed class VerificationStatusStub : IEmailVerificationStatusService
    {
        public Task<bool> IsVerifiedAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class StorageStub : IOperatorDocumentStorage
    {
        private int _allocated;
        private int _deletes;

        public int Deletes => Volatile.Read(ref _deletes);

        public OperatorDocumentStorageDeleteOutcome DeleteOutcome { get; set; } =
            OperatorDocumentStorageDeleteOutcome.Deleted;

        public string AllocatePublicId() => $"operators/sql-test-{Interlocked.Increment(ref _allocated)}";

        public Task<OperatorDocumentStorageUploadResult> UploadAsync(
            OperatorDocumentStorageUpload request, CancellationToken cancellationToken) =>
            Task.FromResult(OperatorDocumentStorageUploadResult.Succeeded(
                new Uri($"https://cdn.example.test/{request.PublicId}")));

        public Task<OperatorDocumentStorageDeleteResult> DeleteAsync(
            string publicId, string contentType, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _deletes);
            return Task.FromResult(new OperatorDocumentStorageDeleteResult(
                DeleteOutcome, DeleteOutcome == OperatorDocumentStorageDeleteOutcome.TransientFailure
                    ? "PROVIDER_UNAVAILABLE" : null));
        }

        public Uri? CreateTemporaryDownloadUrl(string storedReference, DateTimeOffset expiresAtUtc) => null;
    }

    private sealed class FailAfterSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected failure after SQL writes, before commit.");
    }

    private sealed class ConcurrentSaveInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothSaving =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) == 2)
                _bothSaving.TrySetResult();
            await _bothSaving.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    private static TripMateApiFactory Factory(
        SqlServerTestDatabase database, StorageStub storage,
        SaveChangesInterceptor? interceptor = null) => new(
            sqlServerConnectionString: database.ConnectionString,
            firebaseServiceFactory: _ => new FirebaseStub(),
            saveChangesInterceptor: interceptor,
            configureTestServices: services =>
            {
                services.RemoveAll<IOperatorDocumentStorage>();
                services.AddSingleton<IOperatorDocumentStorage>(storage);
                services.RemoveAll<IEmailVerificationStatusService>();
                services.AddSingleton<IEmailVerificationStatusService, VerificationStatusStub>();
            });

    private static MultipartFormDataContent Form(
        string email, string taxCode = "0101234567", string licence = "79-0123/2026/TCDL-GPLHQT",
        string companyName = "UC-02 SQL Operator", string contactPerson = "SQL Operator",
        string? businessAddress = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(email), "firebaseIdToken");
        form.Add(new StringContent(email), "email");
        form.Add(new StringContent(Password), "password");
        form.Add(new StringContent(Password), "confirmPassword");
        form.Add(new StringContent(companyName), "companyName");
        form.Add(new StringContent(licence), "businessLicenseNo");
        form.Add(new StringContent(taxCode), "taxCode");
        form.Add(new StringContent(contactPerson), "contactPerson");
        if (businessAddress is not null)
            form.Add(new StringContent(businessAddress), "businessAddress");
        form.Add(new StringContent("true"), "acceptTerms");
        var file = new ByteArrayContent("%PDF-test"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "businessLicenseDocument", "licence.pdf");
        return form;
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RegisterVerifySignInAndAdminDetail_PersistOnePendingApplication()
    {
        const string companyName = "Công ty Du lịch Đà Nẵng";
        const string contactPerson = "Nguyễn Thị Ánh";
        const string businessAddress = "123 đường Trần Phú, Đà Nẵng";
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var storage = new StorageStub();
        using var factory = Factory(database, storage);
        using var client = factory.CreateClient();
        using var form = Form("operator@example.com", companyName: companyName,
            contactPerson: contactPerson, businessAddress: businessAddress);

        var registration = await client.PostAsync(Route, form);
        registration.StatusCode.Should().Be(HttpStatusCode.Created);
        var data = (await registration.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var userId = data.GetProperty("userId").GetInt64();
        data.GetProperty("applicationStatus").GetString().Should().Be("PendingApproval");

        await using (var persisted = database.CreateDbContext())
        {
            var user = await persisted.Users.SingleAsync();
            user.Id.Should().Be(userId);
            user.Status.Should().Be(AccountStatus.PendingApproval);
            user.EmailVerifiedAtUtc.Should().BeNull();
            user.FullName.Should().Be(contactPerson);
            var profile = await persisted.OperatorProfiles.SingleAsync();
            profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.PendingApproval);
            profile.CompanyName.Should().Be(companyName);
            profile.ContactAddress.Should().Be(businessAddress);
            (await persisted.OperatorDocuments.SingleAsync()).Status
                .Should().Be(DocumentStatus.Submitted);
        }

        using var verifyClient = factory.CreateClient();
        verifyClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "verified:operator@example.com");
        (await verifyClient.PostAsync("/api/v1/auth/web/verify-email", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var signIn = await client.PostAsJsonAsync("/api/v1/auth/web/login",
            new { email = "operator@example.com", password = Password });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);

        using var admin = factory.CreateAuthenticatedClient(userId, UserRole.Administrator);
        var detail = await admin.GetAsync($"/api/v1/admin/tour-operator-applications/{userId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var application = await detail.Content.ReadFromJsonAsync<JsonElement>();
        application.GetProperty("applicationStatus").GetString().Should().Be("PendingApproval");
        application.GetProperty("documents").GetArrayLength().Should().Be(1);

        await using var finalContext = database.CreateDbContext();
        (await finalContext.Users.SingleAsync()).EmailVerifiedAtUtc.Should().NotBeNull();
        (await finalContext.OperatorProfiles.SingleAsync()).ApprovalStatus
            .Should().Be(OperatorApprovalStatus.PendingApproval);
    }

    [SqlServerTheory]
    [InlineData("email", "MSG160")]
    [InlineData("tax", "MSG159")]
    [InlineData("licence", "MSG159")]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentDuplicateRegistration_OneSucceedsOtherConflicts_WithoutPartialRows(
        string duplicate, string expectedCode)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var storage = new StorageStub();
        using var factory = Factory(database, storage, new ConcurrentSaveInterceptor());
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        using var firstForm = Form("first@example.com", "0101234567", "79-0123/2026/TCDL-GPLHQT");
        using var secondForm = Form(
            duplicate == "email" ? "first@example.com" : "second@example.com",
            duplicate == "tax" ? "0101234567" : "0201234567",
            duplicate == "licence" ? "79-0123/2026/TCDL-GPLHQT" : "01-0456/2025/SDL-GPLHND");

        var responses = await Task.WhenAll(
            firstClient.PostAsync(Route, firstForm),
            secondClient.PostAsync(Route, secondForm));

        responses.Should().ContainSingle(response => response.StatusCode == HttpStatusCode.Created);
        var conflict = responses.Single(response => response.StatusCode != HttpStatusCode.Created);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conflict.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errorCode").GetString().Should().Be(expectedCode);

        await using var persisted = database.CreateDbContext();
        (await persisted.Users.CountAsync()).Should().Be(1);
        (await persisted.OperatorProfiles.CountAsync()).Should().Be(1);
        (await persisted.OperatorDocuments.CountAsync()).Should().Be(1);
        storage.Deletes.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FailureAfterSqlWrites_RollsBackGraphAndCompensatesUpload()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var storage = new StorageStub();
        using var factory = Factory(database, storage, new FailAfterSaveInterceptor());
        using var client = factory.CreateClient();
        using var form = Form("rollback@example.com");

        var response = await client.PostAsync(Route, form);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errorCode").GetString().Should().Be("MSG127");

        await using var persisted = database.CreateDbContext();
        (await persisted.Users.CountAsync()).Should().Be(0);
        (await persisted.OperatorProfiles.CountAsync()).Should().Be(0);
        (await persisted.OperatorDocuments.CountAsync()).Should().Be(0);
        storage.Deletes.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FailureAfterSqlWrites_AndDeleteFailure_PreservesDurableCleanupIntent()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var storage = new StorageStub
        {
            DeleteOutcome = OperatorDocumentStorageDeleteOutcome.TransientFailure,
        };
        using var factory = Factory(database, storage, new FailAfterSaveInterceptor());
        using var client = factory.CreateClient();
        using var form = Form("orphan@example.com");

        var response = await client.PostAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await using var persisted = database.CreateDbContext();
        (await persisted.Users.CountAsync()).Should().Be(0);
        (await persisted.OperatorDocuments.CountAsync()).Should().Be(0);
        storage.Deletes.Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.OperatorDocumentCleanupOutbox
            WHERE cleanup_status = 'Pending' AND content_type = 'application/pdf';
            """)).Should().Be(1);
    }
}