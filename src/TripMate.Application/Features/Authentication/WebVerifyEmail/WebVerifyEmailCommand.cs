using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.WebVerifyEmail;

public sealed record WebVerifyEmailCommand(string FirebaseIdToken) : IRequest<Result<WebVerifyEmailResponse>>;
public sealed record WebVerifyEmailResponse(bool EmailVerified);