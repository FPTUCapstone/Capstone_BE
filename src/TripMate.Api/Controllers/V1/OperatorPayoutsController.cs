using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.Payouts.BankAccount;
using TripMate.Application.Features.Operator.Payouts.CancelPayout;
using TripMate.Application.Features.Operator.Payouts.GetAvailableBalance;
using TripMate.Application.Features.Operator.Payouts.GetHistory;
using TripMate.Application.Features.Operator.Payouts.GetSettlementMonths;
using TripMate.Application.Features.Operator.Payouts.RequestPayout;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/payouts")]
public sealed class OperatorPayoutsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("settlement-months")]
    public async Task<IActionResult> SettlementMonths([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorSettlementMonthsQuery(year), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("available-balance")]
    public async Task<IActionResult> AvailableBalance(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorAvailableBalanceQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    public async Task<IActionResult> Request(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new RequestPayoutCommand(), cancellationToken);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : HandleFailure(result);
    }

    [HttpGet]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new GetOperatorPayoutHistoryQuery(page, pageSize), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{payoutCode}/cancel")]
    public async Task<IActionResult> Cancel(string payoutCode, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new CancelPayoutCommand(payoutCode), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("bank-account")]
    public async Task<IActionResult> GetBankAccount(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetBeneficiaryBankAccountQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    public sealed record UpsertBankAccountRequest(string BankName, string AccountHolderName, string AccountNumber);

    [HttpPut("bank-account")]
    public async Task<IActionResult> UpsertBankAccount([FromBody] UpsertBankAccountRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new UpsertBeneficiaryBankAccountCommand(request.BankName, request.AccountHolderName, request.AccountNumber),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}