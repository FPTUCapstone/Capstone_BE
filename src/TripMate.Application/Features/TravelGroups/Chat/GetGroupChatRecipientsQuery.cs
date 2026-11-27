using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat;

public sealed record GetGroupChatRecipientsQuery(long GroupId) : IRequest<IReadOnlyList<string>>;

public sealed class GetGroupChatRecipientsQueryHandler(IApplicationDbContext db)
    : IRequestHandler<GetGroupChatRecipientsQuery, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> Handle(GetGroupChatRecipientsQuery request, CancellationToken cancellationToken)
    {
        var ids = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == request.GroupId && m.Status == GroupMemberStatus.Active
                && m.User.Status == AccountStatus.Active)
            .Select(m => m.UserId).Distinct().ToListAsync(cancellationToken);
        return ids.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
    }
}