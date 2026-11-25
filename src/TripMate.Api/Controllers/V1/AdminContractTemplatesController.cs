using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Admin.ContractTemplates;
using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = "Administrator")]
[Route("api/v1/admin/contract-templates")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminContractTemplatesController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> List(string? status, CancellationToken ct, int page = 1, int pageSize = 20)
    {
        var result = await Sender.Send(new ListContractTemplatesQuery(status, page, pageSize), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var result = await Sender.Send(new GetContractTemplateQuery(id), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> Create([FromForm] ContractTemplateForm form, CancellationToken ct)
    {
        var upload = await ReadUpload(form.File, ct);
        if (upload is null) return HandleFailure(TripMate.Application.Common.Models.Result.Failure(ContractTemplateDocumentErrors.InvalidDocument, "A DOCX document is required."));
        var result = await Sender.Send(new CreateContractTemplateCommand(form.Title, upload), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("{id:long}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> Update(long id, [FromForm] ContractTemplateForm form, CancellationToken ct)
    {
        var upload = await ReadUpload(form.File, ct);
        if (form.File is not null && upload is null) return HandleFailure(TripMate.Application.Common.Models.Result.Failure(ContractTemplateDocumentErrors.DocumentTooLarge, "DOCX must not exceed 10 MB."));
        var result = await Sender.Send(new UpdateContractTemplateCommand(id, form.Title, form.ExpectedRevision, form.RowVersion ?? "", upload), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{id:long}/preview")]
    public async Task<IActionResult> Preview(long id, [FromBody] PreviewContractTemplateInput input, CancellationToken ct)
    {
        var result = await Sender.Send(new PreviewContractTemplateCommand(id, input), ct);
        return result.IsSuccess ? Accepted(result.Value) : HandleFailure(result);
    }

    [HttpPost("{id:long}/publish")]
    public async Task<IActionResult> Publish(long id, [FromBody] PublishContractTemplateInput input, CancellationToken ct)
    {
        var result = await Sender.Send(new PublishContractTemplateCommand(id, input), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{id:long}/versions")]
    public async Task<IActionResult> Version(long id, [FromBody] ContractTemplateVersionInput input, CancellationToken ct)
    {
        var result = await Sender.Send(new CreateContractTemplateVersionCommand(id, input.Title, input.RowVersion), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}/documents/{kind}")]
    public async Task<IActionResult> Download(long id, string kind, CancellationToken ct)
    {
        var result = await Sender.Send(new DownloadContractTemplateQuery(id, kind), ct);
        if (result.IsFailure) return HandleFailure(result);
        Response.Headers.XContentTypeOptions = "nosniff";
        if (kind == "preview")
        {
            Response.Headers.ContentDisposition = $"inline; filename=\"{result.Value.FileName}\"";
            return File(result.Value.Bytes, result.Value.ContentType);
        }
        return File(result.Value.Bytes, result.Value.ContentType, result.Value.FileName);
    }

    private static async Task<ContractTemplateUpload?> ReadUpload(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length is <= 0 or > 10 * 1024 * 1024) return null;
        await using var input = file.OpenReadStream();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > 10 * 1024 * 1024) return null;
            output.Write(buffer, 0, read);
        }
        return new ContractTemplateUpload(file.FileName, file.ContentType, output.ToArray());
    }
}

public sealed class ContractTemplateForm
{
    public string Title { get; set; } = "";
    public IFormFile? File { get; set; }
    public int ExpectedRevision { get; set; }
    public string? RowVersion { get; set; }
}
public sealed record ContractTemplateVersionInput(string? Title, string RowVersion);