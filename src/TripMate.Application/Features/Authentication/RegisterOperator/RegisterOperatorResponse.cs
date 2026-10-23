namespace TripMate.Application.Features.Authentication.RegisterOperator;

public sealed record RegisterOperatorResponse(
    long UserId,
    string ApplicationStatus,
    string MessageCode);