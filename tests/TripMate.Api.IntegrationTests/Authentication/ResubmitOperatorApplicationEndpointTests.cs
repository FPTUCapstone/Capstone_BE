using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public sealed class ResubmitOperatorApplicationEndpointTests
{
    private const string ResubmitRoute = "/api/v1/operator/application/resubmit";
    private const string GetRoute = "/api/v1/operator/application";

    private sealed class StorageStub : IOperatorDocumentStorage
    {
        public int Uploads { get; private set; }
        public int Deletes { get; private set; }
        public bool FailUpload { get; set; }

        public string AllocatePublicId() => $"operators/endpoint-test-{Uploads + 1}";

        public Task<OperatorDocumentStorageUploadResult> UploadAsync(
            OperatorDocumentStorageUpload request, CancellationToken cancellationToken)
        {
            Uploads++;
            return Task.FromResult(FailUpload
                ? OperatorDocumentStorageUploadResult.Failed(TourMediaStorageFailureKind.Transient, "PROVIDER_UNAVAILABLE")
                : OperatorDocumentStorageUploadResult.Succeeded(
                    new Uri($"cloudinary-operator://asset/raw/pdf/{Uri.EscapeDataString(request.PublicId)}")));
        }

        public Task<OperatorDocumentStorageDeleteResult> DeleteAsync(
            string publicId, string contentType, CancellationToken cancellationToken)
        {
            Deletes++;
            return Task.FromResult(new OperatorDocumentStorageDeleteResult(
                OperatorDocumentStorageDeleteOutcome.Deleted, null));
        }

        public Uri? CreateTemporaryDownloadUrl(string storedReference, DateTimeOffset expiresAtUtc) =>
            new($"https://cdn.example.test/signed-document?ref={Uri.EscapeDataString(storedReference)}");
    }

    private sealed class CleanupJournalStub : IOperatorDocumentCleanupJournal
    {
        public Task ReserveAsync(string publicId, string contentType,
            DateTimeOffset notBeforeAtUtc, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(string publicId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RetryNowAsync(string publicId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static TripMateApiFactory Factory(StorageStub storage) => new(
        configureTestServices: services =>
        {
            services.RemoveAll<IOperatorDocumentStorage>();
            services.AddSingleton<IOperatorDocumentStorage>(storage);
            services.RemoveAll<IOperatorDocumentCleanupJournal>();
            services.AddSingleton<IOperatorDocumentCleanupJournal, CleanupJournalStub>();
        });

    private static MultipartFormDataContent Form(
        string companyName = "Cty Du Lịch Đất Việt",
        string licenseNo = "79-0123/2026/TCDL-GPLHQT",
        string taxCode = "0101234567",
        string contactPerson = "Nguyễn Văn A",
        string? businessAddress = "123 Lê Lợi, Q1, TP.HCM",
        string? contactPhone = "0987654321",
        byte[]? licenseBytes = null,
        byte[]? supportingBytes = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(companyName), "companyName");
        form.Add(new StringContent(licenseNo), "businessLicenseNo");
        form.Add(new StringContent(taxCode), "taxCode");
        form.Add(new StringContent(contactPerson), "contactPerson");
        if (businessAddress is not null)
            form.Add(new StringContent(businessAddress), "businessAddress");
        if (contactPhone is not null)
            form.Add(new StringContent(contactPhone), "contactPhone");

        if (licenseBytes is not null)
        {
            var licenseContent = new ByteArrayContent(licenseBytes);
            licenseContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(licenseContent, "businessLicenseDocument", "license.pdf");
        }

        if (supportingBytes is not null)
        {
            var supportingContent = new ByteArrayContent(supportingBytes);
            supportingContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(supportingContent, "supportingDocuments", "cert.png");
        }

        return form;
    }

    private static async Task<long> SeedRejectedOperatorAsync(
        TripMateApiFactory factory,
        string email = "operator@example.com",
        string taxCode = "0101234567",
        string licenseNo = "79-0123/2026/TCDL-GPLHQT",
        AccountStatus accountStatus = AccountStatus.Rejected,
        OperatorApprovalStatus approvalStatus = OperatorApprovalStatus.Rejected,
        bool includeLicenseDocument = true)
    {
        long userId = 0;
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Email = email,
                FullName = "Old Contact Person",
                PasswordHash = "hashed",
                Role = UserRole.TourOperator,
                Status = accountStatus,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;

            var profile = new OperatorProfile
            {
                UserId = user.Id,
                CompanyName = "Old Company Name",
                TaxCode = taxCode,
                BusinessLicenseNo = licenseNo,
                ContactAddress = "Old Address",
                ContactPhone = "0900000000",
                ApprovalStatus = approvalStatus,
                RejectionReason = "Giấy phép không hợp lệ, mờ scan.",
                ReviewedBy = 1,
                ReviewedAtUtc = DateTimeOffset.UtcNow.AddDays(-3),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-3),
            };

            if (includeLicenseDocument)
            {
                profile.Documents.Add(new OperatorDocument
                {
                    OperatorUserId = user.Id,
                    DocumentType = OperatorDocumentType.BusinessLicense,
                    FileUrl = "cloudinary-operator://asset/raw/pdf/old-license",
                    Status = DocumentStatus.Rejected,
                    UploadedAtUtc = DateTimeOffset.UtcNow.AddDays(-5),
                });
            }

            db.OperatorProfiles.Add(profile);
            await db.SaveChangesAsync();
            return true;
        });

        return userId;
    }

    [Fact]
    public async Task Resubmit_AnonymousCaller_ReturnsUnauthorized()
    {
        var storage = new StorageStub();
        using var factory = Factory(storage);
        using var client = factory.CreateClient();
        using var form = Form();

        var response = await client.PutAsync(ResubmitRoute, form);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.Administrator)]
    public async Task Resubmit_WrongRole_ReturnsForbidden(UserRole wrongRole)
    {
        var storage = new StorageStub();
        using var factory = Factory(storage);
        using var client = factory.CreateAuthenticatedClient(1234, wrongRole);
        using var form = Form();

        var response = await client.PutAsync(ResubmitRoute, form);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Resubmit_OperatorWithoutApplication_ReturnsNotFound()
    {
        var storage = new StorageStub();
        using var factory = Factory(storage);
        var userId = 0L;
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Email = "orphan-operator@example.com",
                FullName = "No Profile Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Rejected,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
            return true;
        });

        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form();

        var response = await client.PutAsync(ResubmitRoute, form);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.PendingApproval)]
    [InlineData(AccountStatus.Active, OperatorApprovalStatus.Approved)]
    public async Task Resubmit_ApplicationNotRejected_ReturnsConflictMsg161(
        AccountStatus accountStatus, OperatorApprovalStatus approvalStatus)
    {
        var storage = new StorageStub();
        using var factory = Factory(storage);
        var userId = await SeedRejectedOperatorAsync(factory, "pending@example.com",
            accountStatus: accountStatus, approvalStatus: approvalStatus);

        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form();

        var response = await client.PutAsync(ResubmitRoute, form);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("MSG161");
    }

    [Fact]
    public async Task Resubmit_NoNewLicenseAndNoRetainedLicense_ReturnsBadRequestMsg157()
    {
        var storage = new StorageStub();
        using var factory = Factory(storage);
        var userId = await SeedRejectedOperatorAsync(factory, "nolicense@example.com",
            includeLicenseDocument: false);

        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(licenseBytes: null);

        var response = await client.PutAsync(ResubmitRoute, form);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("MSG157");
    }

    [Fact]
    public async Task Resubmit_HappyPath_WithNewBusinessLicense_Returns200AndUpdatesState()
    {
        var storage = new StorageStub();
        using var factory = Factory(storage);
        var userId = await SeedRejectedOperatorAsync(factory, "resubmit-success@example.com");

        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(
            companyName: "Du Lịch Bốn Phương Mới",
            licenseNo: "79-0123/2026/TCDL-GPLHQT",
            taxCode: "0101234567",
            contactPerson: "Lê Văn B",
            licenseBytes: "%PDF-new-license"u8.ToArray(),
            supportingBytes: [137, 80, 78, 71, 13, 10, 26, 10]);

        var response = await client.PutAsync(ResubmitRoute, form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("userId").GetInt64().Should().Be(userId);
        json.GetProperty("userStatus").GetString().Should().Be("PendingApproval");
        json.GetProperty("approvalStatus").GetString().Should().Be("PendingApproval");
        json.GetProperty("messageCode").GetString().Should().Be("MSG162");
        json.GetProperty("resubmissionCount").GetInt32().Should().Be(1);

        // Verify DB updates
        await factory.WithDbContextAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.Status.Should().Be(AccountStatus.PendingApproval);
            user.FullName.Should().Be("Lê Văn B");

            var profile = await db.OperatorProfiles.Include(p => p.Documents).SingleAsync(p => p.UserId == userId);
            profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.PendingApproval);
            profile.CompanyName.Should().Be("Du Lịch Bốn Phương Mới");
            profile.RejectionReason.Should().BeNull();
            profile.ReviewedBy.Should().BeNull();
            profile.ReviewedAtUtc.Should().BeNull();

            // 1 old rejected license, 1 new submitted license, 1 supporting document
            profile.Documents.Should().HaveCount(3);
            profile.Documents.Count(d => d.DocumentType == OperatorDocumentType.BusinessLicense && d.Status == DocumentStatus.Submitted).Should().Be(1);
            profile.Documents.Count(d => d.DocumentType == OperatorDocumentType.BusinessLicense && d.Status == DocumentStatus.Rejected).Should().Be(1);

            var audit = await db.AuditLogs.SingleAsync(a => a.AffectedEntityId == userId);
            audit.ActionType.Should().Be("ResubmitOperatorApplication");
            audit.BeforeData.Should().Contain("Old Company Name");
            audit.BeforeData.Should().Contain("RejectionReason");
            audit.AfterData.Should().Contain("PendingApproval");
            return true;
        });
    }

    [Fact]
    public async Task Resubmit_HappyPath_RetainingExistingRejectedLicense_Returns200()
    {
        var storage = new StorageStub();
        using var factory = Factory(storage);
        var userId = await SeedRejectedOperatorAsync(factory, "retain-license@example.com");

        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(
            companyName: "Công Ty Giữ Giấy Phép Cũ",
            licenseBytes: null,
            supportingBytes: null);

        var response = await client.PutAsync(ResubmitRoute, form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("messageCode").GetString().Should().Be("MSG162");

        await factory.WithDbContextAsync(async db =>
        {
            var profile = await db.OperatorProfiles.Include(p => p.Documents).SingleAsync(p => p.UserId == userId);
            profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.PendingApproval);
            profile.Documents.Single().Status.Should().Be(DocumentStatus.Submitted);
            return true;
        });
    }

    [Fact]
    public async Task Resubmit_DuplicateTaxCodeOfOtherOperator_ReturnsConflictMsg159()
    {
        var storage = new StorageStub();
        using var factory = Factory(storage);

        // Seed other active operator with conflicting tax code
        await factory.WithDbContextAsync(async db =>
        {
            var other = new User
            {
                Email = "existing@example.com",
                FullName = "Existing",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
            };
            db.Users.Add(other);
            await db.SaveChangesAsync();

            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = other.Id,
                CompanyName = "Existing Co",
                TaxCode = "0201234567",
                BusinessLicenseNo = "01-9999/2026/SDL-GPLHND",
            });
            await db.SaveChangesAsync();
            return true;
        });

        var userId = await SeedRejectedOperatorAsync(factory, "resubmit-conflict@example.com");

        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(taxCode: "0201234567");

        var response = await client.PutAsync(ResubmitRoute, form);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("MSG159");
    }

    [Fact]
    public async Task GetApplication_ReturnsCorrectProjectionAndSignedUrls()
    {
        var storage = new StorageStub();
        using var factory = Factory(storage);
        var userId = await SeedRejectedOperatorAsync(factory, "get-app@example.com");

        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        var response = await client.GetAsync(GetRoute);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("userId").GetInt64().Should().Be(userId);
        json.GetProperty("userStatus").GetString().Should().Be("Rejected");
        json.GetProperty("approvalStatus").GetString().Should().Be("Rejected");
        json.GetProperty("rejectionReason").GetString().Should().Be("Giấy phép không hợp lệ, mờ scan.");

        var documents = json.GetProperty("documents");
        documents.GetArrayLength().Should().Be(1);
        var doc = documents[0];
        doc.GetProperty("documentType").GetString().Should().Be("BusinessLicense");
        doc.GetProperty("status").GetString().Should().Be("Rejected");
        doc.GetProperty("downloadUrl").GetString().Should().StartWith("https://cdn.example.test/signed-document");
        doc.TryGetProperty("fileUrl", out _).Should().BeFalse();
    }
}