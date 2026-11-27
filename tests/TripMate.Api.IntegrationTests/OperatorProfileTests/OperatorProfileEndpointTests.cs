using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Media;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.OperatorProfileTests;

[Collection(nameof(TripMateApiFactory))]
public sealed class OperatorProfileEndpointTests
{
    private const string Route = "/api/v1/operator/profile";
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];

    private sealed class LogoStorageStub : IOperatorLogoStorage
    {
        private int allocations;

        public int Uploads { get; private set; }
        public List<string> Deletes { get; } = [];
        public bool FailUpload { get; set; }

        public string AllocatePublicId(long operatorUserId) =>
            $"tripmate/operator-logos/{operatorUserId}/stub{++allocations}";

        public Task<OperatorLogoUploadResult> UploadAsync(OperatorLogoUpload upload, CancellationToken cancellationToken)
        {
            Uploads++;
            return Task.FromResult(FailUpload
                ? OperatorLogoUploadResult.Failed("OPERATOR_LOGO_STORAGE_UNAVAILABLE")
                : OperatorLogoUploadResult.Succeeded(new Uri($"https://cdn.example.test/{upload.PublicId}.png")));
        }

        public Task<bool> DeleteAsync(string publicId, CancellationToken cancellationToken)
        {
            Deletes.Add(publicId);
            return Task.FromResult(true);
        }
    }

    private static TripMateApiFactory Factory(LogoStorageStub storage) => new(
        configureTestServices: services =>
        {
            services.RemoveAll<IOperatorLogoStorage>();
            services.AddSingleton<IOperatorLogoStorage>(storage);
        });

    private static async Task<long> SeedOperatorAsync(
        TripMateApiFactory factory,
        string email = "owner@suntours.vn",
        AccountStatus account = AccountStatus.Active,
        OperatorApprovalStatus approval = OperatorApprovalStatus.Approved,
        string taxCode = "0312345678",
        string licenseNo = "79-1234/2026/TCDL-GPLHQT")
    {
        long userId = 0;
        await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Email = email,
                FullName = "Owner",
                PasswordHash = "hashed",
                Role = UserRole.TourOperator,
                Status = account,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = user.Id,
                CompanyName = "Sun Tours",
                TaxCode = taxCode,
                BusinessLicenseNo = licenseNo,
                CommissionRate = 12.5m,
                ApprovalStatus = approval,
                ReviewedBy = 1,
                ReviewedAtUtc = DateTimeOffset.UtcNow.AddDays(-3),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-3),
            });
            await db.SaveChangesAsync();
            return true;
        });
        return userId;
    }

    private static MultipartFormDataContent Form(
        string? businessName = "Moon Tours",
        string? description = "Night tours",
        string? address = "2 River St",
        string? phone = "0911111111",
        string? email = "hello@moon.vn",
        string? website = "https://moon.vn",
        byte[]? logo = null,
        string logoContentType = "image/png",
        string? removeLogo = null)
    {
        var form = new MultipartFormDataContent();
        if (businessName is not null) form.Add(new StringContent(businessName), "businessName");
        if (description is not null) form.Add(new StringContent(description), "businessDescription");
        if (address is not null) form.Add(new StringContent(address), "businessAddress");
        if (phone is not null) form.Add(new StringContent(phone), "contactPhone");
        if (email is not null) form.Add(new StringContent(email), "contactEmail");
        if (website is not null) form.Add(new StringContent(website), "website");
        if (removeLogo is not null) form.Add(new StringContent(removeLogo), "removeLogo");
        if (logo is not null)
        {
            var content = new ByteArrayContent(logo);
            content.Headers.ContentType = new MediaTypeHeaderValue(logoContentType);
            form.Add(content, "logo", "logo.bin");
        }

        return form;
    }

    // ---- authorization -------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_IsUnauthorized_ForGetAndPut()
    {
        using var factory = Factory(new LogoStorageStub());
        using var client = factory.CreateClient();
        using var form = Form();

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PutAsync(Route, form)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.ServiceProvider)]
    public async Task OtherRoles_AreForbidden_ForGetAndPut(UserRole role)
    {
        using var factory = Factory(new LogoStorageStub());
        using var client = factory.CreateAuthenticatedClient(1234, role);
        using var form = Form();

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsync(Route, form)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.PendingApproval)]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.Rejected)]
    [InlineData(AccountStatus.Locked, OperatorApprovalStatus.Approved)]
    [InlineData(AccountStatus.PendingContractSignature, OperatorApprovalStatus.Approved)]
    public async Task OperatorsThatAreNotApprovedAndActive_AreForbiddenWithTheAccessCode_AndNothingChanges(
        AccountStatus account, OperatorApprovalStatus approval)
    {
        var storage = new LogoStorageStub();
        using var factory = Factory(storage);
        long userId = await SeedOperatorAsync(factory, account: account, approval: approval);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(businessName: "Hacked", logo: Png);

        var get = await client.GetAsync(Route);
        var put = await client.PutAsync(Route, form);

        get.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString()
            .Should().Be("operator_tour.access_required");
        storage.Uploads.Should().Be(0);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.OperatorProfiles.SingleAsync(p => p.UserId == userId)).CompanyName.Should().Be("Sun Tours");
            return true;
        });
    }

    // ---- GET -----------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheProfileInTheFrontEndShape_WithoutUnsetOptionalUrls()
    {
        using var factory = Factory(new LogoStorageStub());
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);

        var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("userId").GetInt64().Should().Be(userId);
        json.GetProperty("businessName").GetString().Should().Be("Sun Tours");
        json.GetProperty("businessDescription").GetString().Should().BeEmpty();
        json.GetProperty("businessAddress").GetString().Should().BeEmpty();
        json.GetProperty("contactPhone").GetString().Should().BeEmpty();
        json.GetProperty("contactEmail").GetString().Should().BeEmpty();
        json.GetProperty("businessLicenceNumber").GetString().Should().Be("79-1234/2026/TCDL-GPLHQT");
        json.GetProperty("taxCode").GetString().Should().Be("0312345678");
        json.GetProperty("approvalStatus").GetString().Should().Be("Approved");
        json.GetProperty("accountEmail").GetString().Should().Be("owner@suntours.vn");
        json.TryGetProperty("website", out _).Should().BeFalse("unset website is omitted");
        json.TryGetProperty("logoUrl", out _).Should().BeFalse("unset logo is omitted");
        json.TryGetProperty("commissionRate", out _).Should().BeFalse();
    }

    // ---- PUT happy path ------------------------------------------------------------------------

    [Fact]
    public async Task Put_UpdatesEditableFields_AndReturnsCodeMessageAndProfile()
    {
        using var factory = Factory(new LogoStorageStub());
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form();

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("code").GetString().Should().Be("operator_profile.updated");
        json.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        var profile = json.GetProperty("profile");
        profile.GetProperty("businessName").GetString().Should().Be("Moon Tours");
        profile.GetProperty("businessDescription").GetString().Should().Be("Night tours");
        profile.GetProperty("businessAddress").GetString().Should().Be("2 River St");
        profile.GetProperty("contactPhone").GetString().Should().Be("0911111111");
        profile.GetProperty("contactEmail").GetString().Should().Be("hello@moon.vn");
        profile.GetProperty("website").GetString().Should().Be("https://moon.vn");

        (await client.GetFromJsonAsync<JsonElement>(Route)).GetProperty("businessName").GetString()
            .Should().Be("Moon Tours");
    }

    [Fact]
    public async Task Put_IgnoresLegalApprovalCommissionAndAccountEmailFormFields()
    {
        using var factory = Factory(new LogoStorageStub());
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form();
        form.Add(new StringContent("0999999999"), "taxCode");
        form.Add(new StringContent("11-1/2026/TCDL-GPLHQT"), "businessLicenseNo");
        form.Add(new StringContent("11-1/2026/TCDL-GPLHQT"), "businessLicenceNumber");
        form.Add(new StringContent("hacker@evil.test"), "accountEmail");
        form.Add(new StringContent("hacker@evil.test"), "email");
        form.Add(new StringContent("Rejected"), "approvalStatus");
        form.Add(new StringContent("0"), "commissionRate");
        form.Add(new StringContent("Hacker"), "contactPerson");

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            var profile = await db.OperatorProfiles.Include(p => p.User).SingleAsync(p => p.UserId == userId);
            profile.TaxCode.Should().Be("0312345678");
            profile.BusinessLicenseNo.Should().Be("79-1234/2026/TCDL-GPLHQT");
            profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Approved);
            profile.CommissionRate.Should().Be(12.5m);
            profile.User.Email.Should().Be("owner@suntours.vn");
            profile.User.FullName.Should().Be("Owner");
            return true;
        });
    }

    // ---- PUT validation (spec 4.1) -------------------------------------------------------------

    [Fact]
    public async Task Put_InvalidFields_Returns400ProblemWithCamelCaseFieldCodeArrays()
    {
        var storage = new LogoStorageStub();
        using var factory = Factory(storage);
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(businessName: "", email: "bad", phone: "123", website: "nope",
            logo: Png, logoContentType: "image/gif");

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetInt32().Should().Be(400);
        json.GetProperty("errorCode").GetString().Should().Be("operator_profile.request_invalid");
        var errors = json.GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "businessName", "contactEmail", "contactPhone", "website", "logo");
        errors.GetProperty("businessName").EnumerateArray().Select(e => e.GetString()).Should().Equal("MSG01");
        errors.GetProperty("contactEmail").EnumerateArray().Select(e => e.GetString()).Should().Equal("MSG02");
        errors.GetProperty("contactPhone").EnumerateArray().Select(e => e.GetString()).Should().Equal("MSG04");
        errors.GetProperty("website").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("operator_profile.request_invalid");
        errors.GetProperty("logo").EnumerateArray().Select(e => e.GetString()).Should().Equal("MSG19");
        storage.Uploads.Should().Be(0);
    }

    [Fact]
    public async Task Put_OnlyTheFailingFieldsAppearInTheErrorsDictionary()
    {
        using var factory = Factory(new LogoStorageStub());
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(email: "bad");

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).Should().Equal("contactEmail");
    }

    [Fact]
    public async Task Put_MissingBusinessName_IsMsg01()
    {
        using var factory = Factory(new LogoStorageStub());
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(businessName: null);

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")
            .GetProperty("businessName").EnumerateArray().Select(e => e.GetString()).Should().Equal("MSG01");
    }

    [Fact]
    public async Task Put_LogoLargerThanFiveMegabytes_IsMsg19_WithoutUploading()
    {
        var storage = new LogoStorageStub();
        using var factory = Factory(storage);
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        byte[] big = new byte[(5 * 1024 * 1024) + 1];
        Png.CopyTo(big, 0);
        using var form = Form(logo: big);

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")
            .GetProperty("logo").EnumerateArray().Select(e => e.GetString()).Should().Equal("MSG19");
        storage.Uploads.Should().Be(0);
    }

    [Fact]
    public async Task Put_LogoWhoseBytesAreNotTheDeclaredImageType_IsMsg19()
    {
        var storage = new LogoStorageStub();
        using var factory = Factory(storage);
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(logo: "<svg onload=alert(1)>"u8.ToArray(), logoContentType: "image/png");

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        storage.Uploads.Should().Be(0);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    public async Task Put_UnparsableRemoveLogo_IsRequestInvalidUnderRemoveLogo(string value)
    {
        using var factory = Factory(new LogoStorageStub());
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(removeLogo: value);

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("errorCode").GetString().Should().Be("operator_profile.request_invalid");
        json.GetProperty("errors").GetProperty("removeLogo").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("operator_profile.request_invalid");
    }

    // ---- logo lifecycle (spec 4.2, 6) ----------------------------------------------------------

    [Fact]
    public async Task Put_UploadReplaceAndRemoveLogo_FollowsTheSpec()
    {
        var storage = new LogoStorageStub();
        using var factory = Factory(storage);
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);

        using var first = Form(logo: Png);
        var uploaded = await (await client.PutAsync(Route, first)).Content.ReadFromJsonAsync<JsonElement>();
        string firstUrl = uploaded.GetProperty("profile").GetProperty("logoUrl").GetString()!;
        firstUrl.Should().StartWith("https://cdn.example.test/tripmate/operator-logos/");
        (await client.GetFromJsonAsync<JsonElement>(Route)).GetProperty("logoUrl").GetString().Should().Be(firstUrl);

        using var second = Form(logo: Png);
        var replaced = await (await client.PutAsync(Route, second)).Content.ReadFromJsonAsync<JsonElement>();
        replaced.GetProperty("profile").GetProperty("logoUrl").GetString().Should().NotBe(firstUrl);
        storage.Deletes.Should().ContainSingle().Which.Should().EndWith("/stub1");

        using var remove = Form(removeLogo: "true");
        var removed = await client.PutAsync(Route, remove);
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile")
            .TryGetProperty("logoUrl", out _).Should().BeFalse();
        storage.Deletes.Should().HaveCount(2);

        int deletesBefore = storage.Deletes.Count;
        using var removeAgain = Form(removeLogo: "true");
        (await client.PutAsync(Route, removeAgain)).StatusCode.Should().Be(HttpStatusCode.OK);
        storage.Deletes.Should().HaveCount(deletesBefore, "removing a logo that does not exist is a no-op");
        storage.Uploads.Should().Be(2);
    }

    [Fact]
    public async Task Put_StorageUnavailable_Returns503_AndSavesNothing()
    {
        var storage = new LogoStorageStub { FailUpload = true };
        using var factory = Factory(storage);
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        using var form = Form(businessName: "Should Not Save", logo: Png);

        var response = await client.PutAsync(Route, form);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString()
            .Should().Be("operator_profile.unavailable");
        await factory.WithDbContextAsync(async db =>
        {
            (await db.OperatorProfiles.SingleAsync(p => p.UserId == userId)).CompanyName.Should().Be("Sun Tours");
            return true;
        });
    }

    // ---- request size (probe: what does the host do above the 6 MB limit?) ---------------------

    [Fact]
    public async Task Put_RequestBodyAboveTheSixMegabyteLimit_IsRejectedBeforeTheHandlerRuns()
    {
        var storage = new LogoStorageStub();
        using var factory = Factory(storage);
        long userId = await SeedOperatorAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.TourOperator);
        byte[] huge = new byte[7 * 1024 * 1024];
        Png.CopyTo(huge, 0);
        using var form = Form(businessName: "Should Not Save", logo: huge);

        HttpStatusCode status;
        try
        {
            status = (await client.PutAsync(Route, form)).StatusCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or InvalidDataException)
        {
            status = HttpStatusCode.RequestEntityTooLarge;
        }

        Console.WriteLine($"PROBE oversized request status: {(int)status} {status}");
        status.Should().NotBe(HttpStatusCode.OK);
        storage.Uploads.Should().Be(0);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.OperatorProfiles.SingleAsync(p => p.UserId == userId)).CompanyName.Should().Be("Sun Tours");
            return true;
        });
    }
}