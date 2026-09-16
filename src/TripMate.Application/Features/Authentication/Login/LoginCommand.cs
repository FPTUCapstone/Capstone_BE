using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.Login;

public record LoginCommand(string Email, string Password) : IRequest<Result<AuthResponseDto>>
{
    public static LoginCommand ForMobile(string email, string password) =>
        new(email, password) { IsMobileEndpoint = true };

    // Server-selected entry contract; not part of any client request body.
    internal bool AdministratorOnly { get; init; }

    // Set only by POST /api/v1/auth/login. Web password endpoints deliberately leave this false.
    internal bool IsMobileEndpoint { get; init; }
}