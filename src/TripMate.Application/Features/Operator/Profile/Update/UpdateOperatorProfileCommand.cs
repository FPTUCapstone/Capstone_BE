using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Profile.Common;

namespace TripMate.Application.Features.Operator.Profile.Update;

/// <summary>UC-34 update. Only editable business-profile fields exist on this command; legal identifiers,
/// approval state, commission, bank account and account email cannot be expressed. <see cref="LogoBytes"/>
/// is null when no file was sent and empty when the file was unreadable or over the size limit (the
/// controller does not buffer an oversized upload).</summary>
public sealed record UpdateOperatorProfileCommand(
    string BusinessName,
    string? BusinessDescription,
    string? BusinessAddress,
    string? ContactPhone,
    string? ContactEmail,
    string? Website,
    byte[]? LogoBytes,
    string? LogoContentType,
    bool RemoveLogo) : IRequest<Result<UpdateOperatorProfileResponse>>;

public sealed record UpdateOperatorProfileResponse(string Code, string Message, OperatorProfileDto Profile);