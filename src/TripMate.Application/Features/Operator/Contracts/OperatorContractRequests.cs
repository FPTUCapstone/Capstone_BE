using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Contracts.Common;

namespace TripMate.Application.Features.Operator.Contracts;

public sealed record GetCurrentOperatorContractQuery : IRequest<Result<OperatorContractDto>>;
public sealed record DownloadOperatorContractQuery(long Id, string Kind) : IRequest<Result<OperatorContractDocument>>;
public sealed record PrepareOperatorContractCommand(long Id, PrepareOperatorContractInput Input) : IRequest<Result<OperatorContractDto>>;
public sealed record CreateOperatorContractChallengeCommand(long Id, OperatorContractChallengeInput Input) : IRequest<Result<OperatorContractChallengeDto>>;
public sealed record SignOperatorContractCommand(long Id, SignOperatorContractInput Input) : IRequest<Result<OperatorContractDto>>;

public sealed class OperatorContractRequestHandlers(OperatorContractWorkflow workflow) :
    IRequestHandler<GetCurrentOperatorContractQuery, Result<OperatorContractDto>>,
    IRequestHandler<DownloadOperatorContractQuery, Result<OperatorContractDocument>>,
    IRequestHandler<PrepareOperatorContractCommand, Result<OperatorContractDto>>,
    IRequestHandler<CreateOperatorContractChallengeCommand, Result<OperatorContractChallengeDto>>,
    IRequestHandler<SignOperatorContractCommand, Result<OperatorContractDto>>
{
    public Task<Result<OperatorContractDto>> Handle(GetCurrentOperatorContractQuery request, CancellationToken ct) => workflow.GetCurrentAsync(ct);
    public Task<Result<OperatorContractDocument>> Handle(DownloadOperatorContractQuery request, CancellationToken ct) => workflow.DownloadAsync(request.Id, request.Kind, ct);
    public Task<Result<OperatorContractDto>> Handle(PrepareOperatorContractCommand request, CancellationToken ct) => workflow.PrepareAsync(request.Id, request.Input, ct);
    public Task<Result<OperatorContractChallengeDto>> Handle(CreateOperatorContractChallengeCommand request, CancellationToken ct) => workflow.CreateChallengeAsync(request.Id, request.Input, ct);
    public Task<Result<OperatorContractDto>> Handle(SignOperatorContractCommand request, CancellationToken ct) => workflow.SignAsync(request.Id, request.Input, ct);
}