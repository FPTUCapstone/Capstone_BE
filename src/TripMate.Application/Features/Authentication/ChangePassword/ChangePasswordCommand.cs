using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.ChangePassword;

/// <summary>
/// UC-07: SubjectUserId is assembled by authenticated transport code (the validated access
/// token's subject claim), never a client-controlled body value.
/// </summary>
public sealed record ChangePasswordCommand(
    string CurrentPassword,
    string NewPassword,
    long SubjectUserId) : IRequest<Result<AuthResponseDto>>;