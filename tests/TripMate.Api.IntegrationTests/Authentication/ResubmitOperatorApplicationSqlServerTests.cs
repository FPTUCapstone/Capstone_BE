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
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public sealed class ResubmitOperatorApplicationSqlServerTests
{
    private const string Route = "/api/v1/operator/application/resubmit";

    private sealed class StorageStub : IOperatorDocumentStorage
    {
        private int _allocated;
        private int _deletes;

        public int Deletes => Volatile.Read(ref _deletes);

        public OperatorDocumentStorageDeleteOutcome DeleteOutcome { get; set; } =
            OperatorDocumentStorageDeleteOutcome.Deleted;

        public string AllocatePublicId() => $"operators/resubmit-sql-{Interlocked.Increment(ref _allocated)}";

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

    private sealed class ConcurrentSaveInterceptor(int participants) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var isTargetSave = eventData.Context?.ChangeTracker
                .Entries<OperatorProfile>()
                .Any(entry => entry.State == EntityState.Modified &&
                    entry.Property(profile => profile.ApprovalStatus).IsModified) == true;

            if (!isTargetSave)
            {
                return result;
            }

            if (Interlocked.Increment(ref _arrivals) == participants)
            {
                _release.TrySetResult(true);
            }

            await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    private static TripMateApiFactory Factory(
        SqlServerTestDatabase database, StorageStub storage,
        SaveChangesInterceptor? interceptor = null) => new(
            sqlServerConnectionString: database.ConnectionString,
            saveChangesInterceptor: interceptor,
            configureTestServices: services =>
            {
                services.RemoveAll<IOperatorDocumentStorage>();
                services.AddSingleton<IOperatorDocumentStorage>(storage);
            });

    private static MultipartFormDataContent Form(
        string companyName = "Cty Du Lịch Đà Nẵng",
        string licenseNo = "79-0123/2026/TCDL-GPLHQT",
        string taxCode = "0101234567",
        string contactPerson = "Nguyễn Văn A",
        string? businessAddress = "123 Trần Phú, Đà Nẵng",
        byte[]? licenseBytes = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(companyName), "companyName");
        form.Add(new StringContent(licenseNo), "businessLicenseNo");
        form.Add(new StringContent(taxCode), "taxCode");
        form.Add(new StringContent(contactPerson), "contactPerson");
        if (businessAddress is not null)
            form.Add(new StringContent(businessAddress), "businessAddress");

        var bytes = licenseBytes ?? "%PDF-test"u8.ToArray();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "businessLicenseDocument", "license.pdf");
        return form;
    }

    private static async Task<long> SeedRejectedOperatorAsync(
        SqlServerTestDatabase database,
        string email = "resubmit-sql@example.com",
        string taxCode = "0101234567",
        string licenseNo = "79-0123/2026/TCDL-GPLHQT")
    {
        await using var db = database.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var admin = new User
        {
            Email = $"admin-{Guid.NewGuid():N}@example.com",
            FullName = "Admin Reviewer",
            PasswordHash = "hashed",
            Role = UserRole.Administrator,
            Status = AccountStatus.Active,
            CreatedAtUtc = now.AddDays(-10),
            UpdatedAtUtc = now.AddDays(-10),
        };
        var user = new User
        {
            Email = email,
            FullName = "Nguyên Bản Contact",
            PasswordHash = "hashed",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Rejected,
            CreatedAtUtc = now.AddDays(-5),
            UpdatedAtUtc = now.AddDays(-2),
        };
        db.Users.AddRange(admin, user);
        await db.SaveChangesAsync();

        var profile = new OperatorProfile
        {
            UserId = user.Id,
            CompanyName = "Nguyên Bản Company",
            TaxCode = taxCode,
            BusinessLicenseNo = licenseNo,
            ContactAddress = "123 Cũ, Đà Nẵng",
            ContactPhone = "0901234567",
            ApprovalStatus = OperatorApprovalStatus.Rejected,
            RejectionReason = "Giấy phép không rõ ràng, yêu cầu chụp lại bản gốc.",
            ReviewedBy = admin.Id,
            ReviewedAtUtc = now.AddDays(-2),
            CreatedAtUtc = now.AddDays(-5),
            UpdatedAtUtc = now.AddDays(-2),
        };

        profile.Documents.Add(new OperatorDocument
        {
            OperatorUserId = user.Id,
            DocumentType = OperatorDocumentType.BusinessLicense,
            FileUrl = "https://cdn.example.test/operators/old-license.pdf",
            Status = DocumentStatus.Rejected,
            UploadedAtUtc = now.AddDays(-5),
        });

        db.OperatorProfiles.Add(profile);
        await db.SaveChangesAsync();

        return user.Id;
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FailureAfterSqlWrites_RollsBackAllEntitiesAndCompensatesUpload()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var storage = new StorageStub();
        var userId = await SeedRejectedOperatorAsync(database);

        using var factory = Factory(database, storage, new FailAfterSaveInterceptor());
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(companyName: "Công Ty Sau Sửa Đổi");

        var response = await client.PutAsync(Route, form);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("MSG127");

        // Verify total rollback in SQL Server
        await using var persisted = database.CreateDbContext();
        var user = await persisted.Users.SingleAsync(u => u.Id == userId);
        user.Status.Should().Be(AccountStatus.Rejected);
        user.FullName.Should().Be("Nguyên Bản Contact");

        var profile = await persisted.OperatorProfiles.Include(p => p.Documents).SingleAsync(p => p.UserId == userId);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Rejected);
        profile.CompanyName.Should().Be("Nguyên Bản Company");
        profile.RejectionReason.Should().Be("Giấy phép không rõ ràng, yêu cầu chụp lại bản gốc.");
        profile.ReviewedBy.Should().NotBeNull();

        // Retained document remains Rejected, no new submitted document created
        profile.Documents.Should().HaveCount(1);
        profile.Documents.Single().Status.Should().Be(DocumentStatus.Rejected);

        // No audit row persisted
        (await persisted.AuditLogs.CountAsync(a => a.AffectedEntityId == userId)).Should().Be(0);

        // Uploaded asset compensated
        storage.Deletes.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentResubmission_OneSucceedsAndOtherReturnsConflictMsg161_WithOneAuditLog()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var storage = new StorageStub();
        var userId = await SeedRejectedOperatorAsync(database);

        using var factory = Factory(database, storage, new ConcurrentSaveInterceptor(2));
        using var firstClient = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var secondClient = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);

        using var firstForm = Form(companyName: "Công Ty Nhánh 1", licenseBytes: "%PDF-branch-1"u8.ToArray());
        using var secondForm = Form(companyName: "Công Ty Nhánh 2", licenseBytes: "%PDF-branch-2"u8.ToArray());

        var responses = await Task.WhenAll(
            firstClient.PutAsync(Route, firstForm),
            secondClient.PutAsync(Route, secondForm));

        // Exactly one 200 OK and one 409 Conflict
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.OK);
        var conflict = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var conflictJson = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        conflictJson.GetProperty("errorCode").GetString().Should().Be("MSG161");

        // Verify only 1 audit log and exactly 1 winning new document
        await using var persisted = database.CreateDbContext();
        var profile = await persisted.OperatorProfiles.Include(p => p.Documents).SingleAsync(p => p.UserId == userId);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.PendingApproval);

        // 1 original rejected doc + 1 winning submitted doc
        profile.Documents.Should().HaveCount(2);
        profile.Documents.Count(d => d.Status == DocumentStatus.Submitted).Should().Be(1);
        profile.Documents.Count(d => d.Status == DocumentStatus.Rejected).Should().Be(1);

        // Exactly 1 audit log
        (await persisted.AuditLogs.CountAsync(a => a.AffectedEntityId == userId && a.ActionType == "ResubmitOperatorApplication"))
            .Should().Be(1);

        // The losing upload was compensated
        storage.Deletes.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FailureAfterSqlWrites_AndDeleteFailure_PreservesDurableCleanupOutbox()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var storage = new StorageStub
        {
            DeleteOutcome = OperatorDocumentStorageDeleteOutcome.TransientFailure,
        };
        var userId = await SeedRejectedOperatorAsync(database);

        using var factory = Factory(database, storage, new FailAfterSaveInterceptor());
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(companyName: "Durable Cleanup Test");

        var response = await client.PutAsync(Route, form);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        await using var persisted = database.CreateDbContext();
        var profile = await persisted.OperatorProfiles.Include(p => p.Documents).SingleAsync(p => p.UserId == userId);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Rejected);
        profile.Documents.Should().HaveCount(1);
        storage.Deletes.Should().Be(1);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.OperatorDocumentCleanupOutbox
            WHERE cleanup_status = 'Pending' AND content_type = 'application/pdf';
            """)).Should().Be(1);
    }
}