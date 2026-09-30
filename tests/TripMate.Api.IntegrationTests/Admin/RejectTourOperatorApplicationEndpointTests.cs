using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Admin;

[Collection(nameof(TripMateApiFactory))]
public class RejectTourOperatorApplicationEndpointTests
{
    private const string RejectionReason = "Business license details could not be verified.";

    [Fact]
    public async Task Post_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/tour-operator-applications/1/reject",
            new { reason = RejectionReason });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithNonAdministratorRole_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/tour-operator-applications/1/reject",
            new { reason = RejectionReason });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_WithMissingReason_ReturnsUnprocessableEntity(string reason)
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedPendingApplicationAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.AdminId, UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/tour-operator-applications/{seed.OperatorId}/reject",
            new { reason });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Post_WithReasonOverDatabaseLimit_ReturnsUnprocessableEntity()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedPendingApplicationAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.AdminId, UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/tour-operator-applications/{seed.OperatorId}/reject",
            new { reason = new string('x', OperatorProfile.RejectionReasonMaxLength + 1) });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Post_WhenApplicationDoesNotExist_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/tour-operator-applications/999999/reject",
            new { reason = RejectionReason });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_WhenApplicationIsNoLongerPending_ReturnsConflict()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedPendingApplicationAsync(factory, AccountStatus.Active, OperatorApprovalStatus.Approved);
        using var client = factory.CreateAuthenticatedClient(seed.AdminId, UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/tour-operator-applications/{seed.OperatorId}/reject",
            new { reason = RejectionReason });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Post_WithExactlyFiveHundredCharacters_RejectsApplicationAtomically()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedPendingApplicationAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.AdminId, UserRole.Administrator);
        var reason = new string('x', OperatorProfile.RejectionReasonMaxLength);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/tour-operator-applications/{seed.OperatorId}/reject",
            new { reason });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var persisted = await factory.WithDbContextAsync(async dbContext =>
        {
            var user = await dbContext.Users.AsNoTracking().SingleAsync(item => item.Id == seed.OperatorId);
            var profile = await dbContext.OperatorProfiles.AsNoTracking().SingleAsync(item => item.UserId == seed.OperatorId);
            var document = await dbContext.OperatorDocuments.AsNoTracking().SingleAsync(item => item.OperatorUserId == seed.OperatorId);
            var audit = await dbContext.AuditLogs.AsNoTracking().SingleAsync(item =>
                item.ActionType == AuditActionTypes.OperatorApplicationReject &&
                item.AffectedEntityId == seed.OperatorId);
            var notification = await dbContext.Notifications.AsNoTracking().SingleAsync(item =>
                item.UserId == seed.OperatorId && item.Type == "OperatorApplicationRejected");
            return (user, profile, document, audit, notification);
        });

        persisted.user.Status.Should().Be(AccountStatus.Rejected);
        persisted.profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Rejected);
        persisted.profile.RejectionReason.Should().Be(reason);
        persisted.profile.ReviewedBy.Should().Be(seed.AdminId);
        persisted.document.Status.Should().Be(DocumentStatus.Rejected);
        persisted.audit.Result.Should().Be(AuditOutcome.Success);
        persisted.notification.Status.Should().Be(NotificationStatus.Pending);
        persisted.notification.Channel.Should().Be(NotificationChannel.Email);
    }

    private static Task<SeedResult> SeedPendingApplicationAsync(
        TripMateApiFactory factory,
        AccountStatus accountStatus = AccountStatus.PendingApproval,
        OperatorApprovalStatus approvalStatus = OperatorApprovalStatus.PendingApproval) =>
        factory.WithDbContextAsync(async dbContext =>
        {
            var now = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
            var admin = new User
            {
                Role = UserRole.Administrator,
                Email = $"admin-{Guid.NewGuid():N}@tripmate.local",
                FullName = "UC-51 Administrator",
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            var tourOperator = new User
            {
                Role = UserRole.TourOperator,
                Email = $"operator-{Guid.NewGuid():N}@tripmate.local",
                FullName = "UC-51 Tour Operator",
                Status = accountStatus,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            dbContext.Users.AddRange(admin, tourOperator);
            await dbContext.SaveChangesAsync();

            dbContext.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = tourOperator.Id,
                CompanyName = "UC-51 Test Operator",
                TaxCode = "0401234567",
                BusinessLicenseNo = "BL-UC51-001",
                ApprovalStatus = approvalStatus,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            dbContext.OperatorDocuments.Add(new OperatorDocument
            {
                OperatorUserId = tourOperator.Id,
                DocumentType = OperatorDocumentType.BusinessLicense,
                FileUrl = "https://storage.tripmate.local/uc51/business-license.pdf",
                Status = DocumentStatus.Submitted,
                UploadedAtUtc = now,
            });
            await dbContext.SaveChangesAsync();

            return new SeedResult(admin.Id, tourOperator.Id);
        });

    private sealed record SeedResult(long AdminId, long OperatorId);
}