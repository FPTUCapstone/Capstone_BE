using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.AuditLogs.GetDetail;

public record GetAuditLogDetailQuery(long Id) : IRequest<Result<AuditLogDetailDto>>;