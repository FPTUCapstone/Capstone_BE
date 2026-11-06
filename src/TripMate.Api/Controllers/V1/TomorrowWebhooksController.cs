using System.Text;
using System.Text.Json;

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
[Route("api/v1/webhooks/tomorrow")]
[Route("api/v1/weather/webhooks/tomorrow")]
public sealed class TomorrowWebhooksController(
    ISender sender,
    ITomorrowWebhookAuthenticator authenticator,
    IOptions<TomorrowIoOptions> options,
    ILogger<TomorrowWebhooksController> logger)
    : ApiControllerBase(sender)
{
    private readonly TomorrowIoOptions _options = options.Value;

    [HttpPost("alerts")]
    [HttpPost("")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
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

        // 2. Request body size check: a declared length is refused unread, and a chunked body
        // is read only up to the limit.
        if (Request.ContentLength > _options.MaxPayloadSizeBytes)
        {
            return PayloadTooLarge();
        }

        string? rawBody = await ReadBodyWithinLimitAsync(cancellationToken);
        if (rawBody is null)
        {
            return PayloadTooLarge();
        }

        // 3. Signature verification
        string? signatureHeader = Request.Headers["X-Signature"].FirstOrDefault();

        if (!authenticator.ValidateSignature(signatureHeader, null, out string? failureReason))
        {
            logger.LogWarning("Tomorrow.io webhook authentication rejected: {Reason}", failureReason);
            return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Webhook Authentication Failed",
                // The reason stays in the log; an unauthenticated caller learns nothing about why.
                Detail = "The webhook signature could not be verified."
            });
        }

        // 4. Only an authenticated sender learns whether its body was valid
        if (!IsJson(rawBody))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Payload",
                Detail = "The request body must be a JSON document."
            });
        }

        // 5. Enqueue into durable inbox
        var result = await Sender.Send(new ReceiveTomorrowAlertCommand(rawBody), cancellationToken);
        if (result.IsFailure)
        {
            return HandleFailure(result);
        }

        return StatusCode(StatusCodes.Status202Accepted, new
        {
            inboxId = result.Value.InboxId,
            status = result.Value.Status,
            isDuplicate = result.Value.IsDuplicate
        });
    }

    private async Task<string?> ReadBodyWithinLimitAsync(CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (body.Length + read > _options.MaxPayloadSizeBytes)
            {
                return null;
            }

            body.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
    }

    private ObjectResult PayloadTooLarge() => StatusCode(StatusCodes.Status413PayloadTooLarge, new ProblemDetails
    {
        Status = StatusCodes.Status413PayloadTooLarge,
        Title = "Payload Too Large",
        Detail = $"Payload exceeds maximum allowed size of {_options.MaxPayloadSizeBytes} bytes."
    });

    private static bool IsJson(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
