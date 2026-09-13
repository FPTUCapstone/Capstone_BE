using MediatR;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.GoogleAuth;

// IdToken is nullable so a missing body token binds without triggering the [ApiController]
// implicit-Required auto-400 — the controller then answers with a ProblemDetails 400 carrying
// the stable AUTH_TOKEN_MISSING errorCode (UC-04 §7.3). The validator still rejects empties.
public record GoogleAuthCommand(string? IdToken)
    : IRequest<Result<GoogleAuthResponse>>;
