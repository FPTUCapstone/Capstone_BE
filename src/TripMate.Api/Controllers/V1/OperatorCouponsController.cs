using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Coupons.Common;
using TripMate.Application.Features.Coupons.Create;
using TripMate.Application.Features.Coupons.Get;
using TripMate.Application.Features.Coupons.GetEligibleTours;
using TripMate.Application.Features.Coupons.Lifecycle;
using TripMate.Application.Features.Coupons.List;
using TripMate.Application.Features.Coupons.Update;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/coupons")]
public sealed class OperatorCouponsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OperatorCouponResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ListOperatorCouponsQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{couponId:long}")]
    [ProducesResponseType(typeof(OperatorCouponResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long couponId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorCouponQuery(couponId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("eligible-tours")]
    [ProducesResponseType(typeof(IReadOnlyList<EligibleCouponTourResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetEligibleTours(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetEligibleCouponToursQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateCouponResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(CreateCouponCommand command, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : HandleFailure(result);
    }

    [HttpPut("{couponId:long}")]
    [ProducesResponseType(typeof(OperatorCouponResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(long couponId, UpdateCouponCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command with { CouponId = couponId }, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{couponId:long}/deactivate")]
    [ProducesResponseType(typeof(OperatorCouponResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(long couponId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new SetCouponActivationCommand(couponId, false), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{couponId:long}/activate")]
    [ProducesResponseType(typeof(OperatorCouponResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Activate(long couponId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new SetCouponActivationCommand(couponId, true), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}
