using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.PaymentChannels.GetChannel;
using TripMate.Application.Features.Operator.PaymentChannels.Submit;
using TripMate.Application.Features.Operator.PaymentChannels.Verify;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.TourOperator))]
[Authorize(Policy = global::TripMate.Api.Authorization.OperatorContractWorkspacePolicies.Workspace)]
[Route("api/v1/operator/payment-channel")]
public sealed class OperatorPaymentChannelController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> GetChannel(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorPaymentChannelQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    public sealed record SubmitCredentialsRequest(
        string ClientId,
        string ApiKey,
        string ChecksumKey,
        string BeneficiaryAccountNumber,
        string BeneficiaryAccountName,
        string BeneficiaryBankCode);

    [HttpPut]
    public async Task<IActionResult> SubmitCredentials(
        [FromBody] SubmitCredentialsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new SubmitPaymentChannelCredentialsCommand(
            request.ClientId,
            request.ApiKey,
            request.ChecksumKey,
            request.BeneficiaryAccountNumber,
            request.BeneficiaryAccountName,
            request.BeneficiaryBankCode),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("verify")]
    public async Task<IActionResult> Verify(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new VerifyPaymentChannelCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}
