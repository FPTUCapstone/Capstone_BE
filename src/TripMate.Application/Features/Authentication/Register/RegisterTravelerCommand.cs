using MediatR;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.Register;

public record RegisterTravelerCommand(string Email, string Password, string FullName)
    : IRequest<Result<AuthResponseDto>>;
