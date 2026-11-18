using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Features.PointsOfInterest.Deactivate;
using TripMate.Application.Features.PointsOfInterest.Management;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Route("api/v1/admin/pois")]
public sealed class PoiManagementController(ISender sender) : ApiControllerBase(sender)
{
    private static readonly string[] QueryParameterNames =
    [
        nameof(ListManagedPoisQuery.Page), nameof(ListManagedPoisQuery.PageSize),
        nameof(ListManagedPoisQuery.Search), nameof(ListManagedPoisQuery.CategoryId),
        nameof(ListManagedPoisQuery.Status),
    ];

    [HttpGet]
    [TypeFilter(typeof(ManagedPoiReadAuthorizationFilter))]
    [ProducesErrorResponseType(typeof(void))]
    [ProducesResponseType(typeof(ManagedPoiListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(
        [FromQuery] ListManagedPoisQuery query,
        CancellationToken cancellationToken)
    {
        foreach (var entry in Request.Query)
        {
            if (!QueryParameterNames.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (entry.Value.Count != 1)
            {
                ModelState.AddModelError(entry.Key, "Provide this query parameter once.");
            }
            else if ((string.Equals(entry.Key, nameof(ListManagedPoisQuery.Status), StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Key, nameof(ListManagedPoisQuery.CategoryId), StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrWhiteSpace(entry.Value[0]))
            {
                // Nullable model binding would otherwise treat an invalid present value as omitted.
                ModelState.AddModelError(entry.Key, "Provide a valid value or omit this parameter.");
            }
        }
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}/deactivation-references")]
    [Authorize(Roles = nameof(UserRole.Administrator))]
    [ForbiddenProblemDetails(PoiDeactivationErrors.Msg126, PoiDeactivationErrors.Msg126Message)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PoiDeactivationReferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetDeactivationReferences(
        [FromRoute] long id,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetPoiDeactivationReferencesQuery(id), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{id:long}/deactivate")]
    [Authorize(Roles = nameof(UserRole.Administrator))]
    [ForbiddenProblemDetails(PoiDeactivationErrors.Msg126, PoiDeactivationErrors.Msg126Message)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(DeactivatePoiResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Deactivate(
        [FromRoute] long id,
        [FromBody] DeactivatePoiCommand command,
        [FromHeader(Name = "Idempotency-Key")] Guid? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (id != command.PoiId)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Route POI ID does not match request body POI ID.",
                Extensions = { ["errorCode"] = PoiDeactivationErrors.InvalidInput }
            });
        }

        var key = idempotencyKey.HasValue && idempotencyKey.Value != Guid.Empty
            ? idempotencyKey.Value
            : Guid.NewGuid();

        var result = await Sender.Send(command with { IdempotencyKey = key }, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}

// MVC authorization deliberately runs here so no-store also covers challenges,
// forbidden responses and automatic model-binding errors before the action runs.
// The built-in filter uses the existing authentication scheme and policy evaluator.
internal sealed class ManagedPoiReadAuthorizationFilter : AuthorizeFilter
{
    public ManagedPoiReadAuthorizationFilter()
        : base(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireRole(nameof(UserRole.Administrator), nameof(UserRole.Staff))
            .Build())
    {
    }

    public override async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        await base.OnAuthorizationAsync(context);
        if (context.Result is ForbidResult)
        {
            var problem = context.HttpContext.RequestServices
                .GetRequiredService<ProblemDetailsFactory>()
                .CreateProblemDetails(context.HttpContext, StatusCodes.Status403Forbidden,
                    "You do not have permission to access this resource.");
            problem.Extensions["errorCode"] = ManagedPoiErrors.AccessRequired;
            context.Result = new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status403Forbidden,
                ContentTypes = { "application/problem+json" },
            };
        }
    }
}