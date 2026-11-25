using FluentValidation;

using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Application.Features.Admin.ContractTemplates;

public sealed record ListContractTemplatesQuery(string? Status, int Page = 1, int PageSize = 20) : IRequest<Result<ContractTemplatePage>>;
public sealed record GetContractTemplateQuery(long Id) : IRequest<Result<ContractTemplateDto>>;
public sealed record CreateContractTemplateCommand(string Title, ContractTemplateUpload Upload) : IRequest<Result<ContractTemplateDto>>;
public sealed record UpdateContractTemplateCommand(long Id, string Title, int ExpectedRevision, string RowVersion, ContractTemplateUpload? Upload) : IRequest<Result<ContractTemplateDto>>;
public sealed record PreviewContractTemplateCommand(long Id, PreviewContractTemplateInput Input) : IRequest<Result<ContractTemplateDto>>;
public sealed record PublishContractTemplateCommand(long Id, PublishContractTemplateInput Input) : IRequest<Result<ContractTemplateDto>>;
public sealed record CreateContractTemplateVersionCommand(long Id, string? Title, string RowVersion) : IRequest<Result<ContractTemplateDto>>;
public sealed record DownloadContractTemplateQuery(long Id, string Kind) : IRequest<Result<ContractTemplateDocument>>;

public sealed class CreateContractTemplateCommandValidator : AbstractValidator<CreateContractTemplateCommand>
{
    public CreateContractTemplateCommandValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200).WithErrorCode(ContractTemplateErrors.Validation);
        RuleFor(x => x.Upload).NotNull().WithErrorCode(ContractTemplateErrors.Validation);
    }
}

public sealed class UpdateContractTemplateCommandValidator : AbstractValidator<UpdateContractTemplateCommand>
{
    public UpdateContractTemplateCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200).WithErrorCode(ContractTemplateErrors.Validation);
        RuleFor(x => x.RowVersion).NotNull();
    }
}

public sealed class ContractTemplateRequestHandlers(ContractTemplateWorkflow workflow) :
    IRequestHandler<ListContractTemplatesQuery, Result<ContractTemplatePage>>,
    IRequestHandler<GetContractTemplateQuery, Result<ContractTemplateDto>>,
    IRequestHandler<CreateContractTemplateCommand, Result<ContractTemplateDto>>,
    IRequestHandler<UpdateContractTemplateCommand, Result<ContractTemplateDto>>,
    IRequestHandler<PreviewContractTemplateCommand, Result<ContractTemplateDto>>,
    IRequestHandler<PublishContractTemplateCommand, Result<ContractTemplateDto>>,
    IRequestHandler<CreateContractTemplateVersionCommand, Result<ContractTemplateDto>>,
    IRequestHandler<DownloadContractTemplateQuery, Result<ContractTemplateDocument>>
{
    public Task<Result<ContractTemplatePage>> Handle(ListContractTemplatesQuery request, CancellationToken ct) => workflow.ListAsync(request.Status, request.Page, request.PageSize, ct);
    public Task<Result<ContractTemplateDto>> Handle(GetContractTemplateQuery request, CancellationToken ct) => workflow.GetAsync(request.Id, ct);
    public Task<Result<ContractTemplateDto>> Handle(CreateContractTemplateCommand request, CancellationToken ct) => workflow.CreateAsync(request.Title, request.Upload, ct);
    public Task<Result<ContractTemplateDto>> Handle(UpdateContractTemplateCommand request, CancellationToken ct) => workflow.UpdateAsync(request.Id, request.Title, request.ExpectedRevision, request.RowVersion, request.Upload, ct);
    public Task<Result<ContractTemplateDto>> Handle(PreviewContractTemplateCommand request, CancellationToken ct) => workflow.PreviewAsync(request.Id, request.Input, ct);
    public Task<Result<ContractTemplateDto>> Handle(PublishContractTemplateCommand request, CancellationToken ct) => workflow.PublishAsync(request.Id, request.Input, ct);
    public Task<Result<ContractTemplateDto>> Handle(CreateContractTemplateVersionCommand request, CancellationToken ct) => workflow.CreateVersionAsync(request.Id, request.Title, request.RowVersion, ct);
    public Task<Result<ContractTemplateDocument>> Handle(DownloadContractTemplateQuery request, CancellationToken ct) => workflow.DownloadAsync(request.Id, request.Kind, ct);
}