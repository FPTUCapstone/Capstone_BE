using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.ServiceProviderRequests;

public sealed record ListServiceProviderRequests(
    string? Status = null, DateOnly? RequestedDateFrom = null, DateOnly? RequestedDateTo = null,
    string? Keyword = null, int PageNumber = 1, int PageSize = 20)
    : IRequest<Result<ProviderRequestPage>>;

public sealed record GetServiceProviderRequest(long Id) : IRequest<Result<ProviderRequestDetail>>;
public sealed record DecideServiceProviderRequest(long Id, string Decision, string? Reason,
    string RowVersion, Guid IdempotencyKey) : IRequest<Result<ProviderRequestDetail>>;
public sealed record RecordServiceProviderOutcome(long Id, string Outcome, decimal? ActualAmount,
    string RowVersion, Guid IdempotencyKey) : IRequest<Result<ProviderRequestDetail>>;

public sealed record ProviderRequestPage(IReadOnlyList<ProviderRequestSummary> Items,
    int TotalCount, int PageNumber, int PageSize);

public sealed record ProviderRequestSummary(long Id, string RequestCode, long ServiceId,
    string ServiceName, string? Option, DateOnly RequestedDate, string RequestedTime,
    int Quantity, string TravelerName, string Status, DateTimeOffset? SubmittedAtUtc, string RowVersion);

public sealed record ProviderRequestDetail(long Id, string RequestCode, long ServiceId,
    string ServiceName, string? Option, long? PoiId, DateOnly RequestedDate, string RequestedTime,
    DateTimeOffset StartAtUtc, DateTimeOffset? EndAtUtc, int Quantity, decimal EstimatedAmount,
    decimal? ActualAmount, decimal? CommissionAmount, string TravelerName, string? TravelerEmail,
    string? TravelerPhone, string? SpecialRequest, string Status, DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ResponseDeadlineUtc, DateTimeOffset OutcomeAvailableAtUtc,
    DateTimeOffset AutoCompleteAtUtc, string? RejectionReason, DateTimeOffset? RespondedAtUtc,
    DateTimeOffset? OutcomeAtUtc, bool IsAutoCompleted, long? ItineraryId, string RowVersion)
{
    public static ProviderRequestDetail From(ServiceBooking booking, User traveler) => new(
        booking.Id, $"SR-{booking.Id}", booking.ServiceId, booking.Service.Name, booking.ServiceOption, booking.Service.PoiId,
        booking.RequestedDate, booking.StartAtUtc.ToOffset(ServiceBooking.VietnamOffset).ToString("HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture),
        booking.StartAtUtc, booking.EndAtUtc, booking.Quantity, booking.TotalPrice, booking.ActualAmount,
        booking.CommissionAmount, booking.ContactName ?? traveler.FullName, booking.ContactEmail ?? traveler.Email,
        booking.ContactPhone ?? traveler.PhoneNumber, booking.SpecialRequest,
        booking.Status, booking.RequestSubmittedAtUtc, booking.ResponseDeadlineUtc,
        booking.OutcomeAvailableAtUtc, booking.AutoCompleteAtUtc, booking.RejectionReason,
        booking.RespondedAtUtc, booking.OutcomeAtUtc, booking.IsAutoCompleted, booking.ItineraryId,
        Convert.ToBase64String(booking.RowVersion));

    public ProviderRequestSummary ToSummary() => new(Id, RequestCode, ServiceId, ServiceName,
        Option, RequestedDate, RequestedTime, Quantity, TravelerName, Status, SubmittedAtUtc, RowVersion);
}

public interface IServiceProviderRequestLock
{
    Task AcquireOperationAsync(long actorUserId, Guid key, CancellationToken cancellationToken);
    Task AcquireBookingAsync(long bookingId, CancellationToken cancellationToken);
    Task AcquireAvailabilityAsync(long serviceId, DateTimeOffset startsAt, CancellationToken cancellationToken);
}

public static class ProviderRequestErrors
{
    public const string Forbidden = "ServiceProviderRequest.Forbidden";
    public const string NotFound = "ServiceProviderRequest.NotFound";
    public const string InvalidInput = "ServiceProviderRequest.InvalidInput";
    public const string InvalidState = "ServiceProviderRequest.InvalidState";
    public const string VersionConflict = "ServiceProviderRequest.VersionConflict";
    public const string IdempotencyMismatch = "ServiceProviderRequest.IdempotencyMismatch";
}