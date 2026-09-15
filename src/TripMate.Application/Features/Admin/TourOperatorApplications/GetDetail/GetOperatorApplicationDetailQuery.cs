using MediatR;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.GetDetail;

public record GetOperatorApplicationDetailQuery(long UserId) : IRequest<Result<TourOperatorApplicationDetailDto>>;
