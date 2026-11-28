using FluentValidation;

using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Constants;

namespace TripMate.Application.Features.TravelGroups.HostManagement;

public sealed record RenameTravelGroupCommand(long GroupId, long HostUserId, string? Name)
    : IRequest<Result<RenameTravelGroupResponse>>;

public sealed record RenameTravelGroupResponse(long GroupId, string Name);

public sealed record DissolveTravelGroupCommand(long GroupId, long HostUserId) : IRequest<Result>;

public sealed class RenameTravelGroupCommandValidator : AbstractValidator<RenameTravelGroupCommand>
{
    public RenameTravelGroupCommandValidator()
    {
        RuleFor(request => request.GroupId).GreaterThan(0);
        RuleFor(request => request.HostUserId).GreaterThan(0);
        RuleFor(request => request.Name)
            .Cascade(CascadeMode.Stop)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("A travel group name is required.")
            .Must(name => name!.Trim().Length <= TravelGroupConstants.MaxGroupNameLength)
            .WithMessage($"Group name must not exceed {TravelGroupConstants.MaxGroupNameLength} characters.");
    }
}

public sealed class DissolveTravelGroupCommandValidator : AbstractValidator<DissolveTravelGroupCommand>
{
    public DissolveTravelGroupCommandValidator()
    {
        RuleFor(request => request.GroupId).GreaterThan(0);
        RuleFor(request => request.HostUserId).GreaterThan(0);
    }
}