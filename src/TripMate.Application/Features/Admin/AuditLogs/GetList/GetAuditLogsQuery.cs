using MediatR;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.AuditLogs.GetList;

public record GetAuditLogsQuery(
    string? Keyword = null,
    string? ActionType = null,
    UserRole? ActorRole = null,
    string? AffectedEntity = null,
    DateTimeOffset? FromDateUtc = null,
    DateTimeOffset? ToDateUtc = null,
    int PageNumber = 1,
    int PageSize = 20)
    : IRequest<Result<PaginatedList<AuditLogSummaryDto>>>;
