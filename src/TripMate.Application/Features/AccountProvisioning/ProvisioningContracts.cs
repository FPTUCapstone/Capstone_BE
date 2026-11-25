using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.AccountProvisioning;

public sealed record CreateStaffAccountCommand(string FullName, string Email, string PhoneNumber)
    : IRequest<Result<ProvisionedAccountDto>>;

public sealed record CreateServiceProviderAccountCommand(string FullName, string Email, string PhoneNumber,
    string BusinessName, string Category, IReadOnlyList<long> LinkedPoiIds)
    : IRequest<Result<ProvisionedAccountDto>>;

public sealed record GetAvailableProviderPoisQuery(string? Category = null)
    : IRequest<Result<IReadOnlyList<AvailableProviderPoiDto>>>;

public sealed record AvailableProviderPoiDto(long Id, string Name, string Category);

public sealed record ProvisionedAccountDto(long UserId, string FullName, string Email, string PhoneNumber,
    UserRole Role, AccountStatus Status, bool MustChangePassword, long? ServiceProviderId,
    IReadOnlyList<long> LinkedPoiIds, EmailDeliveryStatus EmailDeliveryStatus);

public static class ProvisioningErrors
{
    public const string Forbidden = "MSG126";
    public const string Unavailable = "MSG127";
    public const string Required = "MSG01";
    public const string Invalid = "MSG143";
    public const string DuplicateEmail = "MSG05";
    public const string InvalidPoi = "MSG34";
    public const string PoiAlreadyLinked = "MSG202";
    public const string DuplicatePhone = "MSG_PHONE_DUP";
    public const string PasswordChangeRequired = "auth.password_change_required";
}