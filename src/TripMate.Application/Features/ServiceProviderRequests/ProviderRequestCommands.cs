using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.ServiceProviderRequests;

public sealed class DecideServiceProviderRequestHandler(IApplicationDbContext db, ICurrentUserService current,
    ProviderRequestMutationService mutations) : IRequestHandler<DecideServiceProviderRequest, Result<ProviderRequestDetail>>
{
    public async Task<Result<ProviderRequestDetail>> Handle(DecideServiceProviderRequest request, CancellationToken token)
    {
        var actor = await ProviderRequestAccess.ActorAsync(db, current, token);
        if (actor is null) return Result.Failure<ProviderRequestDetail>(ProviderRequestErrors.Forbidden,
            "An active Service Provider account is required.");
        if (!(await new DecideServiceProviderRequestValidator().ValidateAsync(request, token)).IsValid)
            return Result.Failure<ProviderRequestDetail>(ProviderRequestErrors.InvalidInput, "The decision is invalid. A rejection requires a reason.");
        return await mutations.ExecuteAsync(actor.Value, request.Id, request.IdempotencyKey,
            ServiceProviderRequestOperation.Decision, request.Decision, request.Reason?.Trim(), null, request.RowVersion, token);
    }
}

public sealed class RecordServiceProviderOutcomeHandler(IApplicationDbContext db, ICurrentUserService current,
    ProviderRequestMutationService mutations) : IRequestHandler<RecordServiceProviderOutcome, Result<ProviderRequestDetail>>
{
    public async Task<Result<ProviderRequestDetail>> Handle(RecordServiceProviderOutcome request, CancellationToken token)
    {
        var actor = await ProviderRequestAccess.ActorAsync(db, current, token);
        if (actor is null) return Result.Failure<ProviderRequestDetail>(ProviderRequestErrors.Forbidden,
            "An active Service Provider account is required.");
        if (!(await new RecordServiceProviderOutcomeValidator().ValidateAsync(request, token)).IsValid)
            return Result.Failure<ProviderRequestDetail>(ProviderRequestErrors.InvalidInput, "The outcome or actual amount is invalid.");
        return await mutations.ExecuteAsync(actor.Value, request.Id, request.IdempotencyKey,
            ServiceProviderRequestOperation.Outcome, request.Outcome, null, request.ActualAmount, request.RowVersion, token);
    }
}