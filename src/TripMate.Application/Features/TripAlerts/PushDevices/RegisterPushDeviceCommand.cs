using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TripAlerts.PushDevices;

public sealed record RegisterPushDeviceCommand(
    long TravelerUserId,
    Guid InstallationId,
    string Token) : IRequest<Result>;