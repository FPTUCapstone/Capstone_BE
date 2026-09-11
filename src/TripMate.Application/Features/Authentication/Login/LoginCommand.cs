using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.Login;

public record LoginCommand(string Email, string Password) : IRequest<Result<AuthResponseDto>>;