using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class SchedulingRequestTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithValidOperation_StoresPendingRequestAndRestPreference()
    {
        var requestedAt = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        var operationKey = Guid.Parse("c84467b4-ea99-4be4-a281-4f9cd2d7c741");

        var request = SchedulingRequest.Create(
            travelerUserId: 42,
            operationKey,
            requestHash: "a".PadLeft(64, 'a'),
            startAtUtc: requestedAt,
            timeZoneId: "Asia/Ho_Chi_Minh",
            startLatitude: 16.0544m,
            startLongitude: 108.2022m,
            explorationLatitude: 16.0471m,
            explorationLongitude: 108.2068m,
            endPointOfInterestId: null,
            returnToStart: true,
            availableMinutes: 480,
            transportMode: TransportMode.Motorbike,
            searchRadiusKm: 10m,
            budgetVnd: 800_000m,
            mandatoryPoiIdsJson: "[12,28]",
            restPreference: RestPreference.Auto,
            requestedAtUtc: requestedAt);

        request.TravelerUserId.Should().Be(42);
        request.IdempotencyKey.Should().Be(operationKey);
        request.Status.Should().Be(SchedulingRequestStatus.Pending);
        request.RestPreference.Should().Be(RestPreference.Auto);
        request.TimeZoneId.Should().Be("Asia/Ho_Chi_Minh");
    }

    [Fact]
    public void Create_WithBlankTimeZone_Throws()
    {
        var action = () => SchedulingRequest.Create(
            travelerUserId: 42,
            Guid.NewGuid(),
            requestHash: new string('a', 64),
            startAtUtc: DateTimeOffset.UtcNow,
            timeZoneId: " ",
            startLatitude: 16.0544m,
            startLongitude: 108.2022m,
            explorationLatitude: 16.0471m,
            explorationLongitude: 108.2068m,
            endPointOfInterestId: null,
            returnToStart: true,
            availableMinutes: 480,
            transportMode: TransportMode.Motorbike,
            searchRadiusKm: 10m,
            budgetVnd: null,
            mandatoryPoiIdsJson: "[]",
            restPreference: RestPreference.Auto,
            requestedAtUtc: DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ClaimGeneration_FromPending_AssignsOwnerLeaseAndFirstAttempt()
    {
        var request = CreateRequest();
        var ownerId = Guid.Parse("a68a5759-0c2d-4bad-8a5d-af25b2e03de2");

        request.ClaimGeneration(ownerId, Now.AddMinutes(1), Now);

        request.Status.Should().Be(SchedulingRequestStatus.Processing);
        request.GenerationOwnerId.Should().Be(ownerId);
        request.GenerationLeaseExpiresAtUtc.Should().Be(Now.AddMinutes(1));
        request.GenerationAttempt.Should().Be(1);
    }

    [Fact]
    public void ClaimGeneration_BeforeCurrentLeaseExpires_RejectsTakeover()
    {
        var request = CreateRequest();
        request.ClaimGeneration(Guid.NewGuid(), Now.AddMinutes(1), Now);

        var action = () => request.ClaimGeneration(
            Guid.NewGuid(),
            Now.AddMinutes(2),
            Now.AddSeconds(59));

        action.Should().Throw<InvalidOperationException>();
        request.GenerationAttempt.Should().Be(1);
    }

    [Fact]
    public void ClaimGeneration_AfterCurrentLeaseExpires_TransfersOwnershipAndIncrementsAttempt()
    {
        var request = CreateRequest();
        var firstOwnerId = Guid.NewGuid();
        var secondOwnerId = Guid.NewGuid();
        request.ClaimGeneration(firstOwnerId, Now.AddMinutes(1), Now);

        request.ClaimGeneration(secondOwnerId, Now.AddMinutes(2), Now.AddMinutes(1));

        request.GenerationOwnerId.Should().Be(secondOwnerId);
        request.GenerationLeaseExpiresAtUtc.Should().Be(Now.AddMinutes(2));
        request.GenerationAttempt.Should().Be(2);
    }

    [Fact]
    public void CompleteGeneration_WithStaleOwner_RejectsMutation()
    {
        var request = CreateRequest();
        var currentOwnerId = Guid.NewGuid();
        request.ClaimGeneration(currentOwnerId, Now.AddMinutes(1), Now);

        var action = () => request.CompleteGeneration(Guid.NewGuid(), Now.AddSeconds(10));

        action.Should().Throw<InvalidOperationException>();
        request.Status.Should().Be(SchedulingRequestStatus.Processing);
        request.GenerationOwnerId.Should().Be(currentOwnerId);
    }

    [Fact]
    public void CompleteGeneration_WithCurrentOwner_CompletesAndClearsLease()
    {
        var request = CreateRequest();
        var ownerId = Guid.NewGuid();
        request.ClaimGeneration(ownerId, Now.AddMinutes(1), Now);

        request.CompleteGeneration(ownerId, Now.AddSeconds(10));

        request.Status.Should().Be(SchedulingRequestStatus.Completed);
        request.CompletedAtUtc.Should().Be(Now.AddSeconds(10));
        request.GenerationOwnerId.Should().BeNull();
        request.GenerationLeaseExpiresAtUtc.Should().BeNull();
        request.GenerationAttempt.Should().Be(1);
    }

    [Fact]
    public void FailGeneration_WithCurrentOwner_PersistsFailureAndClearsLease()
    {
        var request = CreateRequest();
        var ownerId = Guid.NewGuid();
        request.ClaimGeneration(ownerId, Now.AddMinutes(1), Now);

        request.FailGeneration(ownerId, "planning.constraints_infeasible", "No route.", Now);

        request.Status.Should().Be(SchedulingRequestStatus.Failed);
        request.FailureCode.Should().Be("planning.constraints_infeasible");
        request.FailureMessage.Should().Be("No route.");
        request.GenerationOwnerId.Should().BeNull();
        request.GenerationLeaseExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public void ReleaseGeneration_WithCurrentOwner_ReturnsToPendingAndClearsLease()
    {
        var request = CreateRequest();
        var ownerId = Guid.NewGuid();
        request.ClaimGeneration(ownerId, Now.AddMinutes(1), Now);

        request.ReleaseGeneration(ownerId, Now.AddSeconds(10));

        request.Status.Should().Be(SchedulingRequestStatus.Pending);
        request.GenerationOwnerId.Should().BeNull();
        request.GenerationLeaseExpiresAtUtc.Should().BeNull();
        request.GenerationAttempt.Should().Be(1);
    }

    [Fact]
    public void RenewGenerationLease_WithStaleOwner_RejectsMutation()
    {
        var request = CreateRequest();
        var ownerId = Guid.NewGuid();
        request.ClaimGeneration(ownerId, Now.AddMinutes(1), Now);

        var action = () => request.RenewGenerationLease(
            Guid.NewGuid(),
            Now.AddMinutes(2),
            Now.AddSeconds(10));

        action.Should().Throw<InvalidOperationException>();
        request.GenerationLeaseExpiresAtUtc.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void ReleaseGeneration_AfterLeaseExpires_RejectsMutation()
    {
        var request = CreateRequest();
        var ownerId = Guid.NewGuid();
        request.ClaimGeneration(ownerId, Now.AddMinutes(1), Now);

        var action = () => request.ReleaseGeneration(ownerId, Now.AddMinutes(1));

        action.Should().Throw<InvalidOperationException>();
        request.Status.Should().Be(SchedulingRequestStatus.Processing);
        request.GenerationOwnerId.Should().Be(ownerId);
    }

    [Fact]
    public void FailGeneration_WithStaleOwner_RejectsMutation()
    {
        var request = CreateRequest();
        var ownerId = Guid.NewGuid();
        request.ClaimGeneration(ownerId, Now.AddMinutes(1), Now);

        var action = () => request.FailGeneration(
            Guid.NewGuid(),
            "planning.constraints_infeasible",
            "No route.",
            Now.AddSeconds(10));

        action.Should().Throw<InvalidOperationException>();
        request.Status.Should().Be(SchedulingRequestStatus.Processing);
        request.GenerationOwnerId.Should().Be(ownerId);
    }

    [Fact]
    public void FailGeneration_WithInvalidMessage_DoesNotPartiallyMutateRequest()
    {
        var request = CreateRequest();
        var ownerId = Guid.NewGuid();
        request.ClaimGeneration(ownerId, Now.AddMinutes(1), Now);

        var action = () => request.FailGeneration(
            ownerId,
            "planning.constraints_infeasible",
            " ",
            Now.AddSeconds(10));

        action.Should().Throw<ArgumentException>();
        request.Status.Should().Be(SchedulingRequestStatus.Processing);
        request.CompletedAtUtc.Should().BeNull();
        request.FailureCode.Should().BeNull();
        request.FailureMessage.Should().BeNull();
        request.GenerationOwnerId.Should().Be(ownerId);
    }

    [Fact]
    public void CompletionAndFailure_DoNotExposeOwnerlessPublicOverloads()
    {
        var publicMethods = typeof(SchedulingRequest).GetMethods();

        publicMethods.Should().NotContain(method =>
            method.Name == "Complete" && method.GetParameters().Length == 1);
        publicMethods.Should().NotContain(method =>
            method.Name == "FailInfeasible" && method.GetParameters().Length == 3);
    }

    [Fact]
    public void ClaimGeneration_NormalizesLeaseToUtc()
    {
        var request = CreateRequest();
        var claimedAt = Now.ToOffset(TimeSpan.FromHours(7));
        var leaseExpiresAt = Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(7));

        request.ClaimGeneration(Guid.NewGuid(), leaseExpiresAt, claimedAt);

        request.GenerationLeaseExpiresAtUtc.Should().Be(Now.AddMinutes(1));
        request.GenerationLeaseExpiresAtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void ClaimGeneration_WithInvalidOwnerOrDeadline_Throws(
        bool emptyOwner,
        int leaseOffsetMinutes)
    {
        var request = CreateRequest();
        var ownerId = emptyOwner ? Guid.Empty : Guid.NewGuid();

        var action = () => request.ClaimGeneration(
            ownerId,
            Now.AddMinutes(leaseOffsetMinutes),
            Now);

        action.Should().Throw<ArgumentException>();
    }

    private static SchedulingRequest CreateRequest() => SchedulingRequest.Create(
        travelerUserId: 42,
        operationKey: Guid.NewGuid(),
        requestHash: new string('a', SchedulingRequest.RequestHashLength),
        startAtUtc: Now,
        timeZoneId: "Asia/Ho_Chi_Minh",
        startLatitude: 16.0544m,
        startLongitude: 108.2022m,
        explorationLatitude: 16.0471m,
        explorationLongitude: 108.2068m,
        endPointOfInterestId: null,
        returnToStart: true,
        availableMinutes: 480,
        transportMode: TransportMode.Motorbike,
        searchRadiusKm: 10m,
        budgetVnd: 800_000m,
        mandatoryPoiIdsJson: "[]",
        restPreference: RestPreference.Auto,
        requestedAtUtc: Now);
}