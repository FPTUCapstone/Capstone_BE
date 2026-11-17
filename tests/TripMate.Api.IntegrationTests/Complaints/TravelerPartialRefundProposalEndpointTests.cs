using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Complaints;

[Collection(nameof(TripMateApiFactory))]
public sealed class TravelerPartialRefundProposalEndpointTests
{
    private const long TravelerId = 11;
    private const long ComplaintId = 501;
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_Owner_ReturnsProposal()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);

        var response = await client.GetAsync(Url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PartialRefundProposalResponse>();
        body!.ProposedAmount.Should().Be(500_000m);
        body.CanRespond.Should().BeTrue();
    }

    [Fact]
    public async Task Respond_Accept_ReturnsApprovedAndCreatesRefundRequest()
    {
        await using var factory = CreateFactory(out var refunds);
        using var client = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);

        var response = await client.PostAsJsonAsync($"{Url}/response", new { decision = "Accept" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RespondToPartialRefundProposalResponse>();
        body!.Status.Should().Be(nameof(PostTripComplaintStatus.Approved));
        body.ApprovedAmount.Should().Be(500_000m);
        refunds.Count.Should().Be(1);
    }

    [Fact]
    public async Task Respond_Twice_SecondCallReturnsConflictMsg165()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);
        (await client.PostAsJsonAsync($"{Url}/response", new { decision = "Decline" })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync($"{Url}/response", new { decision = "Accept" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).Should().Be(PartialRefundProposalErrorCodes.ResponseClosed);
    }

    [Fact]
    public async Task Respond_OtherTraveler_ReturnsForbiddenMsg126()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateAuthenticatedClient(TravelerId + 1, UserRole.Traveler);

        var response = await client.PostAsJsonAsync($"{Url}/response", new { decision = "Accept" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be(PartialRefundProposalErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Respond_UnknownDecision_ReturnsBadRequest()
    {
        await using var factory = CreateFactory(out var refunds);
        using var client = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);

        // Only Accept or Decline exist (no counter-offer); undefined numeric values are rejected too.
        foreach (object decision in new object[] { "Counter", 99 })
        {
            var response = await client.PostAsJsonAsync($"{Url}/response", new { decision });
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await client.PostAsJsonAsync($"{Url}/response", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refunds.Count.Should().Be(0);
    }

    [Fact]
    public async Task Respond_TourOperator_IsForbiddenByRole()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateAuthenticatedClient(22, UserRole.TourOperator);

        var response = await client.PostAsJsonAsync($"{Url}/response", new { decision = "Accept" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_BeforeComplaintStoreIsAvailable_ReturnsNotFound()
    {
        // Until TM-222 lands, the temporary store finds no complaint.
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(TravelerId, UserRole.Traveler);

        var response = await client.GetAsync(Url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be(PartialRefundProposalErrorCodes.NotFound);
    }

    private static string Url => $"/api/v1/traveler/complaints/{ComplaintId}/partial-refund-proposal";

    private static TripMateApiFactory CreateFactory(out RefundRequestCounter refunds)
    {
        var counter = new RefundRequestCounter();
        refunds = counter;
        var store = new InMemoryProposalStore(new PartialRefundProposal(ComplaintId, 301, "BK-UC75", TravelerId, 22,
            PostTripComplaintStatus.AwaitingTravelerResponse, 500_000m, Now.AddDays(2)));
        return new TripMateApiFactory(
            dateTimeProviderFactory: _ => new FixedClock(Now),
            configureTestServices: services =>
            {
                services.RemoveAll<IPartialRefundProposalStore>();
                services.AddSingleton<IPartialRefundProposalStore>(store);
                services.RemoveAll<IComplaintRefundRequestCreator>();
                services.AddSingleton<IComplaintRefundRequestCreator>(counter);
            });
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        return problem!.Extensions.TryGetValue("errorCode", out var code) && code is JsonElement element
            ? element.GetString()
            : null;
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RefundRequestCounter : IComplaintRefundRequestCreator
    {
        public int Count { get; private set; }

        public Task CreateForAcceptedComplaintAsync(long complaintId, long bookingId, decimal amount,
            DateTimeOffset requestedAtUtc, CancellationToken cancellationToken)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryProposalStore(PartialRefundProposal proposal) : IPartialRefundProposalStore
    {
        private PartialRefundProposal _proposal = proposal;

        public Task<PartialRefundProposal?> FindAsync(long complaintId, CancellationToken cancellationToken) =>
            Task.FromResult<PartialRefundProposal?>(complaintId == _proposal.ComplaintId ? _proposal : null);

        public Task<bool> TryCloseAsync(long complaintId, PostTripComplaintStatus outcome,
            DateTimeOffset respondedAtUtc, CancellationToken cancellationToken)
        {
            if (complaintId != _proposal.ComplaintId
                || _proposal.Status != PostTripComplaintStatus.AwaitingTravelerResponse)
                return Task.FromResult(false);

            _proposal = _proposal with { Status = outcome };
            return Task.FromResult(true);
        }

        public Task<bool> TryCounterAsync(long complaintId, long travelerUserId, decimal requestedAmount,
            string reason, DateTimeOffset counteredAtUtc, DateTimeOffset operatorReconsiderationDeadlineUtc,
            CancellationToken cancellationToken)
        {
            if (complaintId != _proposal.ComplaintId || _proposal.Status != PostTripComplaintStatus.AwaitingTravelerResponse ||
                _proposal.Round != 1) return Task.FromResult(false);
            _proposal = _proposal with { Status = PostTripComplaintStatus.AwaitingOperatorReconsideration,
                OperatorReconsiderationDeadlineUtc = operatorReconsiderationDeadlineUtc };
            return Task.FromResult(true);
        }

        public Task<bool> TryDeclineFinallyAsync(long complaintId, long travelerUserId, string reason,
            DateTimeOffset declinedAtUtc, CancellationToken cancellationToken)
        {
            if (complaintId != _proposal.ComplaintId || _proposal.Status != PostTripComplaintStatus.AwaitingTravelerResponse ||
                _proposal.Round != 2) return Task.FromResult(false);
            _proposal = _proposal with { Status = PostTripComplaintStatus.Declined };
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<PartialRefundProposal>> ListOverdueAsync(DateTimeOffset nowUtc, int maxCount,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PartialRefundProposal>>([]);
    }
}
