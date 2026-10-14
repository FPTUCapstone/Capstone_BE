using FluentValidation;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.RegisterOperator;
using TripMate.Application.Features.Operator.Application.GetCurrent;
using TripMate.Application.Features.Operator.Application.Resubmit;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = "TourOperator")]
[Route("api/v1/operator/application")]
public sealed class OperatorApplicationController(
    ISender sender,
    IValidator<ResubmitOperatorApplicationCommand> validator) : ApiControllerBase(sender)
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(OperatorApplicationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorApplicationQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("resubmit")]
    [EnableRateLimiting(OperatorRegistrationRateLimiter.PolicyName)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(32 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 32 * 1024 * 1024)]
    [ProducesResponseType(typeof(ResubmitOperatorApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Resubmit(
        [FromForm] ResubmitOperatorApplicationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SupportingDocuments?.Count >
            ResubmitOperatorApplicationCommandValidator.MaxSupportingDocuments)
        {
            return FieldProblem("supportingDocuments", AuthErrorCodes.RequestInvalid);
        }

        var command = new ResubmitOperatorApplicationCommand(
            request.CompanyName ?? string.Empty,
            request.BusinessLicenseNo ?? string.Empty,
            request.TaxCode ?? string.Empty,
            request.ContactPerson ?? string.Empty,
            request.BusinessAddress,
            request.ContactPhone,
            await ReadDocumentAsync(request.BusinessLicenseDocument, cancellationToken),
            request.SupportingDocuments is null
                ? null
                : await Task.WhenAll(request.SupportingDocuments.Select(file =>
                    ReadRequiredDocumentAsync(file, cancellationToken))));

        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            var errors = ValidationErrorKeyNormalizer.Normalize(
                validation.Errors
                    .GroupBy(error => error.PropertyName)
                    .Select(group => new KeyValuePair<string, string[]>(
                        group.Key,
                        group.Select(error => string.IsNullOrWhiteSpace(error.ErrorCode)
                            ? AuthErrorCodes.RequestInvalid
                            : error.ErrorCode).ToArray())),
                System.Text.Json.JsonNamingPolicy.CamelCase);
            return Problem(
                title: "One or more application fields are invalid.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["errorCode"] = AuthErrorCodes.RequestInvalid,
                    ["errors"] = errors,
                });
        }

        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    private IActionResult FieldProblem(string field, string code) =>
        Problem(
            title: "One or more application fields are invalid.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["errorCode"] = AuthErrorCodes.RequestInvalid,
                ["errors"] = new Dictionary<string, string[]> { [field] = [code] },
            });

    private static async Task<OperatorRegistrationDocument?> ReadDocumentAsync(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null) return null;
        if (file.Length is <= 0 or > ResubmitOperatorApplicationCommandValidator.MaxFileSizeBytes)
        {
            return new OperatorRegistrationDocument(file.FileName, file.ContentType, []);
        }

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream((int)file.Length);
        await stream.CopyToAsync(buffer, cancellationToken);
        return new OperatorRegistrationDocument(file.FileName, file.ContentType, buffer.ToArray());
    }

    private static async Task<OperatorRegistrationDocument> ReadRequiredDocumentAsync(
        IFormFile file,
        CancellationToken cancellationToken) =>
        (await ReadDocumentAsync(file, cancellationToken))!;
}