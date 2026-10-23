using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Delete;

public sealed record DeleteOperatorTourCommand(long TourId) : IRequest<Result>;