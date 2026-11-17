using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.ServiceProvider))]
[AuthorizationProblemDetails(ProviderRequestErrors.Forbidden, "Authentication is required.",
    ProviderRequestErrors.Forbidden, "An active Service Provider account is required.")]
[ProviderRequestValidationExceptionFilter]
[Route("api/v1/provider/service-booking-requests")]
public sealed class ServiceProviderRequestsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListServiceProviderRequests query, CancellationToken token)
    {
        var result = await Sender.Send(query, token);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detail(long id, CancellationToken token)
    {
        var result = await Sender.Send(new GetServiceProviderRequest(id), token);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
    }

    [HttpPost("{id:long}/decision")]
    public async Task<IActionResult> Decision(long id, [FromBody] ProviderDecisionBody body,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey, CancellationToken token)
    {
        var result = await Sender.Send(new DecideServiceProviderRequest(id, body.Decision, body.Reason,
            body.RowVersion, idempotencyKey), token);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
    }

    [HttpPost("{id:long}/outcome")]
    public async Task<IActionResult> Outcome(long id, [FromBody] ProviderOutcomeBody body,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey, CancellationToken token)
    {
        var result = await Sender.Send(new RecordServiceProviderOutcome(id, body.Outcome, body.ActualAmount,
            body.RowVersion, idempotencyKey), token);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
    }

    private ActionResult RequestFailure(Result result)
    {
        var status = result.ErrorCode switch
        {
            ProviderRequestErrors.Forbidden => StatusCodes.Status403Forbidden,
            ProviderRequestErrors.NotFound => StatusCodes.Status404NotFound,
            ProviderRequestErrors.InvalidState or ProviderRequestErrors.VersionConflict
                or ProviderRequestErrors.IdempotencyMismatch => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Problem(title: result.ErrorMessage, statusCode: status,
            extensions: new Dictionary<string, object?> { ["errorCode"] = result.ErrorCode });
    }
}

public sealed record ProviderDecisionBody(string Decision, string? Reason, string RowVersion);
public sealed record ProviderOutcomeBody(string Outcome, decimal? ActualAmount, string RowVersion);

[AttributeUsage(AttributeTargets.Class)]
public sealed class ProviderRequestValidationExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not TripMate.Application.Common.Exceptions.ValidationException exception) return;
        var problem = new ValidationProblemDetails(ValidationErrorKeyNormalizer.Normalize(
            exception.Errors, System.Text.Json.JsonNamingPolicy.CamelCase))
        {
            Title = "The service booking request is invalid.",
            Status = StatusCodes.Status400BadRequest
        };
        problem.Extensions["errorCode"] = ProviderRequestErrors.InvalidInput;
        var response = new BadRequestObjectResult(problem);
        response.ContentTypes.Add("application/problem+json");
        context.Result = response;
        context.ExceptionHandled = true;
    }
}