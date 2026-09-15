using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.Login;

public record LoginCommand(string Email, string Password) : IRequest<Result<AuthResponseDto>>
{
    // Server-selected entry contract; not part of any client request body.
    internal bool AdministratorOnly { get; init; }
}