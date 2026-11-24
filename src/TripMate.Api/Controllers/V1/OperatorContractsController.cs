using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using TripMate.Api.Common;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Contracts;
using TripMate.Application.Features.Operator.Contracts.Common;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = "TourOperator")]
[Route("api/v1/operator/contracts")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OperatorContractsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var result = await Sender.Send(new GetCurrentOperatorContractQuery(), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}/documents/{kind}")]
    public async Task<IActionResult> Document(long id, string kind, CancellationToken ct)
    {
        var result = await Sender.Send(new DownloadOperatorContractQuery(id, kind), ct);
        if (result.IsFailure) return HandleFailure(result);
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentDisposition = $"inline; filename=\"{result.Value.FileName}\"";
        return File(result.Value.Bytes, "application/pdf");
    }

    [HttpPost("{id:long}/prepare")]
    [Consumes("application/json")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Prepare(long id, [FromBody] PrepareOperatorContractInput input, CancellationToken ct)
    {
        var result = await Sender.Send(new PrepareOperatorContractCommand(id, input), ct);
        return result.IsSuccess ? Accepted(result.Value) : HandleFailure(result);
    }

    [HttpPost("{id:long}/signing-challenges")]
    [Consumes("application/json")]
    [RequestSizeLimit(16 * 1024)]
    [EnableRateLimiting(OperatorContractSigningRateLimiter.PolicyName)]
    public async Task<IActionResult> Challenge(long id, [FromBody] OperatorContractChallengeInput input, CancellationToken ct)
    {
        var result = await Sender.Send(new CreateOperatorContractChallengeCommand(id, input), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{id:long}/sign")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(600 * 1024)]
    public async Task<IActionResult> Sign(long id, [FromForm] OperatorContractSignForm form, CancellationToken ct)
    {
        if (form.Signature is null || form.Signature.Length <= 0)
            return HandleFailure(Result.Failure("contract.signature_invalid", "Draw a signature before submitting."));
        if (form.Signature.Length > 512 * 1024)
            return HandleFailure(Result.Failure("contract.signature_too_large", "The signature capture is too large."));
        await using var stream = form.Signature.OpenReadStream();
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > 512 * 1024) return HandleFailure(Result.Failure("contract.signature_too_large", "The signature capture is too large."));
            bytes.Write(buffer, 0, read);
        }
        var result = await Sender.Send(new SignOperatorContractCommand(id, new(form.ExpectedContentSha256, form.RowVersion,
            form.SignerName, form.AcceptedTerms, form.ConsentVersion, form.ChallengeId, form.Otp, form.IdempotencyKey, bytes.ToArray())), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}

public sealed class OperatorContractSignForm
{
    public string ExpectedContentSha256 { get; set; } = string.Empty;
    public string RowVersion { get; set; } = string.Empty;
    public string SignerName { get; set; } = string.Empty;
    public bool AcceptedTerms { get; set; }
    public string ConsentVersion { get; set; } = string.Empty;
    public Guid ChallengeId { get; set; }
    public string Otp { get; set; } = string.Empty;
    public Guid IdempotencyKey { get; set; }
    public IFormFile? Signature { get; set; }
}