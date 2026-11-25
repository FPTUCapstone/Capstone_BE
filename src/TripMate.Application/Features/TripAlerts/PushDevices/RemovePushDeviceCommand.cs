using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TripAlerts.PushDevices;

public sealed record RemovePushDeviceCommand(
    long UserId,
    Guid InstallationId) : IRequest<Result>;