using FluentValidation;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Operator.Profile.Common;
using TripMate.Application.Features.Operator.Profile.Get;
using TripMate.Application.Features.Operator.Profile.Update;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-34 Update Tour Operator Profile.</summary>
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/profile")]
public sealed class OperatorProfileController(
    ISender sender,
    IValidator<UpdateOperatorProfileCommand> validator) : ApiControllerBase(sender)
{
    private const int MaxRequestBytes = 6 * 1024 * 1024;

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(OperatorProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorProfileQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(typeof(UpdateOperatorProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Update(
        [FromForm] UpdateOperatorProfileRequest request,
        CancellationToken cancellationToken)
    {
        var fieldErrors = new List<KeyValuePair<string, string[]>>();
        if (!TryParseRemoveLogo(request.RemoveLogo, out bool removeLogo))
        {
            fieldErrors.Add(new(nameof(UpdateOperatorProfileRequest.RemoveLogo),
                [OperatorProfileErrorCodes.RequestInvalid]));
        }

        var command = new UpdateOperatorProfileCommand(
            request.BusinessName ?? string.Empty,
            request.BusinessDescription,
            request.BusinessAddress,
            request.ContactPhone,
            request.ContactEmail,
            request.Website,
            await ReadLogoAsync(request.Logo, cancellationToken),
            request.Logo?.ContentType,
            removeLogo);

        var validation = await validator.ValidateAsync(command, cancellationToken);
        fieldErrors.AddRange(validation.Errors
            .GroupBy(error => error.PropertyName)
            .Select(group => new KeyValuePair<string, string[]>(
                group.Key,
                group.Select(error => string.IsNullOrWhiteSpace(error.ErrorCode)
                    ? OperatorProfileErrorCodes.RequestInvalid
                    : error.ErrorCode).ToArray())));

        if (fieldErrors.Count > 0)
        {
            return Problem(
                title: "One or more profile fields are invalid.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["errorCode"] = OperatorProfileErrorCodes.RequestInvalid,
                    ["errors"] = ValidationErrorKeyNormalizer.Normalize(
                        fieldErrors, System.Text.Json.JsonNamingPolicy.CamelCase),
                });
        }

        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    private static bool TryParseRemoveLogo(string? value, out bool removeLogo)
    {
        removeLogo = false;
        return string.IsNullOrWhiteSpace(value) || bool.TryParse(value.Trim(), out removeLogo);
    }

    /// <summary>Null when no file was sent. An empty array marks an unreadable or oversized file: it is not
    /// buffered, and the validator reports it as MSG19.</summary>
    private static async Task<byte[]?> ReadLogoAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return null;
        }

        if (file.Length is <= 0 or > UpdateOperatorProfileCommandValidator.MaxLogoBytes)
        {
            return [];
        }

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream((int)file.Length);
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}