using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Providers.CommissionStatements;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = "Staff,Administrator")]
[Route("api/v1/admin/commercial-settlements/reconciliation")]
public sealed class AdminCommissionReconciliationController(
    ISender sender,
    AdminReconciliationQueryHandler queryHandler,
    AdminReconciliationCommandHandler commandHandler,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] long? providerId,
        [FromQuery] string? period,
        [FromQuery] string? status,
        [FromQuery] string? keyword,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = new GetReconciliationStatementsQuery(
            ProviderId: providerId,
            Period: period,
            Status: status,
            Keyword: keyword,
            PageNumber: pageNumber,
            PageSize: pageSize);

        var result = await queryHandler.HandleAsync(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detail(long id, CancellationToken cancellationToken)
    {
        var query = new GetReconciliationStatementDetailQuery(id);
        var result = await queryHandler.HandleAsync(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
    }

    [HttpPost("{id:long}/reconcile")]
    public async Task<IActionResult> Reconcile(
        long id,
        [FromBody] ReconcileCommissionReceiptBody? body,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var command = new ConfirmCommissionReceiptCommand(
            StatementId: id,
            StaffUserId: currentUserService.UserId.Value,
            Notes: body?.Notes);

        var result = await commandHandler.HandleAsync(command, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate(
        [FromBody] GenerateCommissionStatementsBody? body,
        CancellationToken cancellationToken)
    {
        var command = new GenerateMonthlyCommissionStatementsCommand(body?.Period);
        var summary = await commandHandler.HandleAsync(command, cancellationToken);
        return Ok(summary);
    }

    [HttpPost("process-overdue")]
    public async Task<IActionResult> ProcessOverdue(CancellationToken cancellationToken)
    {
        var command = new ProcessCommissionOverdueCommand();
        var summary = await commandHandler.HandleAsync(command, cancellationToken);
        return Ok(summary);
    }

    private ActionResult RequestFailure(Result result)
    {
        var status = result.ErrorCode switch
        {
            CommissionStatementErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            CommissionStatementErrorCodes.NotFound => StatusCodes.Status404NotFound,
            CommissionStatementErrorCodes.InvalidState or CommissionStatementErrorCodes.AlreadyPaid => StatusCodes.Status409Conflict,
            CommissionStatementErrorCodes.MissingBankReference or CommissionStatementErrorCodes.InvalidInput => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest
        };

        return Problem(
            title: result.ErrorMessage,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["errorCode"] = result.ErrorCode });
    }
}

public sealed record ReconcileCommissionReceiptBody(string? Notes);
public sealed record GenerateCommissionStatementsBody(string? Period);