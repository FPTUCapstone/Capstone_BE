using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.SignOut;

namespace TripMate.Application.Features.Authentication.WebSignOut;

public sealed record WebSignOutCommand(
    string? RawRefreshToken,
    string? TraceId = null,
    string? ClientIp = null) : IRequest<Result<bool>>;

public sealed class WebSignOutCommandHandler(IRequestHandler<SignOutCommand, Result<bool>> signOutHandler)
    : IRequestHandler<WebSignOutCommand, Result<bool>>
{
    public Task<Result<bool>> Handle(WebSignOutCommand request, CancellationToken cancellationToken) =>
        signOutHandler.Handle(
            new SignOutCommand(
                request.RawRefreshToken,
                "Web",
                request.TraceId,
                request.ClientIp),
            cancellationToken);
}