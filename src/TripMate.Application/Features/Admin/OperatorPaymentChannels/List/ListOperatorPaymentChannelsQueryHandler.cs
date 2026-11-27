using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.OperatorPaymentChannels.Common;
using TripMate.Application.Features.Operator.PaymentChannels.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.OperatorPaymentChannels.List;

public sealed class ListOperatorPaymentChannelsQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser)
    : IRequestHandler<ListOperatorPaymentChannelsQuery, Result<AdminChannelListDto>>
{
    public async Task<Result<AdminChannelListDto>> Handle(
        ListOperatorPaymentChannelsQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.Role != nameof(UserRole.Administrator))
        {
            return Result.Failure<AdminChannelListDto>(
                PaymentChannelErrorCodes.Forbidden, "Administrator access is required.");
        }

        var query = db.OperatorPaymentChannels.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.StatusFilter))
            query = query.Where(c => c.Status == request.StatusFilter);

        var total = await query.CountAsync(cancellationToken);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var page = Math.Max(1, request.Page);

        var items = await query
            .OrderByDescending(c => c.UpdatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new AdminChannelDto(
                c.Id,
                c.OpaqueId,
                c.TourOperatorUserId,
                null,
                c.Status,
                c.ChannelVersion,
                c.BeneficiaryAccountNumber == null ? null : "••••" + c.BeneficiaryAccountNumber.Substring(c.BeneficiaryAccountNumber.Length - 4),
                c.BeneficiaryAccountName,
                c.BeneficiaryBankCode,
                c.WebhookRegisteredAtUtc,
                c.ConnectedAtUtc,
                c.LastVerifiedAtUtc,
                c.CreatedAtUtc,
                c.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return Result.Success(new AdminChannelListDto(items, total, page, pageSize));
    }
}