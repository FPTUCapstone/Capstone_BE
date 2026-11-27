using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.CommissionStatements;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Administrator) + "," + nameof(UserRole.Staff))]
[Route("api/v1/admin/commission-statements")]
public sealed class AdminCommissionStatementsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null, [FromQuery] string? period = null, [FromQuery] long? tourOperatorUserId = null,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new ListDirectCommissionStatementsQuery(pageNumber, pageSize, status, period, true, tourOperatorUserId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new GetDirectCommissionStatementQuery(id, true), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    public async Task<IActionResult> Issue(IssueDirectCommissionStatementCommand request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(request, cancellationToken);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : HandleFailure(result);
    }
}
