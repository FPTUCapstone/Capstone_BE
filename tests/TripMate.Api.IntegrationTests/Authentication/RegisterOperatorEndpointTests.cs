using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.Common;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.Authentication.RegisterOperator;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public class RegisterOperatorEndpointTests
{
    private sealed class FirebaseStub : IFirebaseAuthService
    {
        public string Email { get; set; } = "operator@example.com";
        public bool ThrowInvalid { get; set; }
        public int Calls { get; private set; }

        public Task<FirebaseTokenValidationResult> VerifyIdTokenAsync(
            string idToken, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ThrowInvalid) throw new InvalidOperationException("invalid token");
            return Task.FromResult(new FirebaseTokenValidationResult("uid", Email, false));
        }
    }

    private sealed class StorageStub : IOperatorDocumentStorage
    {
        public bool FailUpload { get; set; }
        public int Uploads { get; private set; }
        public int Deletes { get; private set; }
        public DateTimeOffset? LastDownloadExpiry { get; private set; }

        public string AllocatePublicId() => $"operators/{Uploads + 1}";

        public Task<OperatorDocumentStorageUploadResult> UploadAsync(
            OperatorDocumentStorageUpload request, CancellationToken cancellationToken)
        {
            Uploads++;
            return Task.FromResult(FailUpload
                ? OperatorDocumentStorageUploadResult.Failed(TourMediaStorageFailureKind.Transient, "SAFE")
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

        public Uri? CreateTemporaryDownloadUrl(string storedReference, DateTimeOffset expiresAtUtc)
        {
            LastDownloadExpiry = expiresAtUtc;
            return new Uri("https://api.cloudinary.com/signed-document?expires_at=1791461100");
        }
    }

    private static TripMateApiFactory Factory(FirebaseStub firebase, StorageStub storage) => new(
        firebaseServiceFactory: _ => firebase,
        configureTestServices: services =>
        {
            services.RemoveAll<IOperatorDocumentStorage>();
            services.AddSingleton<IOperatorDocumentStorage>(storage);
        });

    private static MultipartFormDataContent Form(
        bool includeToken = true, bool includeLicense = true, string email = "operator@example.com",
        string confirmPassword = "Password123!", string fileName = "license.pdf",
        string contentType = "application/pdf", string taxCode = "0101234567",
        string licence = "79-0123/2026/TCDL-GPLHQT")
    {
        var form = new MultipartFormDataContent();
        if (includeToken) form.Add(new StringContent("firebase-token"), "firebaseIdToken");
        form.Add(new StringContent(email), "email");
        form.Add(new StringContent("Password123!"), "password");
        form.Add(new StringContent(confirmPassword), "confirmPassword");
        form.Add(new StringContent("Operator Co"), "companyName");
        form.Add(new StringContent(licence), "businessLicenseNo");
        form.Add(new StringContent(taxCode), "taxCode");
        form.Add(new StringContent("Operator Name"), "contactPerson");
        form.Add(new StringContent("true"), "acceptTerms");
        if (includeLicense)
        {
            var file = new ByteArrayContent("%PDF-test"u8.ToArray());
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "businessLicenseDocument", fileName);
        }
        return form;
    }

    [Fact]
    public async Task Post_ExceedingPerIpLimit_Returns429BeforeMultipartBindingOrUpload()
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < OperatorRegistrationRateLimiter.PermitLimit; attempt++)
        {
            using var content = Form();
            using var allowed = await client.PostAsync("/api/v1/auth/register/operator", content);
            allowed.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        using var rejectedContent = new ByteArrayContent("not-a-multipart-body"u8.ToArray());
        rejectedContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
            "multipart/form-data; boundary=missing");
        using var rejected = await client.PostAsync("/api/v1/auth/register/operator", rejectedContent);
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(429);
        firebase.Calls.Should().Be(OperatorRegistrationRateLimiter.PermitLimit);
        storage.Uploads.Should().Be(1);
    }

    [Fact]
    public async Task Post_ValidMultipart_CreatesPendingApplicationAndReturns201()
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        using var form = Form();
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("applicationStatus").GetString().Should().Be("PendingApproval");
        body.GetProperty("data").GetProperty("messageCode").GetString().Should().Be("MSG08");
        body.GetProperty("data").GetProperty("userId").GetInt64().Should().BePositive();
        storage.Uploads.Should().Be(1);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync()).Status.Should().Be(AccountStatus.PendingApproval);
            db.OperatorProfiles.Should().ContainSingle();
            db.OperatorDocuments.Should().ContainSingle();
            return true;
        });
    }

    [Fact]
    public async Task AdminDetail_IssuesSignedDocumentOnlyAfterAuthorization()
    {
        var storage = new StorageStub();
        using var factory = Factory(new FirebaseStub(), storage);
        using var registrationClient = factory.CreateClient();
        using var form = Form();
        using var registration = await registrationClient.PostAsync("/api/v1/auth/register/operator", form);
        registration.StatusCode.Should().Be(HttpStatusCode.Created);
        var registered = await registration.Content.ReadFromJsonAsync<JsonElement>();
        long userId = registered.GetProperty("data").GetProperty("userId").GetInt64();
        var route = $"/api/v1/admin/tour-operator-applications/{userId}";

        using var anonymous = await registrationClient.GetAsync(route);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var travelerClient = factory.CreateAuthenticatedClient(10, UserRole.Traveler);
        using var forbidden = await travelerClient.GetAsync(route);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        storage.LastDownloadExpiry.Should().BeNull();

        using var adminClient = factory.CreateAuthenticatedClient(11, UserRole.Administrator);
        using var allowed = await adminClient.GetAsync(route);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        allowed.Headers.CacheControl!.NoStore.Should().BeTrue();
        var detail = await allowed.Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("documents")[0].GetProperty("fileUrl").GetString()
            .Should().StartWith("https://api.cloudinary.com/signed-document");
        storage.LastDownloadExpiry.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false, true, "AUTH_TOKEN_MISSING", "firebaseIdToken")]
    [InlineData(true, false, "MSG157", "businessLicenseDocument")]
    public async Task Post_MissingRequiredField_ReturnsFieldCode(
        bool token, bool license, string code, string field)
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        using var form = Form(includeToken: token, includeLicense: license);
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty(field).EnumerateArray()
            .Should().ContainSingle(code);
        storage.Uploads.Should().Be(0);
        firebase.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("010123456A", "79-0123/2026/TCDL-GPLHQT", "taxCode", "OPERATOR_TAX_CODE_INVALID")]
    [InlineData("0101234567", "79-0123/2026/TCDL-GPLHND", "businessLicenseNo", "OPERATOR_TRAVEL_LICENSE_INVALID")]
    public async Task Post_InvalidBusinessIdentifier_ReturnsFieldErrorBeforeExternalCalls(
        string taxCode, string licence, string field, string code)
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        using var form = Form(taxCode: taxCode, licence: licence);
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty(field)[0].GetString().Should().Be(code);
        firebase.Calls.Should().Be(0);
        storage.Uploads.Should().Be(0);
    }

    [Theory]
    [InlineData("application/octet-stream", "license.exe", "MSG158", "businessLicenseDocument")]
    [InlineData("application/pdf", "license.pdf", "MSG06", "confirmPassword")]
    public async Task Post_InvalidFileOrConfirmation_ReturnsFieldCode(
        string contentType, string fileName, string code, string field)
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        using var form = Form(confirmPassword: code == "MSG06" ? "Mismatch123!" : "Password123!",
            fileName: fileName, contentType: contentType);
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetProperty(field).EnumerateArray().Should().ContainSingle(code);
        storage.Uploads.Should().Be(0);
    }

    [Theory]
    [InlineData("invalid", HttpStatusCode.Unauthorized, "AUTH_TOKEN_INVALID")]
    [InlineData("mismatch", HttpStatusCode.BadRequest, "AUTH_EMAIL_MISMATCH")]
    public async Task Post_InvalidOrMismatchedFirebaseToken_IsRejectedBeforeUpload(
        string scenario, HttpStatusCode status, string code)
    {
        var firebase = new FirebaseStub
        {
            ThrowInvalid = scenario == "invalid",
            Email = scenario == "mismatch" ? "other@example.com" : "operator@example.com",
        };
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        using var form = Form();
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(status);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errorCode").GetString().Should().Be(code);
        storage.Uploads.Should().Be(0);
    }

    [Fact]
    public async Task Post_DuplicateEmail_Returns409()
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Email = "operator@example.com",
                FullName = "Existing",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
            });
            await db.SaveChangesAsync();
            return true;
        });
        using var form = Form();
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errorCode").GetString().Should().Be("MSG03");
        storage.Uploads.Should().Be(0);
    }

    [Theory]
    [InlineData("pending", "MSG160")]
    [InlineData("tax", "MSG159")]
    [InlineData("license", "MSG159")]
    public async Task Post_DuplicatePendingApplicationOrBusinessIdentifier_Returns409(
        string scenario, string code)
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Email = scenario == "pending" ? "operator@example.com" : "old@example.com",
                FullName = "Existing",
                Role = UserRole.TourOperator,
                Status = AccountStatus.PendingApproval,
            };
            db.OperatorProfiles.Add(new OperatorProfile
            {
                User = user,
                CompanyName = "Existing",
                TaxCode = scenario == "tax" ? "0101234567" : "0201234567",
                BusinessLicenseNo = scenario == "license" ? "79-0123/2026/TCDL-GPLHQT" : "01-0456/2025/SDL-GPLHND",
                ApprovalStatus = OperatorApprovalStatus.PendingApproval,
            });
            await db.SaveChangesAsync();
            return true;
        });
        using var form = Form();
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be(code);
        body.GetProperty("title").GetString().Should().Be(
            code == "MSG160"
                ? OperatorRegistrationMessages.PendingApplicationExists
                : OperatorRegistrationMessages.BusinessIdentifierExists);
        storage.Uploads.Should().Be(0);
    }

    [Fact]
    public async Task Post_InvalidSupportingDocument_UsesIndexedFieldCode()
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        using var form = Form();
        var file = new ByteArrayContent("bad"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "supportingDocuments", "bad.exe");
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errors").GetProperty("supportingDocuments[0]")[0]
            .GetString().Should().Be("MSG158");
        storage.Uploads.Should().Be(0);
    }

    [Fact]
    public async Task Post_TooManySupportingDocuments_ReturnsCountErrorBeforeUpload()
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub();
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        using var form = Form();
        for (var index = 0; index < 6; index++)
        {
            var file = new ByteArrayContent("%PDF-test"u8.ToArray());
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "supportingDocuments", $"extra-{index}.pdf");
        }
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errors").GetProperty("supportingDocuments")[0]
            .GetString().Should().Be("auth.request_invalid");
        storage.Uploads.Should().Be(0);
    }

    [Fact]
    public async Task Post_StorageFailure_Returns503WithoutPersistingRows()
    {
        var firebase = new FirebaseStub();
        var storage = new StorageStub { FailUpload = true };
        using var factory = Factory(firebase, storage);
        using var client = factory.CreateClient();
        using var form = Form();
        var response = await client.PostAsync("/api/v1/auth/register/operator", form);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errorCode").GetString().Should().Be("MSG127");
        storage.Deletes.Should().Be(1);
        await factory.WithDbContextAsync(db =>
        {
            db.Users.Should().BeEmpty();
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task Post_JsonPayload_IsRejectedAsUnsupportedMediaType()
    {
        using var factory = Factory(new FirebaseStub(), new StorageStub());
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register/operator",
            new { email = "operator@example.com" });
        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task OpenApi_DescribesMultipartFormAnd201Response()
    {
        await using var factory = Factory(new FirebaseStub(), new StorageStub());
        var swagger = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        using var json = JsonDocument.Parse(await swagger.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0));
        var post = json.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/auth/register/operator").GetProperty("post");
        post.GetProperty("requestBody").GetProperty("content")
            .TryGetProperty("multipart/form-data", out _).Should().BeTrue();
        var schema = post.GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data").GetProperty("schema");
        if (schema.TryGetProperty("$ref", out var reference))
        {
            schema = json.RootElement.GetProperty("components").GetProperty("schemas")
                .GetProperty(reference.GetString()!["#/components/schemas/".Length..]);
        }
        var properties = schema.GetProperty("properties");
        foreach (var field in new[]
                 {
                     "firebaseIdToken", "email", "password", "confirmPassword", "companyName",
                     "businessLicenseNo", "taxCode", "contactPerson", "businessAddress",
                     "contactPhone", "businessLicenseDocument", "supportingDocuments", "acceptTerms",
                 })
            properties.TryGetProperty(field, out _).Should().BeTrue(
                $"the form must expose {field}; actual fields: {string.Join(", ", properties.EnumerateObject().Select(x => x.Name))}");
        post.GetProperty("responses").TryGetProperty("201", out _).Should().BeTrue();
        if (post.TryGetProperty("security", out var security))
            security.GetArrayLength().Should().Be(0);
    }
}