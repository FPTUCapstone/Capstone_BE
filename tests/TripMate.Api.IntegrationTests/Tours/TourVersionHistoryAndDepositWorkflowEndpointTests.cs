using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

/// <summary>
/// Full HTTP end-to-end workflow test covering:
/// 1) Operator creating a tour with Deposit setup and TravellerRequirements checklist,
/// 2) Staff reviewing Deposit + Requirements and approving v1,
/// 3) Traveler inspecting public v1 detail,
/// 4) Operator cloning v2 with updated Deposit settings and getting v2 approved (superseding v1),
/// 5) Operator navigating clickable Version History across v2 and archived v1.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class TourVersionHistoryAndDepositWorkflowEndpointTests
{
    private static readonly TourReviewChecklistDto ValidChecklist = new(
        ContentCompleteness: true,
        ImageAppropriateness: true,
        PricePlausibility: true,
        PolicyCompliance: true,
        ItineraryFeasibility: true);

    [Fact]
    public async Task EndToEndWorkflow_DepositSetup_PackingNotes_And_VersionHistory_AcrossRoles()
    {
        await using var factory = new TripMateApiFactory();
        var (operatorId, staffId, poiId, destinationId, policyId) = await SeedWorkflowActorsAsync(factory);

        using var opClient = factory.CreateAuthenticatedClient(operatorId, UserRole.TourOperator);
        using var staffClient = factory.CreateAuthenticatedClient(staffId, UserRole.Staff);
        using var guestClient = factory.CreateClient();

        // Step 1: Operator creates v1 Draft with Deposit (30%, due 7 days before departure) + checklist notes
        var createV1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/operator/tours")
        {
            Content = JsonContent.Create(BuildTourRequest(
                "Tour Đà Nẵng - Bà Nà v1",
                poiId,
                destinationId,
                policyId,
                allowDeposit: true,
                depositPercent: 30m,
                balanceDueDaysBeforeDeparture: 7,
                travellerRequirements: "- Giày thể thao\n- Áo mưa / Dù che nắng")),
        };
        createV1.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var createV1Res = await opClient.SendAsync(createV1);
        createV1Res.StatusCode.Should().Be(HttpStatusCode.Created);
        long v1Id = JsonDocument.Parse(await createV1Res.Content.ReadAsStringAsync())
            .RootElement.GetProperty("tourId").GetInt64();

        // Step 2: Operator updates draft deposit to 35% / 10 days before submit
        using var updateV1Res = await opClient.PutAsJsonAsync(
            $"/api/v1/operator/tours/{v1Id}",
            BuildTourRequest(
                "Tour Đà Nẵng - Bà Nà v1",
                poiId,
                destinationId,
                policyId,
                allowDeposit: true,
                depositPercent: 35m,
                balanceDueDaysBeforeDeparture: 10,
                travellerRequirements: "- Giày thể thao\n- Áo mưa / Dù che nắng\n- CCCD / Hộ chiếu"));
        updateV1Res.StatusCode.Should().Be(HttpStatusCode.OK);

        await AttachPrimaryImageAsync(factory, v1Id);

        // Step 3: Submit v1 for review -> Staff inspects review detail -> Staff approves v1
        using var submitV1Res = await opClient.PostAsJsonAsync($"/api/v1/operator/tours/{v1Id}/submit-review", new { submissionNote = "V1 ready" });
        submitV1Res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var staffReviewRes = await staffClient.GetAsync($"/api/v1/admin/tours/{v1Id}/review");
        staffReviewRes.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var reviewJson = JsonDocument.Parse(await staffReviewRes.Content.ReadAsStringAsync()))
        {
            var r = reviewJson.RootElement;
            r.GetProperty("allowDeposit").GetBoolean().Should().BeTrue();
            r.GetProperty("depositPercent").GetDecimal().Should().Be(35m);
            r.GetProperty("balanceDueDaysBeforeDeparture").GetInt32().Should().Be(10);
            r.GetProperty("travellerRequirements").GetString().Should().Contain("CCCD / Hộ chiếu");
        }

        using var approveV1Res = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{v1Id}/approve",
            new { checklist = ValidChecklist, note = "Approved v1" });
        approveV1Res.StatusCode.Should().Be(HttpStatusCode.OK);

        // Step 4: Traveler inspects published v1 detail
        using var publicV1Res = await guestClient.GetAsync($"/api/v1/tours/{v1Id}");
        publicV1Res.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var pubJson = JsonDocument.Parse(await publicV1Res.Content.ReadAsStringAsync()))
        {
            var p = pubJson.RootElement;
            p.GetProperty("allowDeposit").GetBoolean().Should().BeTrue();
            p.GetProperty("depositPercent").GetDecimal().Should().Be(35m);
            p.GetProperty("balanceDueDaysBeforeDeparture").GetInt32().Should().Be(10);
            p.GetProperty("travellerRequirements").GetString().Should().Contain("Giày thể thao");
        }

        // Step 5: Operator clones v1 -> v2 Draft with updated deposit (50%, due 14 days), attaches photo, submits, Staff approves v2
        var createV2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/operator/tours")
        {
            Content = JsonContent.Create(BuildTourRequest(
                "Tour Đà Nẵng - Bà Nà v2 (Cập nhật cọc)",
                poiId,
                destinationId,
                policyId,
                allowDeposit: true,
                depositPercent: 50m,
                balanceDueDaysBeforeDeparture: 14,
                travellerRequirements: "- Giày thể thao\n- Đồ bơi & khăn tắm",
                sourceTourId: v1Id)),
        };
        createV2.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var createV2Res = await opClient.SendAsync(createV2);
        createV2Res.StatusCode.Should().Be(HttpStatusCode.Created);
        using var v2CreatedJson = JsonDocument.Parse(await createV2Res.Content.ReadAsStringAsync());
        long v2Id = v2CreatedJson.RootElement.GetProperty("tourId").GetInt64();
        v2CreatedJson.RootElement.GetProperty("versionNo").GetInt32().Should().Be(2);

        await AttachPrimaryImageAsync(factory, v2Id);
        using var submitV2Res = await opClient.PostAsJsonAsync($"/api/v1/operator/tours/{v2Id}/submit-review", new { submissionNote = "V2 ready" });
        submitV2Res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var approveV2Res = await staffClient.PostAsJsonAsync(
            $"/api/v1/admin/tours/{v2Id}/approve",
            new { checklist = ValidChecklist, note = "Approved v2" });
        approveV2Res.StatusCode.Should().Be(HttpStatusCode.OK);

        // Step 6: Operator opens v2 & clicks through Version History to inspect archived v1
        using var opV2Res = await opClient.GetAsync($"/api/v1/operator/tours/{v2Id}");
        opV2Res.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var opV2Json = JsonDocument.Parse(await opV2Res.Content.ReadAsStringAsync()))
        {
            var d = opV2Json.RootElement;
            d.GetProperty("versionNo").GetInt32().Should().Be(2);
            d.GetProperty("status").GetString().Should().Be("Approved");
            d.GetProperty("depositPercent").GetDecimal().Should().Be(50m);
            d.GetProperty("balanceDueDaysBeforeDeparture").GetInt32().Should().Be(14);
            var versions = d.GetProperty("versions").EnumerateArray().ToList();
            versions.Should().HaveCount(2);
            versions[0].GetProperty("tourId").GetInt64().Should().Be(v2Id);
            versions[0].GetProperty("versionNo").GetInt32().Should().Be(2);
            versions[0].GetProperty("status").GetString().Should().Be("Approved");
            versions[1].GetProperty("tourId").GetInt64().Should().Be(v1Id);
            versions[1].GetProperty("versionNo").GetInt32().Should().Be(1);
            versions[1].GetProperty("status").GetString().Should().Be("Inactive");
        }

        using var opV1ArchivedRes = await opClient.GetAsync($"/api/v1/operator/tours/{v1Id}");
        opV1ArchivedRes.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var opV1Json = JsonDocument.Parse(await opV1ArchivedRes.Content.ReadAsStringAsync()))
        {
            var d = opV1Json.RootElement;
            d.GetProperty("versionNo").GetInt32().Should().Be(1);
            d.GetProperty("status").GetString().Should().Be("Inactive");
            d.GetProperty("depositPercent").GetDecimal().Should().Be(35m);
            d.GetProperty("balanceDueDaysBeforeDeparture").GetInt32().Should().Be(10);
            d.GetProperty("versions").GetArrayLength().Should().Be(2);
        }

        // Step 7: Public v1 is superseded (404) while v2 is active (200 with updated 50%/14d deposit)
        using var publicV1AfterRes = await guestClient.GetAsync($"/api/v1/tours/{v1Id}");
        publicV1AfterRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var publicV2Res = await guestClient.GetAsync($"/api/v1/tours/{v2Id}");
        publicV2Res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var pubV2Json = JsonDocument.Parse(await publicV2Res.Content.ReadAsStringAsync());
        pubV2Json.RootElement.GetProperty("depositPercent").GetDecimal().Should().Be(50m);
        pubV2Json.RootElement.GetProperty("balanceDueDaysBeforeDeparture").GetInt32().Should().Be(14);
    }

    private static object BuildTourRequest(
        string title,
        long poiId,
        long destinationId,
        long policyId,
        bool allowDeposit,
        decimal? depositPercent,
        int? balanceDueDaysBeforeDeparture,
        string travellerRequirements,
        long? sourceTourId = null) => new
    {
        title,
        description = "Hành trình trải nghiệm đầy đủ các điểm nổi bật và lưu ý chuẩn bị.",
        basePrice = 3_500_000m,
        durationDays = 2,
        destinationIds = new[] { destinationId },
        cancellationPolicyTemplateId = policyId,
        travellerRequirements,
        allowDeposit,
        depositPercent,
        balanceDueDaysBeforeDeparture,
        sourceTourId,
        itineraryItems = new[] { new { dayNo = 1, sequenceNo = 1, poiId, stayDurationMinutes = 90 } },
        schedules = new[]
        {
            new
            {
                startAtUtc = DateTimeOffset.UtcNow.AddDays(20),
                endAtUtc = DateTimeOffset.UtcNow.AddDays(22),
                totalCapacity = 20,
                minimumCapacity = 2,
                adultPrice = 3_500_000m,
            },
        },
    };

    private static async Task AttachPrimaryImageAsync(TripMateApiFactory factory, long tourId)
    {
        await factory.WithDbContextAsync(async db =>
        {
            var tour = await db.Tours.SingleAsync(t => t.Id == tourId);
            db.TourMedia.Add(TourMedia.Create(
                tour,
                $"tours/{tourId}/cover",
                $"https://res.cloudinary.com/demo/image/upload/v1/tours/{tourId}.jpg",
                "Ảnh đại diện tour",
                "Tour cover",
                1,
                isPrimary: true,
                DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            return true;
        });
    }

    private static Task<(long OperatorId, long StaffId, long PoiId, long DestinationId, long PolicyId)> SeedWorkflowActorsAsync(
        TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async db =>
        {
            const long operatorId = 710;
            const long staffId = 711;
            var opUser = new User
            {
                Id = operatorId,
                Email = "workflow-op@tripmate.test",
                FullName = "Workflow Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
            };
            var staffUser = new User
            {
                Id = staffId,
                Email = "workflow-staff@tripmate.test",
                FullName = "Workflow Staff",
                Role = UserRole.Staff,
                Status = AccountStatus.Active,
            };
            db.Users.AddRange(opUser, staffUser);
            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = operatorId,
                User = opUser,
                CompanyName = "Workflow Tours",
                TaxCode = "TAX710",
                BusinessLicenseNo = "LIC710",
                ApprovalStatus = OperatorApprovalStatus.Approved,
            });
            var category = PoiCategory.Create("Workflow Category", null);
            db.PoiCategories.Add(category);
            var poi = PointOfInterest.Create(category, "Cầu Rồng", 16.061m, 108.227m, operatorId, DateTimeOffset.UtcNow);
            db.PointsOfInterest.Add(poi);
            var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
            typeof(Destination).GetProperty(nameof(Destination.Name))!.SetValue(destination, "Đà Nẵng");
            db.Destinations.Add(destination);
            var policy = CancellationPolicyTemplate.Create(
                "Chính sách tiêu chuẩn",
                null,
                DateTimeOffset.UtcNow,
                [CancellationPolicyTemplateRule.Create(15, 25), CancellationPolicyTemplateRule.Create(0, 100)]);
            db.CancellationPolicyTemplates.Add(policy);
            await db.SaveChangesAsync();
            return (operatorId, staffId, poi.Id, destination.Id, policy.Id);
        });
}
