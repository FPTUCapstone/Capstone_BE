using System.Net;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Authentication;

[Collection(nameof(TripMateApiFactory))]
public sealed class OperatorContractWorkspaceAuthorizationTests
{
    [Fact]
    public async Task Persisted_pending_signature_blocks_old_JWT_from_business_but_preserves_application_access()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer,
            configurationOverrides: new Dictionary<string, string?> { ["OperatorContracts:Enabled"] = "true" });
        var user = new User
        {
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            FullName = "Contract test",
            Email = "contract-policy@example.invalid",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
            EmailVerifiedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var token = await factory.IssueTokenForPersistedUserAsync(user);
        await factory.WithDbContextAsync(async db =>
        {
            var persisted = await db.Users.FindAsync(user.Id);
            persisted!.Status = AccountStatus.PendingContractSignature;
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = user.Id,
                CompanyName = "Contract company",
                TaxCode = "0000000000",
                BusinessLicenseNo = "TEST-LIC",
                ApprovalStatus = OperatorApprovalStatus.Approved
            });
            await db.SaveChangesAsync(); return true;
        });
        using var client = factory.CreateJwtClient(token);
        foreach (var path in new[] { "/api/v1/operator/post-trip-complaints", "/api/v1/operator/refunds", "/api/v1/operator/tours" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/operator/application")).StatusCode);
    }

    [Fact]
    public async Task Legacy_exemption_only_allows_an_active_approved_persisted_account()
    {
        await using var factory = new TripMateApiFactory(configurationOverrides:
            new Dictionary<string, string?> { ["OperatorContracts:Enabled"] = "true" });
        var id = await factory.WithDbContextAsync(async db =>
        {
            var user = new User
            {
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
                FullName = "Legacy contract test",
                Email = "legacy-contract-policy@example.invalid"
            };
            db.Users.Add(user); db.OperatorProfiles.Add(new OperatorProfile
            {
                User = user,
                CompanyName = "Legacy company",
                TaxCode = "0000000001",
                BusinessLicenseNo = "LEGACY-LIC",
                ApprovalStatus = OperatorApprovalStatus.Approved
            });
            await db.SaveChangesAsync(); return user.Id;
        });
        using var client = factory.CreateAuthenticatedClient(id, UserRole.TourOperator);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/operator/post-trip-complaints")).StatusCode);
        await factory.WithDbContextAsync(async db =>
        {
            db.OperatorContractLegacyExemptions.Add(OperatorContractLegacyExemption.Create(id, DateTimeOffset.UtcNow, "cutover"));
            await db.SaveChangesAsync(); return true;
        });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/operator/post-trip-complaints")).StatusCode);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.FindAsync(id))!.Status = AccountStatus.PendingContractSignature;
            await db.SaveChangesAsync(); return true;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/operator/post-trip-complaints")).StatusCode);
    }
}