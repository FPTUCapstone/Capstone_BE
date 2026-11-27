using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.OperatorPaymentChannels.Common;

namespace TripMate.Application.Features.Admin.OperatorPaymentChannels.List;

public sealed record ListOperatorPaymentChannelsQuery(
    string? StatusFilter,
    int Page,
    int PageSize)
    : IRequest<Result<AdminChannelListDto>>;
