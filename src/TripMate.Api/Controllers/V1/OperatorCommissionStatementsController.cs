using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.CommissionStatements;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>Direct commission statements for Tour Operators (spec §10).</summary>
[Authorize(Policy = global::TripMate.Api.Authorization.OperatorContractWorkspacePolicies.Workspace)]
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/commission-statements")]
public sealed class OperatorCommissionStatementsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null, [FromQuery] string? period = null, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new ListDirectCommissionStatementsQuery(pageNumber, pageSize, status, period), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new GetDirectCommissionStatementQuery(id), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>POST /api/v1/operator/commission-statements/{id}/payment-link</summary>
    [HttpPost("{id:long}/payment-link")]
    [ProducesResponseType(typeof(CreateCommissionPaymentLinkResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreatePaymentLink(long id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new CreateCommissionPaymentLinkCommand(id), cancellationToken);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : HandleFailure(result);
    }
}
