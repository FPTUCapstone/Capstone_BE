using MediatR;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.GoogleAuth;

public record GoogleAuthCommand(string IdToken)
    : IRequest<Result<GoogleAuthResponse>>;
