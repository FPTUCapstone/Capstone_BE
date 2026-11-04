using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.Webhooks.ReceiveTomorrowAlert;
using TripMate.Infrastructure.Weather;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// Authenticated ingress controller for Tomorrow.io weather alerts.
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("api/v1/webhooks/tomorrow")]
[Route("api/v1/weather/webhooks/tomorrow")]
public sealed class TomorrowWebhooksController(
    ISender sender,
    ITomorrowWebhookAuthenticator authenticator,
    IOptions<TomorrowIoOptions> options,
    ILogger<TomorrowWebhooksController> logger)
    : ControllerBase
{
    private readonly TomorrowIoOptions _options = options.Value;

    [HttpPost("alerts")]
    [HttpPost("")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ReceiveAlertAsync(CancellationToken cancellationToken)
    {
        // 1. Content-Type check
        if (Request.ContentType is null || !Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new ProblemDetails
            {
                Status = StatusCodes.Status415UnsupportedMediaType,
                Title = "Unsupported Media Type",
                Detail = "Content-Type must be application/json."
            });
        }

        // 2. Request body size check
        if (Request.ContentLength.HasValue && Request.ContentLength.Value > _options.MaxPayloadSizeBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload Too Large",
                Detail = $"Payload exceeds maximum allowed size of {_options.MaxPayloadSizeBytes} bytes."
            });
        }

        using var reader = new StreamReader(Request.Body);
        string rawBody = await reader.ReadToEndAsync(cancellationToken);

        if (rawBody.Length > _options.MaxPayloadSizeBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload Too Large",
                Detail = $"Payload exceeds maximum allowed size of {_options.MaxPayloadSizeBytes} bytes."
            });
        }

        // 3. Signature verification
        string? signatureHeader = Request.Headers["X-Signature"].FirstOrDefault()
            ?? Request.Headers["Tomorrow-Signature"].FirstOrDefault();

        if (!authenticator.ValidateSignature(rawBody, signatureHeader, null, out string? failureReason))
        {
            logger.LogWarning("Tomorrow.io webhook authentication rejected: {Reason}", failureReason);
            return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Webhook Authentication Failed",
                Detail = failureReason
            });
        }

        // 4. Enqueue into durable inbox
        var command = new ReceiveTomorrowAlertCommand(rawBody, signatureHeader);
        var result = await sender.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            logger.LogError("Failed to persist Tomorrow.io alert into inbox: {ErrorCode} - {ErrorMessage}", result.ErrorCode, result.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Alert Ingestion Failed",
                Detail = result.ErrorMessage ?? "Failed to persist alert into durable inbox."
            });
        }

        return StatusCode(StatusCodes.Status202Accepted, new
        {
            inboxId = result.Value.InboxId,
            status = result.Value.Status,
            isDuplicate = result.Value.IsDuplicate
        });
    }
}
