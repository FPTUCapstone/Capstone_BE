using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Providers.CommissionStatements;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.ServiceProvider))]
[Route("api/v1/provider/commission-statements")]
public sealed class ServiceProviderStatementsController(
    ISender sender,
    ProviderCommissionQueryHandler queryHandler,
    ReportCommissionPaymentCommandHandler reportHandler,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? period,
        [FromQuery] string? keyword,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var query = new GetProviderStatementsQuery(
            CurrentUserId: currentUserService.UserId.Value,
            Status: status,
            Period: period,
            Keyword: keyword,
            PageNumber: pageNumber,
            PageSize: pageSize);

        var result = await queryHandler.HandleAsync(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detail(long id, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var query = new GetProviderStatementDetailQuery(id, currentUserService.UserId.Value);
        var result = await queryHandler.HandleAsync(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
    }

    [HttpPost("{id:long}/report-payment")]
    public async Task<IActionResult> ReportPayment(
        long id,
        [FromBody] ReportCommissionPaymentBody body,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var command = new ReportCommissionPaymentCommand(
            StatementId: id,
            CurrentUserId: currentUserService.UserId.Value,
            BankTransferReference: body.BankTransferReference);

        var result = await reportHandler.HandleAsync(command, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : RequestFailure(result);
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

public sealed record ReportCommissionPaymentBody(string BankTransferReference);