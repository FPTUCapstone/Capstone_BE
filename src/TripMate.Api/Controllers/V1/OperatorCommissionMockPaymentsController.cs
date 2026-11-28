using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.CommissionStatements;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Policy = global::TripMate.Api.Authorization.OperatorContractWorkspacePolicies.Workspace)]
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/commission-statements")]
public sealed class OperatorCommissionMockPaymentsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("{id:long}/mock-complete")]
    public async Task<IActionResult> Complete(long id, MockCommissionRequest request, CancellationToken ct)
    {
        var result = await Sender.Send(new CompleteMockCommissionPaymentCommand(id, request.PaymentId), ct);
        Response.Headers.CacheControl = "no-store";
        return result.IsSuccess ? Ok(new { success = true }) : HandleFailure(result);
    }
}
public sealed record MockCommissionRequest(long PaymentId);