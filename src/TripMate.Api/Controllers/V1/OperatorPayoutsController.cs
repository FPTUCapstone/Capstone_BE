using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.Payouts.BankAccount;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/payouts")]
public sealed class OperatorPayoutsController(ISender sender) : ApiControllerBase(sender)
{
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