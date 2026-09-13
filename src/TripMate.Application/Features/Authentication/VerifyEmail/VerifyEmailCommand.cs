using MediatR;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.VerifyEmail;

public record VerifyEmailCommand(string FirebaseIdToken)
    : IRequest<Result<VerifyEmailResponse>>;
