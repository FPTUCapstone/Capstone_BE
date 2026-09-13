using MediatR;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.Register;

public record RegisterTravelerCommand(
    string Email,
    string Password,
    string FullName,
    string? PhoneNumber,
    bool AcceptedTerms,
    string FirebaseIdToken)
    : IRequest<Result<RegisterTravelerResponse>>;
