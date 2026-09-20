using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.SignOut;

namespace TripMate.Application.Features.Authentication.WebSignOut;

public sealed record WebSignOutAllCommand(
    string? RawRefreshToken,
    string? TraceId = null,
    string? ClientIp = null) : IRequest<Result<bool>>;

public sealed class WebSignOutAllCommandHandler(IRequestHandler<SignOutAllCommand, Result<bool>> signOutAllHandler)
    : IRequestHandler<WebSignOutAllCommand, Result<bool>>
{
    public Task<Result<bool>> Handle(WebSignOutAllCommand request, CancellationToken cancellationToken) =>
        signOutAllHandler.Handle(
            new SignOutAllCommand(
                request.RawRefreshToken,
                "Web",
                request.TraceId,
                request.ClientIp),
            cancellationToken);
}